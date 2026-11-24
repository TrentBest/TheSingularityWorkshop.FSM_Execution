namespace TheSingularityWorkshop.Execution;

/// <summary>
/// One bounded model turn and its ordered operation disposition. A turn is persisted by
/// ExecutionHeadlessAiTurnCoordinator before this orchestrator asks the operation router
/// to execute, escalate, or reject any proposed operation.
/// </summary>
public sealed record ExecutionAgentTurnOutcome(
    ExecutionHeadlessAiTurnResult Turn,
    ExecutionAgentOperationPlanResult OperationPlan);

/// <summary>
/// Host-facing composition of the existing single-call HeadlessAi turn coordinator and the
/// capability/approval operation router. The host still supplies the provider client, task lease,
/// exact candidate identity, capability, response schema, and verified evidence references.
/// </summary>
public sealed class ExecutionAgentTurnOrchestrator
{
    private readonly PersistentWorkTaskCoordinator _tasks;
    private readonly ExecutionHeadlessAiTurnCoordinator _turns;
    private readonly ExecutionAgentOperationRouter _operations;

    public ExecutionAgentTurnOrchestrator(PersistentWorkTaskCoordinator tasks,
        ExecutionHeadlessAiTurnCoordinator turns, ExecutionAgentOperationRouter operations)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _turns = turns ?? throw new ArgumentNullException(nameof(turns));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public async Task<ExecutionAgentTurnOutcome> InvokeAndRouteAsync(
        string taskId, Guid leaseId, string authorizedBranch, string candidateCommitSha,
        Guid capabilityId, string role, string instructions, string canonicalPrompt,
        string responseSchema, IReadOnlyList<ExecutionHeadlessAiEvidence>? evidence = null,
        string? priorTurnId = null, string? priorCandidateCommitSha = null,
        TimeSpan? turnLifetime = null, TimeSpan? approvalLifetime = null,
        CancellationToken cancellationToken = default)
    {
        var turn = await _turns.InvokeAsync(taskId, leaseId, authorizedBranch, candidateCommitSha,
            role, instructions, canonicalPrompt, responseSchema, evidence, priorTurnId,
            priorCandidateCommitSha, turnLifetime, cancellationToken).ConfigureAwait(false);

        // Re-read authoritative task state after the provider call; never route against the
        // stale snapshot captured before network I/O.
        var current = _tasks.GetSnapshot(taskId);
        var plan = await _operations.ProcessAsync(current, turn, capabilityId, authorizedBranch,
            approvalLifetime ?? TimeSpan.FromDays(1), cancellationToken).ConfigureAwait(false);
        return new ExecutionAgentTurnOutcome(turn, plan);
    }
}
