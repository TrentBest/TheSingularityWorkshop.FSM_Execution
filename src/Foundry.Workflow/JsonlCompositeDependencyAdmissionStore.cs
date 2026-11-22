using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

/// <summary>Durable result of the authoritative CiAi composite expression evaluation.</summary>
public enum CompositeDependencyAdmissionState
{
    Waiting,
    Satisfied,
    Blocked
}

/// <summary>
/// Immutable identity of one consumer task's composite prerequisite expression.
/// ExpressionSha256 is CiAi's canonical DefinitionSha256; EvidenceSetSha256 is computed
/// by the trusted host over the complete set of individually verified leaf evidence.
/// </summary>
public sealed record CompositeDependencyAdmissionIdentity(
    string TaskId,
    string ConsumerRepository,
    string ConsumerCandidateId,
    string ConsumerSourceSha,
    string Pipeline,
    string ExpressionSha256,
    string PolicyVersion)
{
    public CompositeDependencyAdmissionIdentity Validate()
    {
        static void Text(string value, int max, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
                throw new ArgumentException($"Composite admission {name} must be bounded, non-empty, and control-free.", name);
        }

        Text(TaskId, 128, nameof(TaskId));
        Text(ConsumerCandidateId, 256, nameof(ConsumerCandidateId));
        Text(Pipeline, 100, nameof(Pipeline));
        Text(PolicyVersion, 128, nameof(PolicyVersion));

        var repository = ConsumerRepository?.Split('/') ?? Array.Empty<string>();
        if (repository.Length != 2 || repository.Any(part => string.IsNullOrWhiteSpace(part) || part.Length > 100 || part.Any(char.IsControl)))
            throw new ArgumentException("Consumer repository must use owner/name identity.", nameof(ConsumerRepository));
        if (ConsumerSourceSha is not { Length: 40 } || !ConsumerSourceSha.All(Uri.IsHexDigit))
            throw new ArgumentException("Consumer source identity must be a full 40-character commit SHA.", nameof(ConsumerSourceSha));
        if (ExpressionSha256 is not { Length: 64 } || !ExpressionSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("CiAi expression identity must be a SHA-256 digest.", nameof(ExpressionSha256));
        return this;
    }

    internal static void ValidateSha(string value, string name)
    {
        if (value is null || (value.Length != 40 && value.Length != 64) ||
            !value.All(Uri.IsHexDigit))
            throw new ArgumentException("A full hexadecimal SHA-256 or commit SHA is required.", name);
    }
}

/// <summary>One append-only composite decision; no single-provider run is misrepresented as the whole expression.</summary>
public sealed record CompositeDependencyAdmissionSnapshot(
    CompositeDependencyAdmissionIdentity Identity,
    CompositeDependencyAdmissionState State,
    long Sequence,
    string EvidenceSetSha256,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Single-process, fsync-backed JSONL journal for CiAi composite decisions.
/// The caller must authenticate and verify evidence, invoke CiAiPipelineGateEvaluator,
/// and pass its exact three-valued result. This journal performs no gate evaluation.
/// </summary>
public sealed class JsonlCompositeDependencyAdmissionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, CompositeDependencyAdmissionSnapshot> _latest = new(StringComparer.Ordinal);

    public JsonlCompositeDependencyAdmissionStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path))
            Replay();
    }

    public CompositeDependencyAdmissionSnapshot CreateWaiting(
        CompositeDependencyAdmissionIdentity identity, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        if (now == default) throw new ArgumentException("A timestamp is required.", nameof(now));
        var snapshot = new CompositeDependencyAdmissionSnapshot(identity,
            CompositeDependencyAdmissionState.Waiting, 0, new string('0', 64), now);
        lock (_gate)
        {
            if (_latest.ContainsKey(identity.TaskId))
                throw new InvalidOperationException($"Composite admission for task '{identity.TaskId}' already exists.");
            Append(snapshot);
            _latest.Add(identity.TaskId, snapshot);
            return snapshot;
        }
    }

    /// <summary>
    /// Persists a decision returned by CiAiPipelineGateEvaluator. The host must not pass
    /// model output or unverified callback claims as the state or evidence digest.
    /// </summary>
    public CompositeDependencyAdmissionSnapshot ApplyVerifiedDecision(
        CompositeDependencyAdmissionIdentity identity,
        CompositeDependencyAdmissionState state,
        long sequence,
        string evidenceSetSha256,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        if (state is not (CompositeDependencyAdmissionState.Waiting or
            CompositeDependencyAdmissionState.Satisfied or CompositeDependencyAdmissionState.Blocked))
            throw new ArgumentOutOfRangeException(nameof(state));
        CompositeDependencyAdmissionIdentity.ValidateSha(evidenceSetSha256, nameof(evidenceSetSha256));
        if (evidenceSetSha256.Length != 64)
            throw new ArgumentException("Evidence-set identity must be a SHA-256 digest.", nameof(evidenceSetSha256));
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (observedAt == default) throw new ArgumentException("A timestamp is required.", nameof(observedAt));

        lock (_gate)
        {
            if (!_latest.TryGetValue(identity.TaskId, out var current))
                throw new KeyNotFoundException($"No composite admission exists for task '{identity.TaskId}'.");
            if (current.Identity != identity)
                throw new InvalidDataException("Consumer candidate, pipeline, expression, or policy identity changed.");
            if (sequence == current.Sequence)
            {
                if (current.State == state &&
                    string.Equals(current.EvidenceSetSha256, evidenceSetSha256, StringComparison.OrdinalIgnoreCase) &&
                    current.UpdatedAt == observedAt)
                    return current;
                throw new InvalidDataException("A duplicate composite decision sequence has conflicting content.");
            }
            if (sequence != current.Sequence + 1)
                throw new InvalidDataException("Composite decision sequence must advance by exactly one.");
            if (observedAt < current.UpdatedAt)
                throw new InvalidDataException("Composite decision timestamps cannot move backwards.");
            if (current.State != CompositeDependencyAdmissionState.Waiting)
                throw new InvalidOperationException("A terminal composite admission cannot be changed.");

            var next = new CompositeDependencyAdmissionSnapshot(identity, state, sequence,
                evidenceSetSha256.ToLowerInvariant(), observedAt);
            Append(next);
            _latest[identity.TaskId] = next;
            return next;
        }
    }

    public CompositeDependencyAdmissionSnapshot? Get(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        lock (_gate) return _latest.GetValueOrDefault(taskId);
    }

    public IReadOnlyList<CompositeDependencyAdmissionSnapshot> GetAll()
    {
        lock (_gate)
            return Array.AsReadOnly(_latest.Values.OrderBy(item => item.Identity.TaskId, StringComparer.Ordinal).ToArray());
    }

    private void Append(CompositeDependencyAdmissionSnapshot snapshot)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, JsonOptions) + "\n");
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void Replay()
    {
        foreach (var (line, index) in File.ReadLines(_path, Encoding.UTF8).Select((value, i) => (value, i)))
        {
            if (string.IsNullOrWhiteSpace(line))
                throw new InvalidDataException($"Composite admission journal contains an empty record at line {index + 1}.");
            CompositeDependencyAdmissionSnapshot snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<CompositeDependencyAdmissionSnapshot>(line, JsonOptions)
                    ?? throw new InvalidDataException("Composite admission record was null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Composite admission record {index + 1} is invalid.", exception);
            }

            snapshot.Identity.Validate();
            CompositeDependencyAdmissionIdentity.ValidateSha(snapshot.EvidenceSetSha256, nameof(snapshot.EvidenceSetSha256));
            if (snapshot.EvidenceSetSha256.Length != 64 || snapshot.UpdatedAt == default ||
                snapshot.State is not (CompositeDependencyAdmissionState.Waiting or
                    CompositeDependencyAdmissionState.Satisfied or CompositeDependencyAdmissionState.Blocked))
                throw new InvalidDataException($"Composite admission record {index + 1} has invalid state or evidence identity.");

            if (snapshot.Sequence == 0)
            {
                if (snapshot.State != CompositeDependencyAdmissionState.Waiting ||
                    snapshot.EvidenceSetSha256 != new string('0', 64) ||
                    _latest.ContainsKey(snapshot.Identity.TaskId))
                    throw new InvalidDataException("Composite admission journal contains an invalid initial record.");
                _latest.Add(snapshot.Identity.TaskId, snapshot);
                continue;
            }

            if (!_latest.TryGetValue(snapshot.Identity.TaskId, out var previous) ||
                previous.Identity != snapshot.Identity ||
                snapshot.Sequence != previous.Sequence + 1 ||
                snapshot.UpdatedAt < previous.UpdatedAt ||
                previous.State != CompositeDependencyAdmissionState.Waiting)
                throw new InvalidDataException("Composite admission journal history is inconsistent.");
            _latest[snapshot.Identity.TaskId] = snapshot;
        }
    }
}
