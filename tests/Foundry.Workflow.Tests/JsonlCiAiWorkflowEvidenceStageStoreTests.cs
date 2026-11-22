using System.Security.Cryptography;
using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class JsonlCiAiWorkflowEvidenceStageStoreTests
{
    [Fact]
    public void StagesExactPacketAndReplaysItAfterRestart()
    {
        var path = NewPath();
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("{\"schema\":\"ciai.workflow-evidence.v2\",\"candidateSha\":\"abc\"}");
            var digest = Digest(bytes);
            var store = new JsonlCiAiWorkflowEvidenceStageStore(path);

            Assert.Equal(CiAiEvidenceStageOutcome.Staged, Stage(store, bytes, digest));
            var reopened = new JsonlCiAiWorkflowEvidenceStageStore(path);
            var staged = Assert.IsType<CiAiWorkflowEvidenceStage>(reopened.Get("request-1"));
            Assert.Equal(digest, staged.PacketSha256);
            Assert.Equal(bytes, staged.GetPacketBytes());
            Assert.Equal("task-1", staged.TaskId);
            Assert.Equal(123L, staged.RunId);
            Assert.Equal(2, staged.RunAttempt);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ExactRetryIsIdempotentButDifferentBytesForSameRequestConflict()
    {
        var path = NewPath();
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("{\"packet\":1}");
            var store = new JsonlCiAiWorkflowEvidenceStageStore(path);
            Assert.Equal(CiAiEvidenceStageOutcome.Staged, Stage(store, bytes, Digest(bytes)));
            Assert.Equal(CiAiEvidenceStageOutcome.Duplicate, Stage(store, bytes, Digest(bytes),
                new DateTimeOffset(2026, 11, 22, 20, 0, 0, TimeSpan.Zero)));

            var changed = System.Text.Encoding.UTF8.GetBytes("{\"packet\":2}");
            Assert.Throws<InvalidDataException>(() => Stage(store, changed, Digest(changed)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RejectsDigestMismatchAndIncompleteFinalRecord()
    {
        var path = NewPath();
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("{\"packet\":1}");
            var store = new JsonlCiAiWorkflowEvidenceStageStore(path);
            Assert.Throws<InvalidDataException>(() => Stage(store, bytes, new string('0', 64)));
            Assert.Empty(store.GetAll());

            File.WriteAllText(path, "{\"truncated\":");
            Assert.Throws<InvalidDataException>(() => new JsonlCiAiWorkflowEvidenceStageStore(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RejectsReusedRunOrIdempotencyIdentityAcrossRequests()
    {
        var path = NewPath();
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("packet");
            var store = new JsonlCiAiWorkflowEvidenceStageStore(path);
            Stage(store, bytes, Digest(bytes));
            Assert.Throws<InvalidDataException>(() => store.Stage(
                "request-2", "task-2", "operation-2", "idempotency-2", "receipt-2",
                "TrentBest/raWWar", 123, 2, bytes, Digest(bytes), DateTimeOffset.UtcNow));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static CiAiEvidenceStageOutcome Stage(JsonlCiAiWorkflowEvidenceStageStore store,
        byte[] bytes, string digest, DateTimeOffset? stagedAt = null) =>
        store.Stage("request-1", "task-1", "operation-1", "idempotency-1", "receipt-1",
            "TrentBest/raWWar", 123, 2, bytes, digest,
            stagedAt ?? new DateTimeOffset(2026, 11, 22, 19, 0, 0, TimeSpan.Zero));

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"foundry-evidence-stage-{Guid.NewGuid():N}.jsonl");
}
