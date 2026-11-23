using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

public enum CiAiTaskWakeupState { Pending, Delivered }

/// <summary>Stable, task-bound wake-up generated only from a Satisfied composite admission.</summary>
public sealed record CiAiTaskWakeup(
    string EventId, string TaskId, string ConsumerRepository, string ConsumerCandidateId,
    string ConsumerSourceSha, string Pipeline, string ExpressionSha256, string PolicyVersion,
    long AdmissionSequence, string EvidenceSetSha256, string IdempotencyKey, long Revision,
    CiAiTaskWakeupState State, int DeliveryAttempts, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? LastAttemptAt, DateTimeOffset? DeliveredAt);

/// <summary>
/// Single-process fsync-backed outbox. It does not evaluate gates or deliver network
/// messages. Hosts deliver at least once and deduplicate using the stable IdempotencyKey.
/// </summary>
public sealed class JsonlCiAiTaskWakeupOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, CiAiTaskWakeup> _latest = new(StringComparer.Ordinal);

    public JsonlCiAiTaskWakeupOutbox(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path)) Replay();
    }

    public CiAiTaskWakeup EnqueueSatisfied(CompositeDependencyAdmissionSnapshot admission, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(admission);
        admission.Identity.Validate();
        if (admission.State != CompositeDependencyAdmissionState.Satisfied)
            throw new InvalidOperationException("Only a persisted Satisfied admission may create a task wake-up.");
        if (admission.Sequence < 1 || admission.UpdatedAt == default || now == default || now < admission.UpdatedAt)
            throw new InvalidDataException("Satisfied admission sequence or timestamp is invalid.");
        CompositeDependencyAdmissionIdentity.ValidateSha(admission.EvidenceSetSha256, nameof(admission.EvidenceSetSha256));
        if (admission.EvidenceSetSha256.Length != 64)
            throw new InvalidDataException("A complete evidence-set SHA-256 is required.");

        var id = EventId(admission.Identity, admission.Sequence, admission.EvidenceSetSha256);
        var item = new CiAiTaskWakeup(id, admission.Identity.TaskId, admission.Identity.ConsumerRepository,
            admission.Identity.ConsumerCandidateId, admission.Identity.ConsumerSourceSha,
            admission.Identity.Pipeline, admission.Identity.ExpressionSha256, admission.Identity.PolicyVersion,
            admission.Sequence, admission.EvidenceSetSha256.ToLowerInvariant(), id, 0,
            CiAiTaskWakeupState.Pending, 0, now, now, null, null);

        lock (_gate)
        {
            if (_latest.TryGetValue(id, out var existing))
            {
                EnsureSameIdentity(existing, item);
                return existing;
            }
            if (_latest.Values.Any(x => x.TaskId == item.TaskId && x.AdmissionSequence == item.AdmissionSequence))
                throw new InvalidDataException("A task admission sequence already has a conflicting wake-up.");
            Append(item);
            _latest.Add(id, item);
            return item;
        }
    }

    /// <summary>Repairs a crash between persisting Satisfied admission and appending its outbox event.</summary>
    public IReadOnlyList<CiAiTaskWakeup> ReconcileSatisfiedAdmissions(
        JsonlCompositeDependencyAdmissionStore admissions, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(admissions);
        var result = admissions.GetAll()
            .Where(x => x.State == CompositeDependencyAdmissionState.Satisfied)
            .OrderBy(x => x.Identity.TaskId, StringComparer.Ordinal)
            .Select(x => EnqueueSatisfied(x, now)).ToArray();
        return Array.AsReadOnly(result);
    }

    public IReadOnlyList<CiAiTaskWakeup> GetPending()
    {
        lock (_gate) return Array.AsReadOnly(_latest.Values
            .Where(x => x.State == CiAiTaskWakeupState.Pending)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.EventId, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Persist before calling the scheduler; a crash leaves a retryable Pending event.</summary>
    public CiAiTaskWakeup RecordDeliveryAttempt(string eventId, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (at == default) throw new ArgumentException("Attempt timestamp is required.", nameof(at));
        lock (_gate)
        {
            var current = Required(eventId);
            if (current.State == CiAiTaskWakeupState.Delivered) return current;
            if (at < current.UpdatedAt) throw new InvalidDataException("Wake-up timestamps cannot move backwards.");
            var next = current with { Revision = checked(current.Revision + 1),
                DeliveryAttempts = checked(current.DeliveryAttempts + 1), LastAttemptAt = at, UpdatedAt = at };
            Append(next);
            _latest[eventId] = next;
            return next;
        }
    }

    /// <summary>Call only after the scheduler confirms durable acceptance of the idempotency key.</summary>
    public CiAiTaskWakeup MarkDelivered(string eventId, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (at == default) throw new ArgumentException("Delivery timestamp is required.", nameof(at));
        lock (_gate)
        {
            var current = Required(eventId);
            if (current.State == CiAiTaskWakeupState.Delivered) return current;
            if (current.DeliveryAttempts == 0 || current.LastAttemptAt is null)
                throw new InvalidOperationException("A delivery attempt must be recorded before acknowledgement.");
            if (at < current.UpdatedAt) throw new InvalidDataException("Wake-up timestamps cannot move backwards.");
            var next = current with { Revision = checked(current.Revision + 1),
                State = CiAiTaskWakeupState.Delivered, UpdatedAt = at, DeliveredAt = at };
            Append(next);
            _latest[eventId] = next;
            return next;
        }
    }

    public CiAiTaskWakeup? Get(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        lock (_gate) return _latest.GetValueOrDefault(eventId);
    }

    private CiAiTaskWakeup Required(string id) => _latest.TryGetValue(id, out var item)
        ? item : throw new KeyNotFoundException($"Wake-up '{id}' does not exist.");

    private void Append(CiAiTaskWakeup item)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, JsonOptions) + "\n");
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void Replay()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 0)
        {
            stream.Position = stream.Length - 1;
            if (stream.ReadByte() != 10) throw new InvalidDataException("Wake-up outbox has an incomplete final record.");
            stream.Position = 0;
        }
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        string? line;
        var number = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            number++;
            CiAiTaskWakeup item;
            try
            {
                using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                RejectDuplicates(json.RootElement);
                item = JsonSerializer.Deserialize<CiAiTaskWakeup>(line, JsonOptions)
                    ?? throw new InvalidDataException("Null wake-up record.");
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException or ArgumentException)
            { throw new InvalidDataException($"Wake-up record {number} is invalid.", ex); }
            Validate(item);
            if (!_latest.TryGetValue(item.EventId, out var prior))
            {
                if (item.Revision != 0 || item.State != CiAiTaskWakeupState.Pending ||
                    item.DeliveryAttempts != 0 || item.LastAttemptAt is not null || item.DeliveredAt is not null)
                    throw new InvalidDataException("First event record must be pristine Pending state.");
                if (_latest.Values.Any(x => x.TaskId == item.TaskId && x.AdmissionSequence == item.AdmissionSequence))
                    throw new InvalidDataException("Duplicate task admission sequence in outbox.");
                _latest.Add(item.EventId, item);
                continue;
            }
            EnsureSameIdentity(prior, item);
            if (item.Revision != prior.Revision + 1 || item.UpdatedAt < prior.UpdatedAt ||
                prior.State == CiAiTaskWakeupState.Delivered ||
                item.DeliveryAttempts < prior.DeliveryAttempts ||
                item.DeliveryAttempts > prior.DeliveryAttempts + 1)
                throw new InvalidDataException("Wake-up revision or lifecycle is invalid.");
            if (item.State == CiAiTaskWakeupState.Pending &&
                (item.DeliveredAt is not null || item.DeliveryAttempts != prior.DeliveryAttempts + 1 ||
                 item.LastAttemptAt != item.UpdatedAt))
                throw new InvalidDataException("Pending update must record exactly one delivery attempt.");
            if (item.State == CiAiTaskWakeupState.Delivered &&
                (item.DeliveryAttempts != prior.DeliveryAttempts || item.DeliveryAttempts < 1 ||
                 item.DeliveredAt != item.UpdatedAt))
                throw new InvalidDataException("Delivered event must follow a durable delivery attempt.");
            _latest[item.EventId] = item;
        }
    }

    private static void Validate(CiAiTaskWakeup x)
    {
        static void Text(string value, int max, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
                throw new InvalidDataException($"Wake-up {name} is invalid.");
        }
        Text(x.EventId, 80, nameof(x.EventId)); Text(x.TaskId, 128, nameof(x.TaskId));
        Text(x.ConsumerRepository, 256, nameof(x.ConsumerRepository));
        Text(x.ConsumerCandidateId, 256, nameof(x.ConsumerCandidateId));
        Text(x.Pipeline, 100, nameof(x.Pipeline)); Text(x.PolicyVersion, 128, nameof(x.PolicyVersion));
        if (x.ConsumerSourceSha is not { Length: 40 } || !x.ConsumerSourceSha.All(Uri.IsHexDigit) ||
            x.ExpressionSha256 is not { Length: 64 } || !x.ExpressionSha256.All(Uri.IsHexDigit) ||
            x.EvidenceSetSha256 is not { Length: 64 } || !x.EvidenceSetSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Wake-up source/evidence digest is invalid.");
        if (x.AdmissionSequence < 1 || x.Revision < 0 || x.DeliveryAttempts < 0 ||
            x.CreatedAt == default || x.UpdatedAt == default ||
            x.State is not (CiAiTaskWakeupState.Pending or CiAiTaskWakeupState.Delivered) ||
            x.IdempotencyKey != x.EventId ||
            x.EventId != EventId(new CompositeDependencyAdmissionIdentity(x.TaskId, x.ConsumerRepository,
                x.ConsumerCandidateId, x.ConsumerSourceSha, x.Pipeline, x.ExpressionSha256, x.PolicyVersion),
                x.AdmissionSequence, x.EvidenceSetSha256))
            throw new InvalidDataException("Wake-up identity or lifecycle is invalid.");
        var parts = x.ConsumerRepository.Split('/');
        if (parts.Length != 2 || parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." ||
            p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
            throw new InvalidDataException("Wake-up repository identity is invalid.");
    }

    private static string EventId(CompositeDependencyAdmissionSnapshot x) =>
        EventId(x.Identity, x.Sequence, x.EvidenceSetSha256);

    private static string EventId(CompositeDependencyAdmissionIdentity identity, long sequence, string digest)
    {
        var canonical = string.Join("\n", identity.TaskId, identity.ConsumerRepository,
            identity.ConsumerCandidateId, identity.ConsumerSourceSha, identity.Pipeline,
            identity.ExpressionSha256, identity.PolicyVersion,
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), digest.ToLowerInvariant());
        return "wakeup-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void EnsureSameIdentity(CiAiTaskWakeup a, CiAiTaskWakeup b)
    {
        if (a.EventId != b.EventId || a.TaskId != b.TaskId ||
            a.ConsumerRepository != b.ConsumerRepository || a.ConsumerCandidateId != b.ConsumerCandidateId ||
            a.ConsumerSourceSha != b.ConsumerSourceSha || a.Pipeline != b.Pipeline ||
            a.ExpressionSha256 != b.ExpressionSha256 || a.PolicyVersion != b.PolicyVersion ||
            a.AdmissionSequence != b.AdmissionSequence || a.EvidenceSetSha256 != b.EvidenceSetSha256 ||
            a.IdempotencyKey != b.IdempotencyKey)
            throw new InvalidDataException("Wake-up event conflicts with its original admission identity.");
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property in wake-up journal.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
