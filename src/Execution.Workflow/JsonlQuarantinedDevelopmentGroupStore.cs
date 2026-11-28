using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Execution;

/// <summary>
/// Append-only quarantine journal for opaque, serialized development-group revisions.
/// This store deliberately has no manifest or deserialization dependency.
/// </summary>
public sealed class JsonlQuarantinedDevelopmentGroupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, DevelopmentGroupSnapshot> _groups = new(StringComparer.Ordinal);

    public JsonlQuarantinedDevelopmentGroupStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A journal path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path))
            Replay();
    }

    public DevelopmentGroupRevision Create(
        string groupId, string revisionId, ReadOnlySpan<byte> serializedBytes,
        string serializationFormat, string producer, string mandateId, string taskId,
        string turnId, IEnumerable<string>? supportingReferences, DateTimeOffset createdAt)
    {
        var revision = DevelopmentGroupRevision.Create(groupId, revisionId, 1, null,
            serializedBytes.ToArray(), serializationFormat, producer, mandateId, taskId,
            turnId, supportingReferences, createdAt);
        lock (_gate)
        {
            if (_groups.ContainsKey(groupId))
                throw new InvalidOperationException($"Development group '{groupId}' already exists.");
            Append(new DevelopmentGroupJournalEntry(1, DevelopmentGroupJournalEvent.RevisionCreated,
                revision, null, null));
            _groups.Add(groupId, new DevelopmentGroupSnapshot(revision,
                Array.Empty<DevelopmentGroupReviewDecision>(), Array.Empty<DevelopmentGroupMaterializationOutcome>()));
            return Clone(revision);
        }
    }

    public DevelopmentGroupRevision AddRevision(
        string groupId, string revisionId, ReadOnlySpan<byte> serializedBytes,
        string serializationFormat, string producer, string mandateId, string taskId,
        string turnId, IEnumerable<string>? supportingReferences, DateTimeOffset createdAt)
    {
        lock (_gate)
        {
            var current = GetSnapshot(groupId);
            if (current.Decisions.Any(x => x.RevisionId == current.Latest.RevisionId))
                throw new InvalidOperationException("A reviewed revision is immutable; create a new group or append before review.");
            var revision = DevelopmentGroupRevision.Create(groupId, revisionId,
                current.Latest.Sequence + 1, current.Latest.RevisionId, serializedBytes.ToArray(),
                serializationFormat, producer, mandateId, taskId, turnId, supportingReferences, createdAt);
            if (current.Revisions.Any(x => x.RevisionId == revisionId))
                throw new InvalidOperationException($"Revision '{revisionId}' already exists.");
            Append(new DevelopmentGroupJournalEntry(NextSequence(groupId), DevelopmentGroupJournalEvent.RevisionCreated,
                revision, null, null));
            _groups[groupId] = current with { Latest = Clone(revision),
                Revisions = current.Revisions.Append(Clone(revision)).ToArray() };
            return Clone(revision);
        }
    }

    public DevelopmentGroupReviewDecision Decide(
        string groupId, string revisionId, string revisionSha256, string supportingMaterialSha256,
        DevelopmentGroupReviewStatus status, string authenticatedApprover, string reason, DateTimeOffset decidedAt)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        ValidateText(authenticatedApprover, nameof(authenticatedApprover), 256);
        ValidateText(reason, nameof(reason), 2048);
        lock (_gate)
        {
            var snapshot = GetSnapshot(groupId);
            var revision = snapshot.Revisions.SingleOrDefault(x => x.RevisionId == revisionId)
                ?? throw new KeyNotFoundException($"Unknown revision '{revisionId}'.");
            if (revision.RevisionId != snapshot.Latest.RevisionId)
                throw new InvalidOperationException("Only the latest group revision can be reviewed.");
            if (snapshot.Decisions.Any(x => x.RevisionId == revisionId))
                throw new InvalidOperationException("This exact revision already has a terminal review decision.");
            VerifyRevision(revision);
            if (!FixedEquals(revision.Sha256, revisionSha256) ||
                !FixedEquals(revision.SupportingMaterialSha256, supportingMaterialSha256))
                throw new InvalidOperationException("The review decision does not bind to the exact revision and supporting material.");
            var decision = new DevelopmentGroupReviewDecision(groupId, revisionId, revision.Sha256,
                revision.SupportingMaterialSha256, status, authenticatedApprover, reason, decidedAt);
            Append(new DevelopmentGroupJournalEntry(NextSequence(groupId), DevelopmentGroupJournalEvent.ReviewDecided,
                null, decision, null));
            _groups[groupId] = snapshot with { Decisions = snapshot.Decisions.Append(decision).ToArray() };
            return decision;
        }
    }

    /// <summary>Returns exact bytes only when an authenticated approval matches the current revision and its support digest.</summary>
    public ApprovedDevelopmentGroupPayload GetApprovedPayload(string groupId, string revisionId)
    {
        lock (_gate)
        {
            var snapshot = GetSnapshot(groupId);
            var revision = snapshot.Revisions.SingleOrDefault(x => x.RevisionId == revisionId)
                ?? throw new KeyNotFoundException($"Unknown revision '{revisionId}'.");
            var decision = snapshot.Decisions.SingleOrDefault(x => x.RevisionId == revisionId);
            if (decision is null || decision.Status != DevelopmentGroupReviewStatus.Approved ||
                revisionId != snapshot.Latest.RevisionId ||
                !FixedEquals(decision.RevisionSha256, revision.Sha256) ||
                !FixedEquals(decision.SupportingMaterialSha256, revision.SupportingMaterialSha256))
                throw new InvalidOperationException("No current approval exists for this exact development-group revision.");
            VerifyRevision(revision);
            return new ApprovedDevelopmentGroupPayload(Clone(revision), decision, revision.SerializedBytes.ToArray());
        }
    }

    public DevelopmentGroupMaterializationOutcome RecordMaterializationOutcome(
        string groupId, string revisionId, string revisionSha256, bool succeeded,
        string outcomeCode, string detail, DateTimeOffset occurredAt)
    {
        ValidateText(outcomeCode, nameof(outcomeCode), 128);
        ValidateText(detail, nameof(detail), 2048);
        lock (_gate)
        {
            var payload = GetApprovedPayload(groupId, revisionId);
            if (!FixedEquals(payload.Revision.Sha256, revisionSha256))
                throw new InvalidOperationException("Materialization outcome is not bound to the approved digest.");
            var snapshot = GetSnapshot(groupId);
            if (snapshot.MaterializationOutcomes.Any(x => x.RevisionId == revisionId))
                throw new InvalidOperationException("A materialization outcome is already recorded for this revision.");
            var outcome = new DevelopmentGroupMaterializationOutcome(groupId, revisionId,
                revisionSha256, succeeded, outcomeCode, detail, occurredAt);
            Append(new DevelopmentGroupJournalEntry(NextSequence(groupId), DevelopmentGroupJournalEvent.MaterializationRecorded,
                null, null, outcome));
            _groups[groupId] = snapshot with {
                MaterializationOutcomes = snapshot.MaterializationOutcomes.Append(outcome).ToArray()
            };
            return outcome;
        }
    }

    public DevelopmentGroupSnapshot Get(string groupId)
    {
        lock (_gate)
            return Clone(GetSnapshot(groupId));
    }

    private DevelopmentGroupSnapshot GetSnapshot(string groupId) =>
        _groups.TryGetValue(groupId, out var snapshot)
            ? snapshot
            : throw new KeyNotFoundException($"Unknown development group '{groupId}'.");

    private long NextSequence(string groupId) =>
        _groups.TryGetValue(groupId, out var snapshot)
            ? snapshot.JournalSequence + 1
            : 1;

    private void Replay()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            DevelopmentGroupJournalEntry entry;
            try
            {
                entry = JsonSerializer.Deserialize<DevelopmentGroupJournalEntry>(line, JsonOptions)
                    ?? throw new InvalidDataException("Null journal entry.");
                ApplyReplay(entry);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                throw new InvalidDataException($"Quarantine journal record {lineNumber} is invalid.", exception);
            }
        }
    }

    private void ApplyReplay(DevelopmentGroupJournalEntry entry)
    {
        if (entry.Sequence <= 0 || !Enum.IsDefined(entry.Event))
            throw new InvalidDataException("Journal sequence or event is invalid.");
        switch (entry.Event)
        {
            case DevelopmentGroupJournalEvent.RevisionCreated:
            {
                if (entry.Revision is null || entry.Decision is not null || entry.Outcome is not null)
                    throw new InvalidDataException("Revision journal record has an invalid shape.");
                var revision = entry.Revision;
                ValidateRevision(revision);
                if (!_groups.TryGetValue(revision.GroupId, out var existing))
                {
                    if (entry.Sequence != 1 || revision.Sequence != 1 || revision.PreviousRevisionId is not null)
                        throw new InvalidDataException("A development group must begin with revision one.");
                    _groups.Add(revision.GroupId, new DevelopmentGroupSnapshot(revision,
                        new[] { revision }, Array.Empty<DevelopmentGroupReviewDecision>(),
                        Array.Empty<DevelopmentGroupMaterializationOutcome>(), entry.Sequence));
                }
                else
                {
                    if (entry.Sequence != existing.JournalSequence + 1 ||
                        revision.Sequence != existing.Latest.Sequence + 1 ||
                        revision.PreviousRevisionId != existing.Latest.RevisionId ||
                        existing.Decisions.Any(x => x.RevisionId == existing.Latest.RevisionId) ||
                        existing.Revisions.Any(x => x.RevisionId == revision.RevisionId))
                        throw new InvalidDataException("Development-group revision history is discontinuous or mutable.");
                    _groups[revision.GroupId] = existing with {
                        Latest = revision, Revisions = existing.Revisions.Append(revision).ToArray(),
                        JournalSequence = entry.Sequence
                    };
                }
                break;
            }
            case DevelopmentGroupJournalEvent.ReviewDecided:
            {
                if (entry.Decision is null || entry.Revision is not null || entry.Outcome is not null)
                    throw new InvalidDataException("Review journal record has an invalid shape.");
                var decision = entry.Decision;
                var snapshot = GetSnapshot(decision.GroupId);
                var revision = snapshot.Revisions.SingleOrDefault(x => x.RevisionId == decision.RevisionId)
                    ?? throw new InvalidDataException("Review references an unknown revision.");
                if (entry.Sequence != snapshot.JournalSequence + 1 ||
                    revision.RevisionId != snapshot.Latest.RevisionId ||
                    snapshot.Decisions.Any(x => x.RevisionId == decision.RevisionId) ||
                    !FixedEquals(revision.Sha256, decision.RevisionSha256) ||
                    !FixedEquals(revision.SupportingMaterialSha256, decision.SupportingMaterialSha256))
                    throw new InvalidDataException("Review decision is not bound to the latest immutable revision.");
                _groups[decision.GroupId] = snapshot with {
                    Decisions = snapshot.Decisions.Append(decision).ToArray(), JournalSequence = entry.Sequence
                };
                break;
            }
            case DevelopmentGroupJournalEvent.MaterializationRecorded:
            {
                if (entry.Outcome is null || entry.Revision is not null || entry.Decision is not null)
                    throw new InvalidDataException("Materialization journal record has an invalid shape.");
                var outcome = entry.Outcome;
                var snapshot = GetSnapshot(outcome.GroupId);
                var decision = snapshot.Decisions.SingleOrDefault(x => x.RevisionId == outcome.RevisionId);
                var revision = snapshot.Revisions.SingleOrDefault(x => x.RevisionId == outcome.RevisionId);
                if (entry.Sequence != snapshot.JournalSequence + 1 || decision?.Status != DevelopmentGroupReviewStatus.Approved ||
                    revision is null || !FixedEquals(revision.Sha256, outcome.RevisionSha256) ||
                    snapshot.MaterializationOutcomes.Any(x => x.RevisionId == outcome.RevisionId))
                    throw new InvalidDataException("Materialization outcome is not bound to one approved revision.");
                _groups[outcome.GroupId] = snapshot with {
                    MaterializationOutcomes = snapshot.MaterializationOutcomes.Append(outcome).ToArray(),
                    JournalSequence = entry.Sequence
                };
                break;
            }
            default:
                throw new InvalidDataException("Unknown journal event.");
        }
    }

    private void Append(DevelopmentGroupJournalEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(json);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateRevision(DevelopmentGroupRevision revision)
    {
        ValidateText(revision.GroupId, nameof(revision.GroupId), 128);
        ValidateText(revision.RevisionId, nameof(revision.RevisionId), 128);
        ValidateText(revision.SerializationFormat, nameof(revision.SerializationFormat), 128);
        ValidateText(revision.Producer, nameof(revision.Producer), 256);
        ValidateText(revision.MandateId, nameof(revision.MandateId), 128);
        ValidateText(revision.TaskId, nameof(revision.TaskId), 128);
        ValidateText(revision.TurnId, nameof(revision.TurnId), 128);
        if (revision.Sequence <= 0 || revision.CreatedAt == default || revision.SerializedBytes is null ||
            revision.ByteLength != revision.SerializedBytes.Length || revision.SupportingReferences is null ||
            revision.SupportingReferences.Any(string.IsNullOrWhiteSpace) ||
            !FixedEquals(revision.Sha256, Hash(revision.SerializedBytes)) ||
            !FixedEquals(revision.SupportingMaterialSha256, HashSupport(revision.SupportingReferences)))
            throw new ArgumentException("Development-group revision integrity validation failed.");
        if (revision.Sequence == 1 && revision.PreviousRevisionId is not null ||
            revision.Sequence > 1 && string.IsNullOrWhiteSpace(revision.PreviousRevisionId))
            throw new ArgumentException("Revision ancestry is invalid.");
    }

    private static void VerifyRevision(DevelopmentGroupRevision revision) => ValidateRevision(revision);
    private static DevelopmentGroupRevision Clone(DevelopmentGroupRevision revision) =>
        revision with { SerializedBytes = revision.SerializedBytes.ToArray(),
            SupportingReferences = Array.AsReadOnly(revision.SupportingReferences.ToArray()) };

    private static DevelopmentGroupSnapshot Clone(DevelopmentGroupSnapshot snapshot) =>
        snapshot with { Latest = Clone(snapshot.Latest), Revisions = snapshot.Revisions.Select(Clone).ToArray(),
            Decisions = snapshot.Decisions.ToArray(), MaterializationOutcomes = snapshot.MaterializationOutcomes.ToArray() };

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string HashSupport(IReadOnlyList<string> references) =>
        Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(references.OrderBy(x => x, StringComparer.Ordinal), JsonOptions)));

    private static bool FixedEquals(string left, string right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToLowerInvariant()), Encoding.ASCII.GetBytes(right.ToLowerInvariant()));

    private static void ValidateText(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new ArgumentException($"'{name}' is required and must be bounded printable text.", name);
    }
}

public enum DevelopmentGroupReviewStatus { Approved, Rejected }
public enum DevelopmentGroupJournalEvent { RevisionCreated, ReviewDecided, MaterializationRecorded }

public sealed record DevelopmentGroupRevision(
    string GroupId, string RevisionId, long Sequence, string? PreviousRevisionId,
    byte[] SerializedBytes, string Sha256, long ByteLength, string SerializationFormat,
    string Producer, string MandateId, string TaskId, string TurnId,
    IReadOnlyList<string> SupportingReferences, string SupportingMaterialSha256, DateTimeOffset CreatedAt)
{
    internal static DevelopmentGroupRevision Create(string groupId, string revisionId, long sequence,
        string? previousRevisionId, byte[] bytes, string serializationFormat, string producer,
        string mandateId, string taskId, string turnId, IEnumerable<string>? references, DateTimeOffset createdAt)
    {
        var support = (references ?? Array.Empty<string>()).ToArray();
        var revision = new DevelopmentGroupRevision(groupId, revisionId, sequence, previousRevisionId,
            bytes.ToArray(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
            serializationFormat, producer, mandateId, taskId, turnId, Array.AsReadOnly(support),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                support.OrderBy(x => x, StringComparer.Ordinal), new JsonSerializerOptions(JsonSerializerDefaults.Web)))))
                .ToLowerInvariant(), createdAt);
        ValidateCreation(revision);
        return revision;
    }

    private static void ValidateCreation(DevelopmentGroupRevision revision)
    {
        if (revision.Sequence <= 0 || revision.CreatedAt == default || revision.SerializedBytes.Length != revision.ByteLength ||
            string.IsNullOrWhiteSpace(revision.GroupId) || string.IsNullOrWhiteSpace(revision.RevisionId) ||
            string.IsNullOrWhiteSpace(revision.SerializationFormat) || string.IsNullOrWhiteSpace(revision.Producer) ||
            string.IsNullOrWhiteSpace(revision.MandateId) || string.IsNullOrWhiteSpace(revision.TaskId) ||
            string.IsNullOrWhiteSpace(revision.TurnId) || revision.SupportingReferences.Any(string.IsNullOrWhiteSpace) ||
            (revision.Sequence == 1 && revision.PreviousRevisionId is not null) ||
            (revision.Sequence > 1 && string.IsNullOrWhiteSpace(revision.PreviousRevisionId)))
            throw new ArgumentException("Development-group revision metadata is invalid.");
    }
}

public sealed record DevelopmentGroupReviewDecision(
    string GroupId, string RevisionId, string RevisionSha256, string SupportingMaterialSha256,
    DevelopmentGroupReviewStatus Status, string AuthenticatedApprover, string Reason, DateTimeOffset DecidedAt);

public sealed record DevelopmentGroupMaterializationOutcome(
    string GroupId, string RevisionId, string RevisionSha256, bool Succeeded,
    string OutcomeCode, string Detail, DateTimeOffset OccurredAt);

public sealed record ApprovedDevelopmentGroupPayload(
    DevelopmentGroupRevision Revision, DevelopmentGroupReviewDecision Decision, byte[] Bytes);

public sealed record DevelopmentGroupSnapshot(
    DevelopmentGroupRevision Latest,
    IReadOnlyList<DevelopmentGroupRevision> Revisions,
    IReadOnlyList<DevelopmentGroupReviewDecision> Decisions,
    IReadOnlyList<DevelopmentGroupMaterializationOutcome> MaterializationOutcomes,
    long JournalSequence)
{
    internal DevelopmentGroupSnapshot(DevelopmentGroupRevision latest,
        IReadOnlyList<DevelopmentGroupReviewDecision> decisions,
        IReadOnlyList<DevelopmentGroupMaterializationOutcome> outcomes)
        : this(latest, new[] { latest }, decisions, outcomes, 1) { }
}

public sealed record DevelopmentGroupJournalEntry(
    long Sequence, DevelopmentGroupJournalEvent Event, DevelopmentGroupRevision? Revision,
    DevelopmentGroupReviewDecision? Decision, DevelopmentGroupMaterializationOutcome? Outcome);
