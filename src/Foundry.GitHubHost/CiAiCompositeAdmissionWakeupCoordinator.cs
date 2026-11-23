using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.FSM_Execution;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>One authoritative CiAi decision and its optional durable task wake-up.</summary>
public sealed record CiAiCompositeAdmissionWakeupResult(
    CompositeDependencyAdmissionSnapshot Admission,
    CiAiTaskWakeup? Wakeup);

/// <summary>
/// Applies a complete set of host-verified leaf evaluations to the task's persisted
/// composite admission. CiAi remains the sole AND/OR evaluator. A wake-up is appended
/// only after the Satisfied decision is durable; startup reconciliation repairs a crash
/// between those two journal writes.
/// </summary>
public sealed class CiAiCompositeAdmissionWakeupCoordinator
{
    private readonly JsonlCompositeDependencyAdmissionStore _admissions;
    private readonly JsonlCiAiTaskWakeupOutbox _outbox;

    public CiAiCompositeAdmissionWakeupCoordinator(
        JsonlCompositeDependencyAdmissionStore admissions,
        JsonlCiAiTaskWakeupOutbox outbox)
    {
        _admissions = admissions ?? throw new ArgumentNullException(nameof(admissions));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    }

    /// <summary>
    /// Call only after the host has authenticated the provider event and independently
    /// verified every supplied leaf evaluation and the complete evidence-set digest.
    /// The expression must be the canonical expression registered for this task.
    /// </summary>
    public CiAiCompositeAdmissionWakeupResult ApplyVerifiedEvidence(
        CiAiPipelineGateExpression expression,
        IReadOnlyDictionary<string, DependencyReleaseGateEvaluation> verifiedLeafEvaluations,
        CompositeDependencyAdmissionIdentity identity,
        string completeEvidenceSetSha256,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(verifiedLeafEvaluations);
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        if (observedAt == default)
            throw new ArgumentException("A decision timestamp is required.", nameof(observedAt));

        var current = _admissions.Get(identity.TaskId)
            ?? throw new KeyNotFoundException($"No durable composite admission exists for task '{identity.TaskId}'.");
        if (current.Identity != identity)
            throw new InvalidDataException("Verified evidence does not match the task's immutable composite admission identity.");
        if (current.State != CompositeDependencyAdmissionState.Waiting)
            throw new InvalidOperationException("A terminal composite admission cannot be reevaluated.");

        var decision = CiAiCompositeDependencyAdmissionBridge.EvaluateAndPersist(
            expression, verifiedLeafEvaluations, identity, completeEvidenceSetSha256,
            checked(current.Sequence + 1), observedAt, _admissions);

        // The admission journal is authoritative. A crash before this append is repaired
        // by ReconcileSatisfiedAdmissions during host startup.
        var wakeup = decision.State == CompositeDependencyAdmissionState.Satisfied
            ? _outbox.EnqueueSatisfied(decision, observedAt)
            : null;
        return new CiAiCompositeAdmissionWakeupResult(decision, wakeup);
    }

    /// <summary>
    /// Repairs the admission-to-outbox crash window. Delivered and pending events remain
    /// idempotent; Waiting and Blocked admissions never produce events.
    /// </summary>
    public IReadOnlyList<CiAiTaskWakeup> ReconcileSatisfiedAdmissions(DateTimeOffset now)
    {
        if (now == default)
            throw new ArgumentException("A reconciliation timestamp is required.", nameof(now));
        return _outbox.ReconcileSatisfiedAdmissions(_admissions, now);
    }
}
