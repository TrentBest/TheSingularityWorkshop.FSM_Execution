using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

/// <summary>Three-valued provider readiness. Waiting is a neutral hold, never a failure.</summary>
public enum DependencyAdmissionStatus
{
    Waiting,
    Satisfied,
    Blocked
}

/// <summary>
/// Exact provider capability identity required by a task. SourceSha is the candidate
/// SHA whose provider evidence is being evaluated, not a moving branch name.
/// </summary>
public sealed record DependencyGateIdentity(
    string GateId,
    string ProviderRepository,
    string CandidateId,
    string SourceSha,
    string Capability,
    string PolicyVersion)
{
    public DependencyGateIdentity Validate()
    {
        if (string.IsNullOrWhiteSpace(GateId) || GateId.Length > 128 ||
            string.IsNullOrWhiteSpace(ProviderRepository) || ProviderRepository.Length > 256 ||
            string.IsNullOrWhiteSpace(CandidateId) || CandidateId.Length > 128 ||
            string.IsNullOrWhiteSpace(Capability) || Capability.Length > 256 ||
            string.IsNullOrWhiteSpace(PolicyVersion) || PolicyVersion.Length > 128 ||
            new[] { GateId, ProviderRepository, CandidateId, Capability, PolicyVersion }.Any(
                value => value.Any(char.IsControl)))
            throw new ArgumentException("Dependency gate identity fields must be bounded, non-empty, and control-free.");
        var parts = ProviderRepository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Provider repository must use owner/name identity.");
        if (SourceSha is null || SourceSha.Length != 40 || !SourceSha.All(Uri.IsHexDigit))
            throw new ArgumentException("Provider source SHA must be a full 40-character hexadecimal commit.");
        return this;
    }
}

/// <summary>
/// Normalized observation supplied only after the host has authenticated the provider
/// and verified its exact run/attempt evidence. This type is a correlation contract,
/// not an authentication primitive.
/// </summary>
public sealed record DependencyGateObservation(
    string TaskId,
    DependencyGateIdentity Gate,
    DependencyAdmissionStatus Status,
    long Sequence,
    long RunId,
    int RunAttempt,
    string EvidenceSha256,
    DateTimeOffset ObservedAt);

/// <summary>Latest durable admission state for one task/provider capability gate.</summary>
public sealed record DependencyAdmissionSnapshot(
    string TaskId,
    DependencyGateIdentity Gate,
    DependencyAdmissionStatus Status,
    long Sequence,
    long? RunId,
    int? RunAttempt,
    string? EvidenceSha256,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Append-only, single-process JSONL journal for dependency admission. Waiting records
/// survive restart. Only a host-verified exact observation may advance them. Terminal
/// states cannot regress, and sequence identity makes duplicate delivery idempotent.
/// </summary>
public sealed class JsonlDependencyAdmissionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, DependencyAdmissionSnapshot> _latest = new(StringComparer.Ordinal);

    public JsonlDependencyAdmissionStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A journal path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path))
            Replay();
    }

    public DependencyAdmissionSnapshot CreateWaiting(string taskId, DependencyGateIdentity gate,
        DateTimeOffset now)
    {
        ValidateTaskId(taskId);
        ArgumentNullException.ThrowIfNull(gate);
        gate.Validate();
        var snapshot = new DependencyAdmissionSnapshot(taskId, gate,
            DependencyAdmissionStatus.Waiting, 0, null, null, null, now);
        lock (_gate)
        {
            if (_latest.ContainsKey(taskId))
                throw new InvalidOperationException($"Dependency admission for task '{taskId}' already exists.");
            Append(snapshot);
            _latest.Add(taskId, snapshot);
            return snapshot;
        }
    }

    public DependencyAdmissionSnapshot ApplyVerifiedObservation(DependencyGateObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateTaskId(observation.TaskId);
        ArgumentNullException.ThrowIfNull(observation.Gate);
        observation.Gate.Validate();
        ValidateObservation(observation);

        lock (_gate)
        {
            if (!_latest.TryGetValue(observation.TaskId, out var current))
                throw new KeyNotFoundException($"No dependency admission exists for task '{observation.TaskId}'.");
            if (current.Gate != observation.Gate)
                throw new InvalidDataException("Provider repository, candidate, source SHA, capability, or policy identity changed.");
            if (observation.Sequence == current.Sequence)
            {
                if (current.Status == observation.Status &&
                    current.RunId == (observation.RunId == 0 ? null : observation.RunId) &&
                    current.RunAttempt == (observation.RunAttempt == 0 ? null : observation.RunAttempt) &&
                    current.EvidenceSha256 == (string.IsNullOrEmpty(observation.EvidenceSha256) ? null : observation.EvidenceSha256) &&
                    current.UpdatedAt == observation.ObservedAt)
                    return current;
                throw new InvalidDataException("A duplicate dependency event sequence has conflicting content.");
            }
            if (observation.Sequence != current.Sequence + 1)
                throw new InvalidDataException("Dependency event sequence must advance by exactly one.");
            if (current.Status != DependencyAdmissionStatus.Waiting)
                throw new InvalidOperationException("A terminal dependency admission cannot be changed.");
            var next = new DependencyAdmissionSnapshot(observation.TaskId, observation.Gate,
                observation.Status, observation.Sequence,
                observation.RunId == 0 ? null : observation.RunId,
                observation.RunAttempt == 0 ? null : observation.RunAttempt,
                string.IsNullOrEmpty(observation.EvidenceSha256) ? null : observation.EvidenceSha256,
                observation.ObservedAt);
            Append(next);
            _latest[observation.TaskId] = next;
            return next;
        }
    }

    public DependencyAdmissionSnapshot? Get(string taskId)
    {
        ValidateTaskId(taskId);
        lock (_gate)
            return _latest.GetValueOrDefault(taskId);
    }

    public IReadOnlyList<DependencyAdmissionSnapshot> GetAll()
    {
        lock (_gate)
            return Array.AsReadOnly(_latest.Values.OrderBy(value => value.TaskId, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateTaskId(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || taskId.Length > 128 || taskId.Any(char.IsControl))
            throw new ArgumentException("Task ID must be non-empty, control-free, and at most 128 characters.", nameof(taskId));
    }

    private static void ValidateObservation(DependencyGateObservation observation)
    {
        if (observation.Sequence < 1)
            throw new ArgumentOutOfRangeException(nameof(observation.Sequence));
        if (observation.Status is not (DependencyAdmissionStatus.Waiting or
            DependencyAdmissionStatus.Satisfied or DependencyAdmissionStatus.Blocked))
            throw new ArgumentOutOfRangeException(nameof(observation.Status));
        if (observation.Status == DependencyAdmissionStatus.Waiting)
        {
            if (observation.RunId != 0 || observation.RunAttempt != 0 ||
                !string.IsNullOrEmpty(observation.EvidenceSha256))
                throw new ArgumentException("A Waiting observation cannot claim terminal provider evidence.");
            return;
        }
        if (observation.RunId <= 0 || observation.RunAttempt <= 0 ||
            observation.EvidenceSha256 is null || observation.EvidenceSha256.Length != 64 ||
            !observation.EvidenceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Terminal provider evidence requires exact run/attempt identity and a SHA-256 digest.");
    }

    private void Append(DependencyAdmissionSnapshot snapshot)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, JsonOptions) + "\n");
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void Replay()
    {
        DependencyAdmissionSnapshot? previous = null;
        var lines = File.ReadAllLines(_path, Encoding.UTF8);
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
                throw new InvalidDataException($"Dependency admission journal contains an empty record at line {index + 1}.");
            DependencyAdmissionSnapshot snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<DependencyAdmissionSnapshot>(lines[index], JsonOptions)
                    ?? throw new InvalidDataException("Dependency admission journal record was null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Dependency admission journal record {index + 1} is invalid.", exception);
            }
            ValidateTaskId(snapshot.TaskId);
            snapshot.Gate.Validate();
            if (snapshot.Sequence == 0)
            {
                if (snapshot.Status != DependencyAdmissionStatus.Waiting ||
                    snapshot.RunId is not null || snapshot.RunAttempt is not null ||
                    snapshot.EvidenceSha256 is not null || _latest.ContainsKey(snapshot.TaskId))
                    throw new InvalidDataException("Initial dependency admission record is invalid or duplicated.");
            }
            else
            {
                if (!_latest.TryGetValue(snapshot.TaskId, out previous) ||
                    previous.Gate != snapshot.Gate || previous.Status != DependencyAdmissionStatus.Waiting ||
                    snapshot.Sequence != previous.Sequence + 1 ||
                    snapshot.Status == DependencyAdmissionStatus.Waiting &&
                        (snapshot.RunId is not null || snapshot.RunAttempt is not null || snapshot.EvidenceSha256 is not null) ||
                    snapshot.Status != DependencyAdmissionStatus.Waiting &&
                        (snapshot.RunId is null or <= 0 || snapshot.RunAttempt is null or <= 0 ||
                         snapshot.EvidenceSha256 is null || snapshot.EvidenceSha256.Length != 64 ||
                         !snapshot.EvidenceSha256.All(Uri.IsHexDigit)))
                    throw new InvalidDataException("Dependency admission journal history is discontinuous or invalid.");
            }
            _latest[snapshot.TaskId] = snapshot;
        }
    }
}
