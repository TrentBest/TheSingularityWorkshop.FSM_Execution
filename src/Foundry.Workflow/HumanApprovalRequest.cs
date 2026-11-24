using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

/// <summary>
/// A proposed operation that cannot proceed until a human explicitly approves it.
/// The proposal is data only: storing it never grants a repository capability.
/// </summary>
public sealed record HumanApprovalRequest(
    int SchemaVersion,
    string ApprovalId,
    string TaskId,
    string CorrelationId,
    string Repository,
    string CandidateSha,
    string RequestedBy,
    string Operation,
    string Summary,
    string Rationale,
    string ArgumentsJson,
    string ArgumentsSha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    long Sequence,
    HumanApprovalStatus Status,
    string? DecisionBy,
    string? DecisionReason,
    DateTimeOffset? DecidedAt)
{
    public const int CurrentSchemaVersion = 1;

    public HumanApprovalRequest Validate()
    {
        static void Required(string value, string name, int maximum)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
                throw new ArgumentException($"{name} must be non-empty, control-free, and at most {maximum} characters.", name);
        }

        if (SchemaVersion != CurrentSchemaVersion)
            throw new ArgumentException("Unsupported human-approval request schema version.");
        Required(ApprovalId, nameof(ApprovalId), 128);
        Required(TaskId, nameof(TaskId), 128);
        Required(CorrelationId, nameof(CorrelationId), 128);
        Required(Repository, nameof(Repository), 256);
        var repositoryParts = Repository.Split('/');
        if (repositoryParts.Length != 2 || repositoryParts.Any(part => string.IsNullOrWhiteSpace(part) ||
            part.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
            throw new ArgumentException("Repository must use owner/name identity.");
        if (string.IsNullOrWhiteSpace(CandidateSha) || CandidateSha.Length != 40 || !CandidateSha.All(Uri.IsHexDigit))
            throw new ArgumentException("Candidate SHA must be a full 40-character hexadecimal commit.");
        Required(RequestedBy, nameof(RequestedBy), 128);
        Required(Operation, nameof(Operation), 128);
        Required(Summary, nameof(Summary), 1024);
        Required(Rationale, nameof(Rationale), 4096);
        if (string.IsNullOrWhiteSpace(ArgumentsJson) || ArgumentsJson.Length > 64 * 1024)
            throw new ArgumentException("Operation arguments must be non-empty and at most 64 KiB.");
        try
        {
            using var document = JsonDocument.Parse(ArgumentsJson, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Operation arguments must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Operation arguments must be valid JSON.", nameof(ArgumentsJson), exception);
        }
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ArgumentsJson))).ToLowerInvariant();
        if (!string.Equals(digest, ArgumentsSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Operation argument digest does not match the exact stored JSON.");
        if (CreatedAt == default || ExpiresAt <= CreatedAt || ExpiresAt - CreatedAt > TimeSpan.FromDays(7))
            throw new ArgumentException("Approval expiry must be after creation and no more than seven days later.");
        if (Sequence < 1 || !Enum.IsDefined(Status))
            throw new ArgumentException("Approval sequence or status is invalid.");
        var decided = Status is HumanApprovalStatus.Approved or HumanApprovalStatus.Rejected or HumanApprovalStatus.Expired or HumanApprovalStatus.Cancelled;
        var hasDecisionFields = DecisionBy is not null || DecisionReason is not null || DecidedAt.HasValue;
        if (decided != hasDecisionFields || (decided &&
            (DecisionBy is null || DecisionReason is null || !DecidedAt.HasValue)))
            throw new ArgumentException("Terminal approval decisions require complete decision provenance.");
        if (decided)
        {
            Required(DecisionBy!, nameof(DecisionBy), 128);
            Required(DecisionReason!, nameof(DecisionReason), 2048);
            if (DecidedAt < CreatedAt || DecidedAt > ExpiresAt.AddDays(1))
                throw new ArgumentException("Decision timestamp is outside the supported request window.");
        }
        return this;
    }

    public static HumanApprovalRequest Create(string approvalId, string taskId, string correlationId,
        string repository, string candidateSha, string requestedBy, string operation, string summary,
        string rationale, string argumentsJson, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(argumentsJson);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(argumentsJson))).ToLowerInvariant();
        return new HumanApprovalRequest(CurrentSchemaVersion, approvalId, taskId, correlationId,
            repository, candidateSha, requestedBy, operation, summary, rationale, argumentsJson,
            digest, createdAt, expiresAt, 1, HumanApprovalStatus.Pending, null, null, null).Validate();
    }

    public HumanApprovalRequest Decide(HumanApprovalStatus decision, string actor, string reason, DateTimeOffset at)
    {
        if (Status != HumanApprovalStatus.Pending)
            throw new InvalidOperationException("Only a pending approval request can be decided.");
        if (decision is not (HumanApprovalStatus.Approved or HumanApprovalStatus.Rejected or HumanApprovalStatus.Cancelled or HumanApprovalStatus.Expired))
            throw new ArgumentOutOfRangeException(nameof(decision), "A terminal human decision is required.");
        if (at < CreatedAt || at > ExpiresAt.AddDays(1))
            throw new ArgumentOutOfRangeException(nameof(at), "Decision time is outside the request window.");
        return (this with { Sequence = Sequence + 1, Status = decision, DecisionBy = actor,
            DecisionReason = reason, DecidedAt = at }).Validate();
    }
}

public enum HumanApprovalStatus { Pending, Approved, Rejected, Expired, Cancelled }

/// <summary>
/// Append-only, fsync-backed, single-process journal for human approval requests.
/// A decision is durable before it is returned to the caller. This store does not execute
/// the approved operation; the host must revalidate policy and issue a fresh capability.
/// </summary>
public sealed class JsonlHumanApprovalRequestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, HumanApprovalRequest> _latest = new(StringComparer.Ordinal);

    public JsonlHumanApprovalRequestStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A journal path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path)) Replay();
    }

    public HumanApprovalRequest Create(HumanApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        lock (_gate)
        {
            if (request.Status != HumanApprovalStatus.Pending || request.Sequence != 1 ||
                request.DecisionBy is not null || request.DecisionReason is not null || request.DecidedAt.HasValue)
                throw new ArgumentException("A new approval request must be pending at sequence one.", nameof(request));
            if (_latest.ContainsKey(request.ApprovalId))
                throw new InvalidOperationException($"Approval request '{request.ApprovalId}' already exists.");
            Append(request);
            _latest.Add(request.ApprovalId, request);
            return request;
        }
    }

    public HumanApprovalRequest Decide(string approvalId, HumanApprovalStatus decision,
        string actor, string reason, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_latest.TryGetValue(approvalId, out var current))
                throw new KeyNotFoundException($"Unknown approval request '{approvalId}'.");
            var updated = current.Decide(decision, actor, reason, at);
            Append(updated);
            _latest[approvalId] = updated;
            return updated;
        }
    }

    public HumanApprovalRequest Get(string approvalId)
    {
        lock (_gate)
            return _latest.TryGetValue(approvalId, out var value)
                ? value : throw new KeyNotFoundException($"Unknown approval request '{approvalId}'.");
    }

    public IReadOnlyList<HumanApprovalRequest> GetPending(string? repository = null)
    {
        lock (_gate)
            return Array.AsReadOnly(_latest.Values
                .Where(x => x.Status == HumanApprovalStatus.Pending &&
                    (repository is null || string.Equals(x.Repository, repository, StringComparison.Ordinal)))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.ApprovalId, StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<HumanApprovalRequest> GetAll()
    {
        lock (_gate)
            return Array.AsReadOnly(_latest.Values.OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.ApprovalId, StringComparer.Ordinal).ToArray());
    }

    private void Append(HumanApprovalRequest request)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions) + "\n");
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void Replay()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                throw new InvalidDataException($"Approval journal contains an empty record at line {lineNumber}.");
            HumanApprovalRequest item;
            try
            {
                item = JsonSerializer.Deserialize<HumanApprovalRequest>(line, JsonOptions)
                    ?? throw new InvalidDataException($"Approval journal record {lineNumber} is null.");
                item.Validate();
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException($"Approval journal record {lineNumber} is invalid.", exception);
            }

            if (!_latest.TryGetValue(item.ApprovalId, out var previous))
            {
                if (item.Sequence != 1 || item.Status != HumanApprovalStatus.Pending)
                    throw new InvalidDataException($"Approval '{item.ApprovalId}' does not begin pending at sequence one.");
            }
            else if (item.Sequence != previous.Sequence + 1 ||
                previous.Status != HumanApprovalStatus.Pending ||
                item.Status == HumanApprovalStatus.Pending ||
                (item with { Sequence = previous.Sequence, Status = previous.Status,
                    DecisionBy = previous.DecisionBy, DecisionReason = previous.DecisionReason,
                    DecidedAt = previous.DecidedAt }) != previous)
            {
                throw new InvalidDataException($"Approval history for '{item.ApprovalId}' is discontinuous or mutates its proposal.");
            }
            _latest[item.ApprovalId] = item;
        }
    }
}
