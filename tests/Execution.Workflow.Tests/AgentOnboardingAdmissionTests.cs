using TheSingularityWorkshop.Execution;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class AgentOnboardingAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 1, 0, 0, TimeSpan.Zero);
    private const string Repository = "TrentBest/raWWar";
    private static readonly string TargetSha = new('a', 40);

    [Fact]
    public async Task PersistentSubmissionAndClaimRequireFreshExactShaAdmissionBeforeMutation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-onboarding-{Guid.NewGuid():N}.jsonl");
        try
        {
            var coordinator = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            var request = Request();

            Assert.Throws<UnauthorizedAccessException>(() => coordinator.Submit(request));

            var rejected = new TestVerifier(Evidence() with
            {
                IsEligible = false,
                Outcome = "NOT_ELIGIBLE",
                FailureCode = "onboarding.missing",
                ContentSha256 = null
            });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                coordinator.SubmitAsync(request, rejected).AsTask());
            Assert.False(File.Exists(path));

            var accepted = new TestVerifier(Evidence());
            var queued = await coordinator.SubmitAsync(request, accepted);
            Assert.Equal(1, accepted.Calls);
            Assert.Equal(WorkTaskStatus.Queued, queued.Status);
            Assert.Single(File.ReadAllLines(path));

            Assert.Throws<UnauthorizedAccessException>(() =>
                coordinator.Claim(request.TaskId, "agent-a", TimeSpan.FromMinutes(5), Now));

            var changed = new TestVerifier(Evidence() with { ContentSha256 = new string('b', 64) });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                coordinator.ClaimAsync(request.TaskId, "agent-a", TimeSpan.FromMinutes(5),
                    Now, changed).AsTask());
            Assert.Single(File.ReadAllLines(path));

            var fresh = new TestVerifier(Evidence());
            var claimed = await coordinator.ClaimAsync(request.TaskId, "agent-a",
                TimeSpan.FromMinutes(5), Now, fresh);
            Assert.Equal(1, fresh.Calls);
            Assert.Equal(WorkTaskStatus.Claimed, claimed.Status);
            Assert.Equal(2, File.ReadAllLines(path).Length);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RestoredGovernedTasksRemainUnavailableUntilExactShaAdmissionIsRevalidated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-onboarding-replay-{Guid.NewGuid():N}.jsonl");
        try
        {
            var first = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            await first.SubmitAsync(Request(), new TestVerifier(Evidence()));

            var restored = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(path));
            Assert.Throws<InvalidOperationException>(() => restored.GetSnapshot("rawwar-task"));

            var changed = new TestVerifier(Evidence() with { ContentSha256 = new string('c', 64) });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                restored.InitializeAsync(changed).AsTask());
            Assert.Throws<InvalidOperationException>(() => restored.GetSnapshot("rawwar-task"));

            var valid = new TestVerifier(Evidence());
            await restored.InitializeAsync(valid);
            Assert.Equal(1, valid.Calls);
            Assert.Equal(WorkTaskStatus.Queued, restored.GetSnapshot("rawwar-task").Status);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static WorkTaskRequest Request() => new(
        1, "rawwar-task", "rawwar-correlation", Repository, 1,
        "https://github.com/TrentBest/raWWar/issues/1", TargetSha,
        new[] { "src", "tests" }, 3);

    private static AgentOnboardingAdmissionEvidence Evidence() => new(
        Repository, TargetSha, AgentOnboardingAdmissionEvidence.RequiredPath,
        AgentOnboardingAdmissionEvidence.RequiredContractVersion, true, "ELIGIBLE",
        null, "CiAi v1 onboarding contract passed at the exact commit.", new string('d', 64));

    private sealed class TestVerifier(AgentOnboardingAdmissionEvidence evidence)
        : IAgentOnboardingAdmissionVerifier
    {
        public int Calls { get; private set; }

        public ValueTask<AgentOnboardingAdmissionEvidence> VerifyAsync(string repository,
            string targetSha, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(evidence);
        }
    }
}
