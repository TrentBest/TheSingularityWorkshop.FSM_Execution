using System.Security.Cryptography;
using TheSingularityWorkshop.CiAi;

namespace TheSingularityWorkshop.FSM_Execution.GitHubHost;

/// <summary>Explicit host-owned enrollment for one GitHub repository.</summary>
public sealed record GitHubWebhookRepositoryEnrollment(long RepositoryId, string FullName)
{
    public GitHubWebhookRepositoryEnrollment Validate()
    {
        var parts = FullName?.Split('/') ?? [];
        if (RepositoryId <= 0 || string.IsNullOrWhiteSpace(FullName) ||
            FullName.Length > 200 || FullName.Any(char.IsControl) ||
            parts.Length != 2 ||
            parts.Any(part => string.IsNullOrWhiteSpace(part) ||
                part.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))))
            throw new ArgumentException("GitHub webhook enrollment must bind a positive repository ID and canonical owner/name identity.");
        return this;
    }
}

public enum GitHubWebhookDeliveryDisposition
{
    Accepted,
    DuplicateDelivery,
    DuplicateRevision,
    SupersededRevision
}

public sealed record GitHubWebhookDeliveryReceipt(
    string DeliveryId,
    string PayloadSha256,
    string WorkItemIdentity,
    long Sequence,
    GitHubWebhookDeliveryDisposition Disposition,
    GitHubWorkItemEvent Event,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Authenticates the exact raw request bytes before parsing, then enforces the host's
/// explicit repository enrollment before admitting a normalized work-item event.
/// </summary>
public sealed class GitHubWebhookIngress
{
    private readonly GitHubWebhookSignatureVerifier _signatureVerifier;
    private readonly IReadOnlyDictionary<long, GitHubWebhookRepositoryEnrollment> _enrollments;
    private readonly JsonlGitHubWebhookDeliveryInbox _inbox;
    private readonly Func<DateTimeOffset> _clock;

    public GitHubWebhookIngress(
        GitHubWebhookSignatureVerifier signatureVerifier,
        IEnumerable<GitHubWebhookRepositoryEnrollment> enrollments,
        JsonlGitHubWebhookDeliveryInbox inbox,
        Func<DateTimeOffset>? clock = null)
    {
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        ArgumentNullException.ThrowIfNull(enrollments);
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        var map = new Dictionary<long, GitHubWebhookRepositoryEnrollment>();
        foreach (var enrollment in enrollments)
        {
            enrollment.Validate();
            if (!map.TryAdd(enrollment.RepositoryId, enrollment))
                throw new ArgumentException($"Duplicate GitHub repository enrollment ID '{enrollment.RepositoryId}'.", nameof(enrollments));
        }
        if (map.Count == 0)
            throw new ArgumentException("At least one explicit GitHub repository enrollment is required.", nameof(enrollments));
        _enrollments = map;
    }

    public GitHubWebhookDeliveryReceipt Accept(
        ReadOnlySpan<byte> exactRequestBody,
        string? signatureHeader,
        string deliveryId,
        string eventName)
    {
        // This must remain the first operation that examines the request body.
        _signatureVerifier.Authenticate(exactRequestBody, signatureHeader);

        var normalized = GitHubWorkItemEvent.Parse(eventName, deliveryId, exactRequestBody);
        if (!_enrollments.TryGetValue(normalized.RepositoryId, out var enrollment) ||
            !string.Equals(enrollment.FullName, normalized.Repository, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("GitHub webhook repository is not explicitly enrolled for work-item dispatch.");

        var now = _clock();
        if (now == default)
            throw new InvalidOperationException("GitHub webhook ingress clock returned an invalid timestamp.");
        return _inbox.Accept(normalized, exactRequestBody, now);
    }
}

/// <summary>
/// Durable, single-process delivery inbox. Every unique authenticated delivery is journaled;
/// only a strictly newer work-item revision becomes the current dispatch candidate.
/// Duplicate delivery IDs are idempotent, and conflicting reuse fails closed.
/// </summary>
public sealed class JsonlGitHubWebhookDeliveryInbox
{
    private const int SchemaVersion = 1;
    private const int MaximumJournalBytes = 256 * 1024 * 1024;
    private const int MaximumRecordBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, GitHubWebhookDeliveryReceipt> _deliveries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GitHubWebhookDeliveryReceipt> _latestByWorkItem = new(StringComparer.Ordinal);
    private long _sequence;

    public JsonlGitHubWebhookDeliveryInbox(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl))
            throw new ArgumentException("Inbox journal path must be non-empty and bounded.", nameof(path));
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Replay();
    }

    public GitHubWebhookDeliveryReceipt Accept(
        GitHubWorkItemEvent workItemEvent,
        ReadOnlySpan<byte> exactRequestBody,
        DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(workItemEvent);
        if (exactRequestBody.IsEmpty || exactRequestBody.Length > GitHubWebhookSignatureVerifier.MaximumBodyBytes)
            throw new ArgumentException("GitHub webhook body is empty or exceeds the configured bound.", nameof(exactRequestBody));
        if (receivedAt == default)
            throw new ArgumentOutOfRangeException(nameof(receivedAt));
        var digest = Convert.ToHexString(SHA256.HashData(exactRequestBody)).ToLowerInvariant();

        lock (_gate)
        {
            if (_deliveries.TryGetValue(workItemEvent.DeliveryId, out var existing))
            {
                if (!string.Equals(existing.PayloadSha256, digest, StringComparison.Ordinal))
                    throw new InvalidDataException("A GitHub delivery ID was reused with different payload bytes.");
                return existing with { Disposition = GitHubWebhookDeliveryDisposition.DuplicateDelivery };
            }

            var disposition = GitHubWebhookDeliveryDisposition.Accepted;
            if (_latestByWorkItem.TryGetValue(workItemEvent.WorkItemIdentity, out var latest))
            {
                if (workItemEvent.UpdatedAt < latest.Event.UpdatedAt)
                    disposition = GitHubWebhookDeliveryDisposition.SupersededRevision;
                else if (workItemEvent.UpdatedAt == latest.Event.UpdatedAt)
                {
                    if (!SameRevision(latest.Event, workItemEvent) ||
                        !string.Equals(latest.PayloadSha256, digest, StringComparison.Ordinal))
                        throw new InvalidDataException(
                            "Different GitHub payloads share the same work-item updated_at timestamp; refusing to guess their order.");
                    disposition = GitHubWebhookDeliveryDisposition.DuplicateRevision;
                }
            }

            var receipt = new GitHubWebhookDeliveryReceipt(
                workItemEvent.DeliveryId, digest, workItemEvent.WorkItemIdentity,
                checked(_sequence + 1), disposition, workItemEvent, receivedAt);
            Append(receipt);
            _sequence = receipt.Sequence;
            _deliveries.Add(receipt.DeliveryId, receipt);
            if (disposition == GitHubWebhookDeliveryDisposition.Accepted)
                _latestByWorkItem[receipt.WorkItemIdentity] = receipt;
            return receipt;
        }
    }

    public GitHubWebhookDeliveryReceipt? GetLatest(string workItemIdentity)
    {
        if (string.IsNullOrWhiteSpace(workItemIdentity))
            throw new ArgumentException("Work-item identity is required.", nameof(workItemIdentity));
        lock (_gate)
            return _latestByWorkItem.GetValueOrDefault(workItemIdentity);
    }

    public IReadOnlyList<GitHubWebhookDeliveryReceipt> GetPendingCandidates()
    {
        lock (_gate)
            return _latestByWorkItem.Values.OrderBy(value => value.Sequence).ToArray();
    }

    private static bool SameRevision(GitHubWorkItemEvent left, GitHubWorkItemEvent right) =>
        left.RepositoryId == right.RepositoryId &&
        left.ItemKind == right.ItemKind &&
        left.ItemId == right.ItemId &&
        left.ItemNodeId == right.ItemNodeId &&
        left.Number == right.Number &&
        string.Equals(left.HeadSha, right.HeadSha, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.BaseSha, right.BaseSha, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.HeadRef, right.HeadRef, StringComparison.Ordinal) &&
        string.Equals(left.BaseRef, right.BaseRef, StringComparison.Ordinal);

    private void Append(GitHubWebhookDeliveryReceipt receipt)
    {
        var entry = new JournalEntry(SchemaVersion, receipt.Sequence, receipt);
        var line = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(entry);
        if (line.Length > MaximumRecordBytes)
            throw new InvalidDataException("GitHub webhook inbox record exceeds the maximum serialized size.");
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        if (stream.Length + line.Length + 1 > MaximumJournalBytes)
            throw new IOException("GitHub webhook inbox journal reached its configured size limit.");
        stream.Write(line);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    private void Replay()
    {
        if (!File.Exists(_path))
            return;
        var bytes = File.ReadAllBytes(_path);
        if (bytes.Length > MaximumJournalBytes)
            throw new InvalidDataException("GitHub webhook inbox journal exceeds its maximum size.");
        if (bytes.Length == 0)
            return;
        if (bytes[^1] != (byte)'\n')
            throw new InvalidDataException("GitHub webhook inbox journal has a torn final record; refusing partial recovery.");

        var start = 0;
        while (start < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            if (end < 0 || end - start <= 0 || end - start > MaximumRecordBytes)
                throw new InvalidDataException("GitHub webhook inbox journal contains an invalid record boundary.");
            JournalEntry entry;
            try
            {
                entry = System.Text.Json.JsonSerializer.Deserialize<JournalEntry>(bytes.AsSpan(start, end - start))
                    ?? throw new InvalidDataException("GitHub webhook inbox journal contains a null record.");
            }
            catch (System.Text.Json.JsonException exception)
            {
                throw new InvalidDataException("GitHub webhook inbox journal contains invalid JSON.", exception);
            }
            if (entry.SchemaVersion != SchemaVersion || entry.Sequence != _sequence + 1 || entry.Receipt is null ||
                string.IsNullOrWhiteSpace(entry.Receipt.DeliveryId) ||
                string.IsNullOrWhiteSpace(entry.Receipt.WorkItemIdentity) ||
                entry.Receipt.PayloadSha256.Length != 64 ||
                !entry.Receipt.PayloadSha256.All(Uri.IsHexDigit) ||
                entry.Receipt.Sequence != entry.Sequence ||
                entry.Receipt.Event is null ||
                entry.Receipt.Event.DeliveryId != entry.Receipt.DeliveryId ||
                entry.Receipt.Event.WorkItemIdentity != entry.Receipt.WorkItemIdentity ||
                _deliveries.ContainsKey(entry.Receipt.DeliveryId))
                throw new InvalidDataException("GitHub webhook inbox journal violates its schema, sequence, or identity invariants.");

            var receipt = entry.Receipt;
            if (_latestByWorkItem.TryGetValue(receipt.WorkItemIdentity, out var latest))
            {
                if (receipt.Disposition == GitHubWebhookDeliveryDisposition.Accepted)
                {
                    if (receipt.Event.UpdatedAt <= latest.Event.UpdatedAt)
                        throw new InvalidDataException("GitHub webhook inbox accepted a non-newer work-item revision.");
                    _latestByWorkItem[receipt.WorkItemIdentity] = receipt;
                }
                else if (receipt.Disposition == GitHubWebhookDeliveryDisposition.DuplicateRevision)
                {
                    if (receipt.Event.UpdatedAt != latest.Event.UpdatedAt ||
                        !SameRevision(latest.Event, receipt.Event) ||
                        !string.Equals(receipt.PayloadSha256, latest.PayloadSha256, StringComparison.Ordinal))
                        throw new InvalidDataException("GitHub webhook inbox duplicate-revision record conflicts with its current revision.");
                }
                else if (receipt.Disposition == GitHubWebhookDeliveryDisposition.SupersededRevision)
                {
                    if (receipt.Event.UpdatedAt >= latest.Event.UpdatedAt)
                        throw new InvalidDataException("GitHub webhook inbox superseded-revision record is not older than current revision.");
                }
                else
                    throw new InvalidDataException("GitHub webhook inbox journal contains an invalid delivery disposition.");
            }
            else
            {
                if (receipt.Disposition != GitHubWebhookDeliveryDisposition.Accepted)
                    throw new InvalidDataException("First work-item revision must be accepted.");
                _latestByWorkItem.Add(receipt.WorkItemIdentity, receipt);
            }
            _deliveries.Add(receipt.DeliveryId, receipt);
            _sequence = entry.Sequence;
            start = end + 1;
        }
    }

    private sealed record JournalEntry(int SchemaVersion, long Sequence, GitHubWebhookDeliveryReceipt Receipt);
}
