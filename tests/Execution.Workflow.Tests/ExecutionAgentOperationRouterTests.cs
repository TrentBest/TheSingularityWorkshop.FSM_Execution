using TheSingularityWorkshop.Execution;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class ExecutionAgentOperationRouterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 5, 0, 0, TimeSpan.Zero);
    private const string BaseSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CandidateSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static (WorkTaskSnapshot Task, WorkTaskCapabilityRegistry Registry, WorkTaskCapability Capability)
        CreateActiveTask(IReadOnlyCollection<WorkTaskAction>? actions = null)
    {
        var coordinator = new WorkTaskCoordinator();
        var request = new WorkTaskRequest(1, "task-1", "correlation-1", "TrentBest/Example",
            1, "https://github.com/TrentBest/Example/issues/1", BaseSha, ["src"], 3);
        coordinator.Submit(request);
        var claimed = coordinator.Claim("task-1", "agent-1", TimeSpan.FromMinutes(30), Now);
        var task = coordinator.Transition("task-1", claimed.Lease!.LeaseId,
            WorkTaskStatus.Investigating, Now.AddSeconds(1), "Begin investigation.");
        var registry = new WorkTaskCapabilityRegistry();
        var capability = registry.Grant(task, "agent/task-1", CandidateSha, ["src"],
            actions ?? [WorkTaskAction.ReadRepository], Now.AddSeconds(2), TimeSpan.FromMinutes(10));
        return (task, registry, capability);
    }

    private static ExecutionHeadlessAiTurnResult Turn(params ExecutionHeadlessAiOperationProposal[] operations)
        => new()
        {
            TurnId = "turn-1",
            TaskId = "task-1",
            CorrelationId = "correlation-1",
            Repository = "TrentBest/Example",
            AuthorizedBranch = "agent/task-1",
            BaseCommitSha = BaseSha,
            CandidateCommitSha = CandidateSha,
            RequestFingerprint = new string('c', 64),
            ProviderId = "openai-responses",
            ModelId = "configured-model",
            StartedAt = Now.AddSeconds(2),
            CompletedAt = Now.AddSeconds(3),
            Operations = operations
        };

    private static ExecutionHeadlessAiOperationProposal ReadProposal() => new()
    {
        Operation = nameof(WorkTaskAction.ReadRepository),
        Summary = "Read the failing registration source",
        Rationale = "The report points to a missing registration.",
        ArgumentsJson = "{\"path\":\"src/Registration.cs\"}"
    };

    private static ExecutionHeadlessAiOperationProposal DraftProposal() => new()
    {
        Operation = nameof(WorkTaskAction.CreateDraftPullRequest),
        Summary = "Open a draft pull request",
        Rationale = "The candidate should be reviewed by the owner.",
        ArgumentsJson = "{\"title\":\"Repair registration failure\",\"draft\":true}"
    };

    [Fact]
    public async Task ExecutesOnlyHostPermittedActionWithinCurrentCapability()
    {
        var (task, registry, capability) = CreateActiveTask();
        var path = Path.Combine(Path.GetTempPath(), $"adp-router-{Guid.NewGuid():N}.jsonl");
        try
        {
            var approvals = new JsonlHumanApprovalRequestStore(path);
            var executor = new FakeExecutor();
            var router = new ExecutionAgentOperationRouter(registry,
                new FakePolicy(ExecutionAgentOperationPolicyDecision.Execute),
                executor, approvals, new FixedTimeProvider(Now.AddSeconds(4)));

            var result = await router.ProcessAsync(task, Turn(ReadProposal()), capability.CapabilityId,
                "agent/task-1", TimeSpan.FromDays(1));

            Assert.Equal(ExecutionAgentOperationDisposition.Executed, Assert.Single(result.Outcomes).Disposition);
            Assert.Equal(1, executor.CallCount);
            Assert.Empty(approvals.GetPending());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task HumanApprovalPolicyCreatesDurableRequestWithoutExecutingOperation()
    {
        var (task, registry, capability) = CreateActiveTask();
        var path = Path.Combine(Path.GetTempPath(), $"adp-router-{Guid.NewGuid():N}.jsonl");
        try
        {
            var approvals = new JsonlHumanApprovalRequestStore(path);
            var executor = new FakeExecutor();
            var router = new ExecutionAgentOperationRouter(registry,
                new FakePolicy(ExecutionAgentOperationPolicyDecision.RequiresHumanApproval),
                executor, approvals, new FixedTimeProvider(Now.AddSeconds(4)));

            var result = await router.ProcessAsync(task, Turn(DraftProposal()), capability.CapabilityId,
                "agent/task-1", TimeSpan.FromDays(1));

            var outcome = Assert.Single(result.Outcomes);
            Assert.Equal(ExecutionAgentOperationDisposition.AwaitingHumanApproval, outcome.Disposition);
            Assert.NotNull(outcome.ApprovalId);
            Assert.Single(approvals.GetPending());
            Assert.Equal(0, executor.CallCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task StopsPlanAtHumanApprovalBarrierAndDefersFollowingOperations()
    {
        var (task, registry, capability) = CreateActiveTask();
        var path = Path.Combine(Path.GetTempPath(), $"adp-router-{Guid.NewGuid():N}.jsonl");
        try
        {
            var approvals = new JsonlHumanApprovalRequestStore(path);
            var executor = new FakeExecutor();
            var router = new ExecutionAgentOperationRouter(registry,
                new FakePolicy(ExecutionAgentOperationPolicyDecision.RequiresHumanApproval),
                executor, approvals, new FixedTimeProvider(Now.AddSeconds(4)));

            var result = await router.ProcessAsync(task, Turn(DraftProposal(), ReadProposal()),
                capability.CapabilityId, "agent/task-1", TimeSpan.FromDays(1));

            Assert.Equal(2, result.Outcomes.Count);
            Assert.Equal(ExecutionAgentOperationDisposition.AwaitingHumanApproval, result.Outcomes[0].Disposition);
            Assert.Equal(ExecutionAgentOperationDisposition.Deferred, result.Outcomes[1].Disposition);
            Assert.Equal(0, executor.CallCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task RejectsUnknownOperationsAndCapabilityEscalation()
    {
        var (task, registry, capability) = CreateActiveTask();
        var path = Path.Combine(Path.GetTempPath(), $"adp-router-{Guid.NewGuid():N}.jsonl");
        try
        {
            var approvals = new JsonlHumanApprovalRequestStore(path);
            var executor = new FakeExecutor();
            var router = new ExecutionAgentOperationRouter(registry,
                new FakePolicy(ExecutionAgentOperationPolicyDecision.Execute),
                executor, approvals, new FixedTimeProvider(Now.AddSeconds(4)));
            var unknown = ReadProposal() with { Operation = "DeleteRepository" };
            var unknownResult = await router.ProcessAsync(task, Turn(unknown), capability.CapabilityId,
                "agent/task-1", TimeSpan.FromDays(1));
            Assert.Equal(ExecutionAgentOperationDisposition.Rejected, Assert.Single(unknownResult.Outcomes).Disposition);

            var unauthorized = await router.ProcessAsync(task, Turn(DraftProposal()), capability.CapabilityId,
                "agent/task-1", TimeSpan.FromDays(1));
            Assert.Equal(ExecutionAgentOperationDisposition.Rejected, Assert.Single(unauthorized.Outcomes).Disposition);
            Assert.Equal(0, executor.CallCount);
            Assert.Empty(approvals.GetPending());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ReplayedTurnReturnsTheSamePendingApprovalWithoutDuplicatingIt()
    {
        var (task, registry, capability) = CreateActiveTask();
        var path = Path.Combine(Path.GetTempPath(), $"adp-router-{Guid.NewGuid():N}.jsonl");
        try
        {
            var approvals = new JsonlHumanApprovalRequestStore(path);
            var executor = new FakeExecutor();
            var turn = Turn(DraftProposal());
            var firstRouter = new ExecutionAgentOperationRouter(registry,
                new FakePolicy(ExecutionAgentOperationPolicyDecision.RequiresHumanApproval),
                executor, approvals, new FixedTimeProvider(Now.AddSeconds(4)));
            var secondRouter = new ExecutionAgentOperationRouter(registry,
                new FakePolicy(ExecutionAgentOperationPolicyDecision.RequiresHumanApproval),
                executor, approvals, new FixedTimeProvider(Now.AddSeconds(5)));
            var first = await firstRouter.ProcessAsync(task, turn, capability.CapabilityId,
                "agent/task-1", TimeSpan.FromDays(1));
            var second = await secondRouter.ProcessAsync(task, turn, capability.CapabilityId,
                "agent/task-1", TimeSpan.FromDays(1));
            Assert.Equal(first.Outcomes[0].ApprovalId, second.Outcomes[0].ApprovalId);
            Assert.Single(approvals.GetPending());
            Assert.Equal(0, executor.CallCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task ApprovedProposalResumesFromExactTurnAndExecutesOnlyOnce()
    {
        var (task, registry, capability) = CreateActiveTask([WorkTaskAction.CreateDraftPullRequest]);
        var path = Path.Combine(Path.GetTempPath(), $"adp-router-{Guid.NewGuid():N}.jsonl");
        try
        {
            var approvals = new JsonlHumanApprovalRequestStore(path);
            var executor = new FakeExecutor();
            var turn = Turn(DraftProposal());
            var router = new ExecutionAgentOperationRouter(registry,
                new FakePolicy(ExecutionAgentOperationPolicyDecision.RequiresHumanApproval),
                executor, approvals, new FixedTimeProvider(Now.AddSeconds(4)));

            var pending = await router.ProcessAsync(task, turn, capability.CapabilityId,
                "agent/task-1", TimeSpan.FromDays(1));
            var approvalId = Assert.Single(pending.Outcomes).ApprovalId!;
            var approval = approvals.Get(approvalId);
            approvals.Decide(approvalId, HumanApprovalStatus.Approved,
                "human:owner", "Reviewed the exact candidate proposal.", approval.CreatedAt.AddSeconds(1));
            var resumeRouter = new ExecutionAgentOperationRouter(registry,
                new FakePolicy(ExecutionAgentOperationPolicyDecision.RequiresHumanApproval),
                executor, approvals, new FixedTimeProvider(approval.CreatedAt.AddSeconds(2)));

            var resumed = await resumeRouter.ResumeApprovedAsync(task, turn, approvalId,
                capability.CapabilityId, "agent/task-1");
            Assert.Equal(ExecutionAgentOperationDisposition.Executed, resumed.Disposition);
            Assert.Equal(1, executor.CallCount);
            Assert.Equal(HumanApprovalStatus.Executed, approvals.Get(approvalId).Status);

            var duplicate = await resumeRouter.ResumeApprovedAsync(task, turn, approvalId,
                capability.CapabilityId, "agent/task-1");
            Assert.Equal(ExecutionAgentOperationDisposition.Executed, duplicate.Disposition);
            Assert.Equal(1, executor.CallCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ApprovedProposalFailsClosedWhenTheFreshCapabilityDoesNotCoverIt()
    {
        var (task, registry, capability) = CreateActiveTask();
        var path = Path.Combine(Path.GetTempPath(), $"adp-router-{Guid.NewGuid():N}.jsonl");
        try
        {
            var approvals = new JsonlHumanApprovalRequestStore(path);
            var executor = new FakeExecutor();
            var turn = Turn(DraftProposal());
            var router = new ExecutionAgentOperationRouter(registry,
                new FakePolicy(ExecutionAgentOperationPolicyDecision.RequiresHumanApproval),
                executor, approvals, new FixedTimeProvider(Now.AddSeconds(4)));
            var pending = await router.ProcessAsync(task, turn, capability.CapabilityId,
                "agent/task-1", TimeSpan.FromDays(1));
            var approvalId = Assert.Single(pending.Outcomes).ApprovalId!;
            var approval = approvals.Get(approvalId);
            approvals.Decide(approvalId, HumanApprovalStatus.Approved,
                "human:owner", "Approved.", approval.CreatedAt.AddSeconds(1));
            var resumeRouter = new ExecutionAgentOperationRouter(registry,
                new FakePolicy(ExecutionAgentOperationPolicyDecision.RequiresHumanApproval),
                executor, approvals, new FixedTimeProvider(approval.CreatedAt.AddSeconds(2)));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                resumeRouter.ResumeApprovedAsync(task, turn, approvalId, capability.CapabilityId,
                    "agent/task-1"));
            Assert.Equal(HumanApprovalStatus.Approved, approvals.Get(approvalId).Status);
            Assert.Equal(0, executor.CallCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class FakePolicy(ExecutionAgentOperationPolicyDecision decision) : IExecutionAgentOperationPolicy
    {
        public ExecutionAgentOperationPolicyResult Classify(WorkTaskSnapshot task,
            ExecutionHeadlessAiTurnResult turn, WorkTaskAction action,
            ExecutionHeadlessAiOperationProposal proposal)
            => new(decision, decision == ExecutionAgentOperationPolicyDecision.Execute
                ? "Action is within the delegated policy." : "Human authority is required.");
    }

    private sealed class FakeExecutor : IExecutionAgentOperationExecutor
    {
        public int CallCount { get; private set; }
        public Task<string?> ExecuteAsync(ExecutionAgentOperationContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<string?>("Executed " + context.Action);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
