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

    /// <summary>
    /// Resumes an approved proposal from its original durable HeadlessAi turn. The original
    /// model is not invoked again; Execution reloads the exact result and demands a fresh
    /// task lease and capability before the host executor can perform the operation.
    /// </summary>
    public async Task<ExecutionAgentOperationOutcome> ResumeApprovedAsync(
        string taskId, Guid leaseId, string turnId, string approvalId,
        string authorizedBranch, Guid capabilityId,
        CancellationToken cancellationToken = default)
    {
        var current = _tasks.GetSnapshot(taskId);
        if (current.Lease is not { } lease || lease.LeaseId != leaseId ||
            lease.ExpiresAt <= DateTimeOffset.UtcNow ||
            current.Status is not (WorkTaskStatus.Investigating or WorkTaskStatus.RepairRequired))
            throw new UnauthorizedAccessException("A current Investigating or RepairRequired task lease is required to resume an approved operation.");

        var turn = _turns.GetCompletedTurn(taskId, turnId)
            ?? throw new KeyNotFoundException("The original completed HeadlessAi turn was not found in the durable journal.");
        return await _operations.ResumeApprovedAsync(current, turn, approvalId,
            capabilityId, authorizedBranch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the next HeadlessAi repair turn from an already-consumed, host-verified
    /// actionable CiAi report. The report bytes, nested source-artifact identity, and exact
    /// workflow run are carried unchanged into HeadlessAi as opaque accepted context.
    /// </summary>
    public async Task<ExecutionAgentTurnOutcome> InvokeRepairAndRouteAsync(
        string taskId, Guid leaseId, string authorizedBranch, string candidateCommitSha,
        Guid capabilityId, string role, string instructions, string canonicalPrompt,
        string responseSchema, string priorTurnId, string priorCandidateCommitSha,
        CiAiDiagnosticReportContext diagnosticReport, TimeSpan? turnLifetime = null,
        TimeSpan? approvalLifetime = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnosticReport);
        var current = _tasks.GetSnapshot(taskId);
        var repairEvent = current.Events.LastOrDefault(item => item.Current == WorkTaskStatus.RepairRequired);
        if (current.Status != WorkTaskStatus.RepairRequired ||
            diagnosticReport.TaskId != taskId ||
            diagnosticReport.CorrelationId != current.Request.CorrelationId ||
            !string.Equals(diagnosticReport.Repository, current.Request.Repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(diagnosticReport.CandidateSha, priorCandidateCommitSha, StringComparison.OrdinalIgnoreCase) ||
            diagnosticReport.Verdict != "ActionableFailure" ||
            string.IsNullOrWhiteSpace(diagnosticReport.SourceArtifactIdentitySha256) ||
            repairEvent is null ||
            repairEvent.EvidenceSchema != WorkTaskRepairAttempt.RequiredEvidenceSchema ||
            !string.Equals(repairEvent.CandidateSha, diagnosticReport.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            repairEvent.CiRunId != diagnosticReport.WorkflowRunId ||
            repairEvent.CiRunAttempt != diagnosticReport.RunAttempt ||
            !string.Equals(repairEvent.EvidencePayloadSha256, diagnosticReport.AuthoritativeWorkflowEvidenceSha256, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Repair turn requires an already-authorized exact-candidate workflow-evidence v1 failure plus matching diagnostic report context.");

        return await InvokeAndRouteAsync(taskId, leaseId, authorizedBranch, candidateCommitSha,
            capabilityId, role, instructions, canonicalPrompt, responseSchema,
            new[] { diagnosticReport.HeadlessAiEvidence }, priorTurnId, priorCandidateCommitSha,
            turnLifetime, approvalLifetime, cancellationToken, diagnosticReport.AcceptedReport).ConfigureAwait(false);
    }

    public async Task<ExecutionAgentTurnOutcome> InvokeAndRouteAsync(
        string taskId, Guid leaseId, string authorizedBranch, string candidateCommitSha,
        Guid capabilityId, string role, string instructions, string canonicalPrompt,
        string responseSchema, IReadOnlyList<ExecutionHeadlessAiEvidence>? evidence = null,
        string? priorTurnId = null, string? priorCandidateCommitSha = null,
        TimeSpan? turnLifetime = null, TimeSpan? approvalLifetime = null,
        CancellationToken cancellationToken = default,
        ExecutionAcceptedCiAiReportContext? acceptedCiAiReport = null)
    {
        var turn = await _turns.InvokeAsync(taskId, leaseId, authorizedBranch, candidateCommitSha,
            role, instructions, canonicalPrompt, responseSchema, evidence, priorTurnId,
            priorCandidateCommitSha, turnLifetime, cancellationToken,
            acceptedCiAiReport).ConfigureAwait(false);

        // Re-read authoritative task state after the provider call; never route against the
        // stale snapshot captured before network I/O.
        var current = _tasks.GetSnapshot(taskId);
        var plan = await _operations.ProcessAsync(current, turn, capabilityId, authorizedBranch,
            approvalLifetime ?? TimeSpan.FromDays(1), cancellationToken).ConfigureAwait(false);
        return new ExecutionAgentTurnOutcome(turn, plan);
    }
}
