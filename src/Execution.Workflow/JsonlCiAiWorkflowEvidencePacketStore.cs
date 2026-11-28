using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Execution;

public enum CiAiWorkflowEvidencePacketStoreOutcome
{
    Stored,
    Duplicate,
    Conflict
}

public sealed record StoredCiAiWorkflowEvidencePacket(
    string TaskId,
    string CorrelationId,
    string Repository,
    long RunId,
    int RunAttempt,
    string CandidateSha,
    string PacketSha256,
    DateTimeOffset ReceivedAt,
    byte[] PacketBytes);

/// <summary>
/// Content-addressed, append-only durable staging for authoritative CiAi V1 packet bytes.
/// A packet is written and flushed before its index record is appended. Recovery verifies every
/// indexed packet digest; an orphaned packet from a crash before index append can be safely retried.
/// </summary>
public sealed class JsonlCiAiWorkflowEvidencePacketStore
{
    private const int MaximumPacketBytes = CiAiWorkflowEvidencePacketConsumer.MaximumPacketBytes;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _rootDirectory;
    private readonly string _packetDirectory;
    private readonly string _ledgerPath;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public JsonlCiAiWorkflowEvidencePacketStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        _rootDirectory = Path.GetFullPath(directoryPath);
        _packetDirectory = Path.Combine(_rootDirectory, "packets");
        _ledgerPath = Path.Combine(_rootDirectory, "index.jsonl");
        Directory.CreateDirectory(_packetDirectory);
        if (File.Exists(_ledgerPath))
            Replay();
    }

    /// <summary>
    /// Durably stages exact packet bytes. A repeated task/run/attempt identity is idempotent only
    /// when every immutable identity and the exact packet digest match.
    /// </summary>
    public CiAiWorkflowEvidencePacketStoreOutcome Store(
        string taskId,
        string correlationId,
        string repository,
        long runId,
        int runAttempt,
        string candidateSha,
        string expectedPacketSha256,
        ReadOnlyMemory<byte> exactPacketBytes,
        DateTimeOffset receivedAt)
    {
        ValidateIdentity(taskId, correlationId, repository, runId, runAttempt,
            candidateSha, expectedPacketSha256, exactPacketBytes, receivedAt);

        var actualDigest = Digest(exactPacketBytes.Span);
        if (!FixedTimeEqualsHex(actualDigest, expectedPacketSha256))
            throw new InvalidDataException("CiAi packet bytes do not match the expected authoritative packet digest.");

        var key = CreateKey(taskId, correlationId, repository, runId, runAttempt);
        var relativePath = Path.Combine("packets", key + ".json").Replace(Path.DirectorySeparatorChar, '/');
        var packetPath = Path.Combine(_rootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var entry = new Entry(taskId, correlationId, repository, runId, runAttempt,
            candidateSha.ToLowerInvariant(), actualDigest, receivedAt, relativePath);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (existing != entry)
                    return CiAiWorkflowEvidencePacketStoreOutcome.Conflict;
                var existingBytes = ReadAndVerify(existing);
                return CryptographicOperations.FixedTimeEquals(existingBytes, exactPacketBytes.Span)
                    ? CiAiWorkflowEvidencePacketStoreOutcome.Duplicate
                    : CiAiWorkflowEvidencePacketStoreOutcome.Conflict;
            }

            if (File.Exists(packetPath))
            {
                var orphanedBytes = File.ReadAllBytes(packetPath);
                if (!FixedTimeEqualsHex(Digest(orphanedBytes), actualDigest) ||
                    !CryptographicOperations.FixedTimeEquals(orphanedBytes, exactPacketBytes.Span))
                    return CiAiWorkflowEvidencePacketStoreOutcome.Conflict;
            }
            else
            {
                var temporaryPath = packetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 81920, FileOptions.WriteThrough))
                    {
                        stream.Write(exactPacketBytes.Span);
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(temporaryPath, packetPath, overwrite: false);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
            }

            AppendEntry(entry);
            _entries.Add(key, entry);
            return CiAiWorkflowEvidencePacketStoreOutcome.Stored;
        }
    }

    /// <summary>Loads an exact staged packet and re-verifies its content digest on every read.</summary>
    public bool TryRead(
        string taskId,
        string correlationId,
        string repository,
        long runId,
        int runAttempt,
        out StoredCiAiWorkflowEvidencePacket? packet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        if (runId <= 0 || runAttempt <= 0)
            throw new ArgumentOutOfRangeException(nameof(runId));

        var key = CreateKey(taskId, correlationId, repository, runId, runAttempt);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                packet = null;
                return false;
            }

            packet = new StoredCiAiWorkflowEvidencePacket(
                entry.TaskId, entry.CorrelationId, entry.Repository, entry.RunId,
                entry.RunAttempt, entry.CandidateSha, entry.PacketSha256,
                entry.ReceivedAt, ReadAndVerify(entry));
            return true;
        }
    }

    private void Replay()
    {
        lock (_gate)
        {
            foreach (var line in File.ReadLines(_ledgerPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var entry = JsonSerializer.Deserialize<Entry>(line, JsonOptions)
                    ?? throw new InvalidDataException("CiAi packet index contains an empty entry.");
                entry.Validate();
                var key = CreateKey(entry.TaskId, entry.CorrelationId, entry.Repository, entry.RunId, entry.RunAttempt);
                if (_entries.ContainsKey(key))
                    throw new InvalidDataException("CiAi packet index contains a duplicate task/run/attempt identity.");
                _ = ReadAndVerify(entry);
                _entries.Add(key, entry);
            }
        }
    }

    private byte[] ReadAndVerify(Entry entry)
    {
        var expectedRelativePath = Path.Combine("packets",
            CreateKey(entry.TaskId, entry.CorrelationId, entry.Repository, entry.RunId, entry.RunAttempt) + ".json")
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!string.Equals(entry.PacketRelativePath, expectedRelativePath, StringComparison.Ordinal))
            throw new InvalidDataException("CiAi packet index contains an unexpected packet path.");
        var path = Path.Combine(_rootDirectory, entry.PacketRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new InvalidDataException("A durable CiAi packet index entry has no packet bytes.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is 0 or > MaximumPacketBytes ||
            !FixedTimeEqualsHex(Digest(bytes), entry.PacketSha256))
            throw new InvalidDataException("Durable CiAi packet bytes are missing, oversized, or fail digest verification.");
        return bytes;
    }

    private void AppendEntry(Entry entry)
    {
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        using var stream = new FileStream(_ledgerPath, FileMode.Append, FileAccess.Write,
            FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateIdentity(
        string taskId,
        string correlationId,
        string repository,
        long runId,
        int runAttempt,
        string candidateSha,
        string expectedPacketSha256,
        ReadOnlyMemory<byte> packetBytes,
        DateTimeOffset receivedAt)
    {
        if (string.IsNullOrWhiteSpace(taskId) || taskId.Length > 200 || taskId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 200 || correlationId.Any(char.IsControl))
            throw new ArgumentException("CiAi packet task and correlation identities are invalid.");
        var parts = repository?.Split('/') ?? Array.Empty<string>();
        if (parts.Length != 2 || parts.Any(part => string.IsNullOrWhiteSpace(part) ||
            part.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))))
            throw new ArgumentException("CiAi packet repository must use canonical owner/name identity.", nameof(repository));
        if (runId <= 0 || runAttempt <= 0)
            throw new ArgumentOutOfRangeException(nameof(runId), "Run and attempt identities must be positive.");
        if (candidateSha is not { Length: 40 } || !candidateSha.All(Uri.IsHexDigit))
            throw new ArgumentException("CiAi packet candidate SHA must be a full commit SHA.", nameof(candidateSha));
        if (expectedPacketSha256 is not { Length: 64 } || !expectedPacketSha256.All(Uri.IsHexDigit) ||
            !string.Equals(expectedPacketSha256, expectedPacketSha256.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ArgumentException("CiAi packet digest must be a lowercase SHA-256 value.", nameof(expectedPacketSha256));
        if (packetBytes.IsEmpty || packetBytes.Length > MaximumPacketBytes)
            throw new InvalidDataException("CiAi packet bytes are empty or exceed the configured size limit.");
        if (receivedAt == default)
            throw new ArgumentException("CiAi packet receipt time is required.", nameof(receivedAt));
    }

    private static string CreateKey(string taskId, string correlationId, string repository, long runId, int runAttempt)
    {
        var material = string.Join("\n", repository.ToLowerInvariant(), taskId, correlationId,
            runId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            runAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Digest(Encoding.UTF8.GetBytes(material));
    }

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool FixedTimeEqualsHex(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64 ||
            !left.All(Uri.IsHexDigit) || !right.All(Uri.IsHexDigit))
            return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    }

    private sealed record Entry(
        string TaskId,
        string CorrelationId,
        string Repository,
        long RunId,
        int RunAttempt,
        string CandidateSha,
        string PacketSha256,
        DateTimeOffset ReceivedAt,
        string PacketRelativePath)
    {
        public void Validate()
        {
            ValidateIdentity(TaskId, CorrelationId, Repository, RunId, RunAttempt,
                CandidateSha, PacketSha256, new byte[] { 1 }, ReceivedAt);
            if (PacketRelativePath is null || PacketRelativePath.Length > 128 ||
                PacketRelativePath.Contains('\\') || PacketRelativePath.StartsWith('/') ||
                PacketRelativePath.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("CiAi packet index contains an unsafe packet path.");
        }
    }
}
