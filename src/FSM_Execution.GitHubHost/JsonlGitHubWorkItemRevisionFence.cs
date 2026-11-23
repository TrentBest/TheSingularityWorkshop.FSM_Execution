using System.Text.Json;
using System.Security.Cryptography;
using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.FSM_Execution.GitHubHost;

/// <summary>
/// Durable latest-revision fence for GitHub work-item tasks. It records the exact
/// authenticated webhook payload digest and timestamp; an Issue timestamp is never
/// represented as a Git commit SHA.
/// </summary>
public sealed class JsonlGitHubWorkItemRevisionFence : IWorkTaskClaimAdmissionPolicy
{
    private const int SchemaVersion = 1;
    private const int MaximumJournalBytes = 64 * 1024 * 1024;
    private const int MaximumRecordBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, Revision> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _taskToWorkItem = new(StringComparer.Ordinal);
    private readonly HashSet<string> _uncertainWorkItems = new(StringComparer.Ordinal);
    private long _sequence;

    public JsonlGitHubWorkItemRevisionFence(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl))
            throw new ArgumentException("Revision fence path must be non-empty and bounded.", nameof(path));
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Replay();
    }

    /// <summary>Registers the exact current inbox candidate before task creation.</summary>
    public Revision Register(GitHubWebhookDeliveryReceipt receipt, string taskId)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (string.IsNullOrWhiteSpace(taskId) || taskId.Length > 128 || taskId.Any(char.IsControl))
            throw new ArgumentException("Task identity must be non-empty, bounded, and control-free.", nameof(taskId));
        if (receipt.Disposition is not (GitHubWebhookDeliveryDisposition.Accepted or GitHubWebhookDeliveryDisposition.DuplicateRevision) ||
            receipt.Event is null || receipt.Event.UpdatedAt == default ||
            receipt.PayloadSha256.Length != 64 || !receipt.PayloadSha256.All(Uri.IsHexDigit) ||
            receipt.WorkItemIdentity != receipt.Event.WorkItemIdentity)
            throw new InvalidDataException("Only a validated current GitHub inbox candidate can be registered.");

        lock (_gate)
        {
            if (_latest.TryGetValue(receipt.WorkItemIdentity, out var current))
            {
                if (receipt.Event.UpdatedAt < current.UpdatedAt)
                    throw new InvalidOperationException("A stale GitHub work-item revision cannot replace the current task.");
                if (receipt.Event.UpdatedAt > current.UpdatedAt &&
                    string.Equals(taskId, current.TaskId, StringComparison.Ordinal))
                    throw new InvalidOperationException("A newer GitHub work-item revision requires a distinct immutable task ID.");
                if (receipt.Event.UpdatedAt == current.UpdatedAt)
                {
                    if (!string.Equals(receipt.PayloadSha256, current.PayloadSha256, StringComparison.Ordinal) ||
                        !string.Equals(taskId, current.TaskId, StringComparison.Ordinal))
                        throw new InvalidOperationException("A same-timestamp GitHub revision conflicts with the registered task identity or payload.");
                    return current;
                }
            }
            if (_taskToWorkItem.TryGetValue(taskId, out var priorIdentity) &&
                !string.Equals(priorIdentity, receipt.WorkItemIdentity, StringComparison.Ordinal))
                throw new InvalidOperationException("A task ID cannot be reused for a different GitHub work item.");

            var revision = new Revision(SchemaVersion, checked(_sequence + 1),
                receipt.WorkItemIdentity, receipt.Event.UpdatedAt, receipt.PayloadSha256.ToLowerInvariant(),
                taskId, receipt.Event.ItemKind, receipt.Event.RepositoryId, receipt.Event.Number,
                receipt.Event.ItemNodeId, receipt.Event.HeadSha, receipt.Event.BaseSha);
            try
            {
                Append(revision);
                Apply(revision);
                return revision;
            }
            catch
            {
                // The inbox may already have durably accepted this newer revision.
                // Until reconciliation succeeds, block claims for this work item.
                _uncertainWorkItems.Add(receipt.WorkItemIdentity);
                throw;
            }
        }
    }

    /// <summary>Final lease-boundary check: superseded work items cannot be claimed.</summary>
    public void EnsureClaimAllowed(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task identity is required.", nameof(taskId));
        lock (_gate)
        {
            if (_taskToWorkItem.TryGetValue(taskId, out var identity))
            {
                if (_uncertainWorkItems.Contains(identity))
                    throw new InvalidOperationException("GitHub work-item revision state is awaiting durable inbox reconciliation; claim is denied.");
                if (!_latest.TryGetValue(identity, out var current) ||
                    !string.Equals(current.TaskId, taskId, StringComparison.Ordinal))
                    throw new InvalidOperationException("GitHub work-item task was superseded by a newer authenticated revision and cannot be claimed.");
            }
        }
    }

    public Revision? GetLatest(string workItemIdentity)
    {
        lock (_gate) return _latest.GetValueOrDefault(workItemIdentity);
    }

    private void Apply(Revision revision)
    {
        _latest[revision.WorkItemIdentity] = revision;
        _taskToWorkItem[revision.TaskId] = revision.WorkItemIdentity;
        _sequence = revision.Sequence;
    }

    private void Append(Revision revision)
    {
        var line = JsonSerializer.SerializeToUtf8Bytes(revision, Options);
        if (line.Length > MaximumRecordBytes)
            throw new InvalidDataException("GitHub revision fence record exceeds its configured bound.");
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        if (stream.Length + line.Length + 1 > MaximumJournalBytes)
            throw new IOException("GitHub revision fence journal reached its configured size limit.");
        stream.Write(line);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    private void Replay()
    {
        if (!File.Exists(_path)) return;
        var bytes = File.ReadAllBytes(_path);
        if (bytes.Length > MaximumJournalBytes)
            throw new InvalidDataException("GitHub revision fence journal exceeds its configured size limit.");
        if (bytes.Length == 0) return;
        if (bytes[^1] != (byte)'\n')
            throw new InvalidDataException("GitHub revision fence journal has a torn final record.");
        var start = 0;
        while (start < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            if (end <= start || end - start > MaximumRecordBytes)
                throw new InvalidDataException("GitHub revision fence journal contains an invalid record boundary.");
            Revision revision;
            try
            {
                revision = JsonSerializer.Deserialize<Revision>(bytes.AsSpan(start, end - start), Options)
                    ?? throw new InvalidDataException("GitHub revision fence contains a null record.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("GitHub revision fence contains invalid JSON.", exception);
            }
            if (revision.SchemaVersion != SchemaVersion || revision.Sequence != _sequence + 1 ||
                string.IsNullOrWhiteSpace(revision.WorkItemIdentity) ||
                string.IsNullOrWhiteSpace(revision.TaskId) || revision.TaskId.Length > 128 ||
                revision.UpdatedAt == default || revision.PayloadSha256.Length != 64 ||
                !revision.PayloadSha256.All(Uri.IsHexDigit) || revision.RepositoryId <= 0 ||
                revision.Number <= 0 || string.IsNullOrWhiteSpace(revision.ItemNodeId) ||
                string.IsNullOrWhiteSpace(revision.ItemKind) ||
                (_latest.TryGetValue(revision.WorkItemIdentity, out var prior) && revision.UpdatedAt <= prior.UpdatedAt) ||
                (_taskToWorkItem.TryGetValue(revision.TaskId, out var mapped) &&
                    !string.Equals(mapped, revision.WorkItemIdentity, StringComparison.Ordinal)))
                throw new InvalidDataException("GitHub revision fence journal violates sequence, revision, or identity invariants.");
            Apply(revision);
            start = end + 1;
        }
    }

    public sealed record Revision(int SchemaVersion, long Sequence, string WorkItemIdentity,
        DateTimeOffset UpdatedAt, string PayloadSha256, string TaskId, string ItemKind,
        long RepositoryId, int Number, string ItemNodeId, string? HeadSha, string? BaseSha);
}
