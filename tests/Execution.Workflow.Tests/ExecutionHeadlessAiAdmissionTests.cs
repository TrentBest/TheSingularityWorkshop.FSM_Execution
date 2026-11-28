using TheSingularityWorkshop.Execution;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class ExecutionHeadlessAiAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 1, 0, 0, TimeSpan.Zero);
    private const string Repository = "TrentBest/raWWar";
    private static readonly string BaseSha = new('a', 40);
    private static readonly string CandidateSha = new('b', 40);

    [Fact]
    public async Task GovernedModelTurnRevalidatesAndJournalsTheExactCandidateSha()
    {
        var taskPath = Path.Combine(Path.GetTempPath(), $"execution-model-admission-{Guid.NewGuid():N}.jsonl");
        var turnPath = taskPath + ".turns.jsonl";
        try
        {
            var baseEvidence = Evidence(BaseSha, 'd');
            var candidateEvidence = Evidence(CandidateSha, 'e');
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(taskPath));
            await tasks.SubmitAsync(Request(), new FixedVerifier(baseEvidence));
            var claimed = await tasks.ClaimAsync("rawwar-model-task", "agent-a",
                TimeSpan.FromMinutes(30), Now, new FixedVerifier(baseEvidence));
            var investigating = tasks.Transition("rawwar-model-task", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "Begin governed model turn.");

            var verifier = new FixedVerifier(candidateEvidence);
            var client = new RecordingClient();
            var coordinator = new ExecutionHeadlessAiTurnCoordinator(tasks, client,
                new JsonlHeadlessAiTurnJournal(turnPath), new FixedTimeProvider(Now.AddMinutes(1)), verifier);

            var result = await coordinator.InvokeAsync("rawwar-model-task", investigating.Lease!.LeaseId,
                "feature/rawwar-admission", CandidateSha, "engineer", "inspect",
                "propose bounded work", """{"type":"object"}""");

            Assert.Equal(2, verifier.Calls);
            Assert.Equal(CandidateSha, verifier.LastSha);
            Assert.Equal(candidateEvidence, result.CandidateAdmissionEvidence);
            Assert.Equal(1, client.CallCount);
            Assert.Contains(candidateEvidence.ContentSha256!, File.ReadAllText(turnPath));
        }
        finally
        {
            if (File.Exists(taskPath)) File.Delete(taskPath);
            if (File.Exists(turnPath)) File.Delete(turnPath);
            if (File.Exists(turnPath + ".lock")) File.Delete(turnPath + ".lock");
        }
    }

    [Fact]
    public async Task GovernedModelTurnStopsBeforeProviderWhenCandidateAdmissionChanges()
    {
        var taskPath = Path.Combine(Path.GetTempPath(), $"execution-model-admission-change-{Guid.NewGuid():N}.jsonl");
        var turnPath = taskPath + ".turns.jsonl";
        try
        {
            var baseEvidence = Evidence(BaseSha, 'd');
            var tasks = new PersistentWorkTaskCoordinator(new JsonlWorkTaskStore(taskPath));
            await tasks.SubmitAsync(Request(), new FixedVerifier(baseEvidence));
            var claimed = await tasks.ClaimAsync("rawwar-model-task", "agent-a",
                TimeSpan.FromMinutes(30), Now, new FixedVerifier(baseEvidence));
            var investigating = tasks.Transition("rawwar-model-task", claimed.Lease!.LeaseId,
                WorkTaskStatus.Investigating, Now.AddSeconds(1), "Begin governed model turn.");

            var verifier = new SequenceVerifier(Evidence(CandidateSha, 'e'), Evidence(CandidateSha, 'f'));
            var client = new RecordingClient();
            var coordinator = new ExecutionHeadlessAiTurnCoordinator(tasks, client,
                new JsonlHeadlessAiTurnJournal(turnPath), new FixedTimeProvider(Now.AddMinutes(1)), verifier);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.InvokeAsync(
                "rawwar-model-task", investigating.Lease!.LeaseId,
                "feature/rawwar-admission", CandidateSha, "engineer", "inspect",
                "propose bounded work", """{"type":"object"}"""));

            Assert.Equal(2, verifier.Calls);
            Assert.Equal(0, client.CallCount);
        }
        finally
        {
            if (File.Exists(taskPath)) File.Delete(taskPath);
            if (File.Exists(turnPath)) File.Delete(turnPath);
            if (File.Exists(turnPath + ".lock")) File.Delete(turnPath + ".lock");
        }
    }

    private static WorkTaskRequest Request() => new(
        1, "rawwar-model-task", "rawwar-model-correlation", Repository, 1,
        "https://github.com/TrentBest/raWWar/issues/1", BaseSha, new[] { "src" }, 3);

    private static AgentOnboardingAdmissionEvidence Evidence(string sha, char digest) => new(
        Repository, sha, AgentOnboardingAdmissionEvidence.RequiredPath,
        AgentOnboardingAdmissionEvidence.RequiredContractVersion, true, "ELIGIBLE",
        null, "CiAi v1 onboarding contract passed at the exact commit.", new string(digest, 64));

    private sealed class FixedVerifier(AgentOnboardingAdmissionEvidence evidence)
        : IAgentOnboardingAdmissionVerifier
    {
        public int Calls { get; private set; }
        public string? LastSha { get; private set; }

        public ValueTask<AgentOnboardingAdmissionEvidence> VerifyAsync(string repository,
            string targetSha, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastSha = targetSha;
            return ValueTask.FromResult(evidence);
        }
    }

    private sealed class SequenceVerifier(params AgentOnboardingAdmissionEvidence[] evidence)
        : IAgentOnboardingAdmissionVerifier
    {
        public int Calls { get; private set; }

        public ValueTask<AgentOnboardingAdmissionEvidence> VerifyAsync(string repository,
            string targetSha, CancellationToken cancellationToken = default)
        {
            var result = evidence[Math.Min(Calls, evidence.Length - 1)];
            Calls++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingClient : IHeadlessAiAgentTurnClient
    {
        public int CallCount { get; private set; }

        public IExecutionHeadlessAiPreparedTurn Prepare(ExecutionHeadlessAiTurnRequest request, DateTimeOffset now) =>
            new PreparedTurn(new string('a', 64));

        public Task<ExecutionHeadlessAiTurnResult> ExecuteAsync(ExecutionHeadlessAiTurnRequest request,
            IExecutionHeadlessAiPreparedTurn prepared, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ExecutionHeadlessAiTurnResult
            {
                TurnId = "turn-rawwar-1",
                TaskId = request.TaskId,
                CorrelationId = request.CorrelationId,
                Repository = request.Repository,
                AuthorizedBranch = request.AuthorizedBranch,
                BaseCommitSha = request.BaseCommitSha,
                CandidateCommitSha = request.CandidateCommitSha,
                RequestFingerprint = prepared.RequestFingerprint,
                ProviderId = "test-provider",
                ModelId = "test-model",
                StartedAt = Now,
                CompletedAt = Now.AddSeconds(1),
                IdempotencyKey = request.IdempotencyKey
            });
        }
    }

    private sealed record PreparedTurn(string RequestFingerprint) : IExecutionHeadlessAiPreparedTurn;

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
