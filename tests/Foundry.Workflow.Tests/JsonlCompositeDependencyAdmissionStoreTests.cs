using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class JsonlCompositeDependencyAdmissionStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WaitingCompositeAdmissionSurvivesRestartAndDoesNotClaimRunIdentity()
    {
        var path = NewPath();
        try
        {
            var identity = Identity();
            var store = new JsonlCompositeDependencyAdmissionStore(path);
            var waiting = store.CreateWaiting(identity, Now);

            Assert.Equal(CompositeDependencyAdmissionState.Waiting, waiting.State);
            Assert.Equal(0, waiting.Sequence);
            Assert.Equal(new string('0', 64), waiting.EvidenceSetSha256);

            var restored = new JsonlCompositeDependencyAdmissionStore(path).Get(identity.TaskId);
            Assert.Equal(waiting, restored);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CompositeDecisionBindsExpressionAndCompleteEvidenceSet()
    {
        var path = NewPath();
        try
        {
            var identity = Identity();
            var store = new JsonlCompositeDependencyAdmissionStore(path);
            store.CreateWaiting(identity, Now);
            var digest = new string('a', 64);
            var satisfied = store.ApplyVerifiedDecision(identity,
                CompositeDependencyAdmissionState.Satisfied, 1, digest, Now.AddMinutes(1));

            Assert.Equal(CompositeDependencyAdmissionState.Satisfied, satisfied.State);
            Assert.Equal(identity.ExpressionSha256, satisfied.Identity.ExpressionSha256);
            Assert.Equal(digest, satisfied.EvidenceSetSha256);
            Assert.Equal(satisfied, new JsonlCompositeDependencyAdmissionStore(path).Get(identity.TaskId));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void IdentityMismatchAndSequenceGapsFailClosed()
    {
        var path = NewPath();
        try
        {
            var identity = Identity();
            var store = new JsonlCompositeDependencyAdmissionStore(path);
            store.CreateWaiting(identity, Now);
            Assert.Throws<InvalidDataException>(() => store.ApplyVerifiedDecision(
                identity with { ExpressionSha256 = new string('b', 64) },
                CompositeDependencyAdmissionState.Satisfied, 1, new string('a', 64), Now.AddMinutes(1)));
            Assert.Throws<InvalidDataException>(() => store.ApplyVerifiedDecision(
                identity, CompositeDependencyAdmissionState.Satisfied, 2,
                new string('a', 64), Now.AddMinutes(1)));
            Assert.Throws<ArgumentException>(() => store.ApplyVerifiedDecision(
                identity, CompositeDependencyAdmissionState.Satisfied, 1,
                "not-a-digest", Now.AddMinutes(1)));
            Assert.Equal(CompositeDependencyAdmissionState.Waiting, store.Get(identity.TaskId)!.State);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void DuplicateDecisionIsIdempotentAndTerminalDecisionCannotRegress()
    {
        var path = NewPath();
        try
        {
            var identity = Identity();
            var store = new JsonlCompositeDependencyAdmissionStore(path);
            store.CreateWaiting(identity, Now);
            var digest = new string('c', 64);
            var blocked = store.ApplyVerifiedDecision(identity,
                CompositeDependencyAdmissionState.Blocked, 1, digest, Now.AddMinutes(1));
            Assert.Equal(blocked, store.ApplyVerifiedDecision(identity,
                CompositeDependencyAdmissionState.Blocked, 1, digest, Now.AddMinutes(1)));
            Assert.Throws<InvalidDataException>(() => store.ApplyVerifiedDecision(identity,
                CompositeDependencyAdmissionState.Satisfied, 1, digest, Now.AddMinutes(1)));
            Assert.Throws<InvalidOperationException>(() => store.ApplyVerifiedDecision(identity,
                CompositeDependencyAdmissionState.Satisfied, 2, new string('d', 64), Now.AddMinutes(2)));
        }
        finally { Delete(path); }
    }

    private static CompositeDependencyAdmissionIdentity Identity() => new(
        "task-composite-1", "TrentBest/raWWar", "rawwar-candidate-1", new string('1', 40),
        "Ecosystem", new string('2', 64), "policy-v1");

    private static string NewPath() => Path.Combine(Path.GetTempPath(), "foundry-composite-admission-" + Guid.NewGuid().ToString("N") + ".jsonl");

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
