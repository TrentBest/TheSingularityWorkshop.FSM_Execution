using System.Security.Cryptography;
using System.Text;
using TheSingularityWorkshop.Execution;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class JsonlCiAiWorkflowEvidencePacketStoreTests
{
    private const string TaskId = "task-packet-1";
    private const string CorrelationId = "correlation-packet-1";
    private const string Repository = "TrentBest/Example";
    private const string CandidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset ReceivedAt =
        new(2026, 11, 27, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StoresExactBytesIdempotentlyAndReplaysAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"execution-ciai-packets-{Guid.NewGuid():N}");
        var bytes = Encoding.UTF8.GetBytes("{\"schema\":\"ciai.workflow-evidence.v1\",\"payload\":\"exact\"}");
        var digest = Digest(bytes);
        try
        {
            var store = new JsonlCiAiWorkflowEvidencePacketStore(directory);
            Assert.Equal(CiAiWorkflowEvidencePacketStoreOutcome.Stored,
                Store(store, bytes, digest));
            Assert.Equal(CiAiWorkflowEvidencePacketStoreOutcome.Duplicate,
                Store(store, bytes, digest));

            Assert.True(store.TryRead(TaskId, CorrelationId, Repository, 42, 2, out var stored));
            Assert.NotNull(stored);
            Assert.Equal(digest, stored.PacketSha256);
            Assert.Equal(bytes, stored.PacketBytes);

            var reopened = new JsonlCiAiWorkflowEvidencePacketStore(directory);
            Assert.True(reopened.TryRead(TaskId, CorrelationId, Repository, 42, 2, out var replayed));
            Assert.NotNull(replayed);
            Assert.Equal(bytes, replayed.PacketBytes);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsDifferentPacketForTheSameTaskRunAttempt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"execution-ciai-conflict-{Guid.NewGuid():N}");
        var first = Encoding.UTF8.GetBytes("{\"packet\":1}");
        var second = Encoding.UTF8.GetBytes("{\"packet\":2}");
        try
        {
            var store = new JsonlCiAiWorkflowEvidencePacketStore(directory);
            Assert.Equal(CiAiWorkflowEvidencePacketStoreOutcome.Stored,
                Store(store, first, Digest(first)));
            Assert.Equal(CiAiWorkflowEvidencePacketStoreOutcome.Conflict,
                Store(store, second, Digest(second)));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RefusesToReplayTamperedPacketBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"execution-ciai-tamper-{Guid.NewGuid():N}");
        var bytes = Encoding.UTF8.GetBytes("{\"packet\":\"original\"}");
        try
        {
            var store = new JsonlCiAiWorkflowEvidencePacketStore(directory);
            Assert.Equal(CiAiWorkflowEvidencePacketStoreOutcome.Stored,
                Store(store, bytes, Digest(bytes)));

            var packetPath = Path.Combine(directory, "packets", CreateKey() + ".json");
            File.WriteAllText(packetPath, "{\"packet\":\"tampered\"}");
            Assert.Throws<InvalidDataException>(() =>
                new JsonlCiAiWorkflowEvidencePacketStore(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static CiAiWorkflowEvidencePacketStoreOutcome Store(
        JsonlCiAiWorkflowEvidencePacketStore store, byte[] bytes, string digest) =>
        store.Store(TaskId, CorrelationId, Repository, 42, 2,
            CandidateSha, digest, bytes, ReceivedAt);

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string CreateKey()
    {
        var material = string.Join("\n", Repository.ToLowerInvariant(), TaskId, CorrelationId, "42", "2");
        return Digest(Encoding.UTF8.GetBytes(material));
    }
}
