using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry;

/// <summary>Immutable packet bytes and exact dispatch identity after host-side v2 validation.</summary>
public sealed record CiAiWorkflowEvidenceStage(
    string RequestId, string TaskId, string OperationId, string IdempotencyKey,
    string ReceiptId, string Repository, long RunId, int RunAttempt,
    string PacketSha256, string PacketBase64, DateTimeOffset StagedAt)
{
    public byte[] GetPacketBytes() => Convert.FromBase64String(PacketBase64);
}

public enum CiAiEvidenceStageOutcome { Staged, Duplicate }

/// <summary>
/// Append-only durable staging for already-validated workflow evidence. This store does not
/// validate packets, acknowledge providers, or wake tasks. It preserves exact bytes and
/// detects incomplete final writes and conflicting replay.
/// </summary>
public sealed class JsonlCiAiWorkflowEvidenceStageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, CiAiWorkflowEvidenceStage> _latest = new(StringComparer.Ordinal);

    public JsonlCiAiWorkflowEvidenceStageStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A journal path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path)) Replay();
    }

    public CiAiEvidenceStageOutcome Stage(
        string requestId, string taskId, string operationId, string idempotencyKey,
        string receiptId, string repository, long runId, int runAttempt,
        ReadOnlyMemory<byte> packetBytes, string expectedPacketSha256, DateTimeOffset stagedAt)
    {
        ValidateText(requestId, nameof(requestId), 200);
        ValidateText(taskId, nameof(taskId), 128);
        ValidateText(operationId, nameof(operationId), 200);
        ValidateText(idempotencyKey, nameof(idempotencyKey), 200);
        ValidateText(receiptId, nameof(receiptId), 200);
        ValidateText(repository, nameof(repository), 256);
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Repository must use owner/name identity.", nameof(repository));
        if (runId <= 0 || runAttempt <= 0) throw new ArgumentOutOfRangeException(nameof(runId));
        if (packetBytes.IsEmpty || packetBytes.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Staged evidence must contain 1 byte through 16 MiB.");
        if (stagedAt == default) throw new ArgumentException("A staging timestamp is required.", nameof(stagedAt));
        var digest = Convert.ToHexString(SHA256.HashData(packetBytes.Span)).ToLowerInvariant();
        if (!string.Equals(digest, expectedPacketSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Staged packet bytes do not match the validator's SHA-256 digest.");
        var snapshot = new CiAiWorkflowEvidenceStage(requestId, taskId, operationId, idempotencyKey,
            receiptId, repository, runId, runAttempt, digest, Convert.ToBase64String(packetBytes.Span), stagedAt);

        lock (_gate)
        {
            if (_latest.TryGetValue(requestId, out var existing))
            {
                if (existing == snapshot) return CiAiEvidenceStageOutcome.Duplicate;
                throw new InvalidDataException("A request already has different staged evidence; immutable staging conflict.");
            }
            if (_latest.Values.Any(value => value.OperationId == operationId || value.IdempotencyKey == idempotencyKey ||
                (value.Repository == repository && value.RunId == runId && value.RunAttempt == runAttempt)))
                throw new InvalidDataException("Operation, idempotency key, or exact provider run was already staged for another request.");
            Append(snapshot);
            _latest.Add(requestId, snapshot);
            return CiAiEvidenceStageOutcome.Staged;
        }
    }

    public CiAiWorkflowEvidenceStage? Get(string requestId)
    {
        ValidateText(requestId, nameof(requestId), 200);
        lock (_gate) return _latest.GetValueOrDefault(requestId);
    }

    public IReadOnlyList<CiAiWorkflowEvidenceStage> GetAll()
    {
        lock (_gate) return Array.AsReadOnly(_latest.Values.OrderBy(value => value.RequestId, StringComparer.Ordinal).ToArray());
    }

    private void Append(CiAiWorkflowEvidenceStage snapshot)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, JsonOptions) + "\n");
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
            if (stream.ReadByte() != 10) throw new InvalidDataException("Evidence-stage journal has an incomplete final record.");
            stream.Position = 0;
        }
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            CiAiWorkflowEvidenceStage snapshot;
            try { snapshot = JsonSerializer.Deserialize<CiAiWorkflowEvidenceStage>(line, JsonOptions) ?? throw new InvalidDataException("Null stage record."); }
            catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
            { throw new InvalidDataException($"Evidence-stage journal record {lineNumber} is invalid.", exception); }
            ValidateText(snapshot.RequestId, nameof(snapshot.RequestId), 200);
            ValidateText(snapshot.TaskId, nameof(snapshot.TaskId), 128);
            ValidateText(snapshot.OperationId, nameof(snapshot.OperationId), 200);
            ValidateText(snapshot.IdempotencyKey, nameof(snapshot.IdempotencyKey), 200);
            ValidateText(snapshot.ReceiptId, nameof(snapshot.ReceiptId), 200);
            ValidateText(snapshot.Repository, nameof(snapshot.Repository), 256);
            if (snapshot.RunId <= 0 || snapshot.RunAttempt <= 0 || snapshot.StagedAt == default)
                throw new InvalidDataException("Evidence-stage identity or timestamp is invalid.");
            byte[] bytes;
            try { bytes = snapshot.GetPacketBytes(); }
            catch (FormatException exception) { throw new InvalidDataException("Staged packet base64 is invalid.", exception); }
            if (bytes.Length is 0 or > 16 * 1024 * 1024 ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), snapshot.PacketSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Staged packet bytes failed replay digest verification.");
            if (_latest.ContainsKey(snapshot.RequestId) || _latest.Values.Any(value =>
                value.OperationId == snapshot.OperationId || value.IdempotencyKey == snapshot.IdempotencyKey ||
                (value.Repository == snapshot.Repository && value.RunId == snapshot.RunId && value.RunAttempt == snapshot.RunAttempt)))
                throw new InvalidDataException("Evidence-stage journal contains duplicate immutable identity.");
            _latest.Add(snapshot.RequestId, snapshot);
        }
    }

    private static void ValidateText(string value, string name, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException($"{name} must be non-empty, bounded, and control-free.", name);
    }
}