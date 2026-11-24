using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.FSM_Execution;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>One authoritative CiAi decision and its optional durable task wake-up.</summary>
public sealed record CiAiCompositeAdmissionWakeupResult(
    CompositeDependencyAdmissionSnapshot Admission,
    CiAiTaskWakeup? Wakeup);

/// <summary>Complete host-verified leaf set and its canonical immutable evidence-set digest for one task.</summary>
public sealed record CiAiVerifiedCompositeEvidenceSet(
    IReadOnlyDictionary<string, DependencyReleaseGateEvaluation> LeafEvaluations,
    string EvidenceSetSha256);

/// <summary>
/// Applies complete host-verified leaf evaluations to a task's immutable registered
/// expression. CiAi remains the sole AND/OR evaluator. A wake-up is appended only after
/// Satisfied is durable; startup reconciliation repairs the journal-to-outbox crash window.
/// </summary>
public sealed class CiAiCompositeAdmissionWakeupCoordinator
{
    private readonly JsonlCompositeDependencyAdmissionStore _admissions;
    private readonly JsonlCiAiTaskWakeupOutbox _outbox;
    private readonly JsonlCiAiCompositeExpressionRegistry? _expressions;

    public CiAiCompositeAdmissionWakeupCoordinator(
        JsonlCompositeDependencyAdmissionStore admissions,
        JsonlCiAiTaskWakeupOutbox outbox,
        JsonlCiAiCompositeExpressionRegistry? expressions = null)
    {
        _admissions = admissions ?? throw new ArgumentNullException(nameof(admissions));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _expressions = expressions;
    }

    /// <summary>Durably binds a task identity to its canonical expression before admission.</summary>
    public CiAiRegisteredCompositeExpression RegisterExpression(
        CompositeDependencyAdmissionIdentity identity,
        CiAiPipelineGateExpression expression)
    {
        var registry = _expressions ?? throw new InvalidOperationException(
            "A durable CiAi expression registry is required before event-driven reevaluation.");
        return registry.Register(identity, expression);
    }

    /// <summary>
    /// Reevaluate only tasks whose persisted expressions depend on the verified event's
    /// gate ID. The caller must authenticate the event and supply the complete current
    /// host-verified leaf-evidence set for each dependent task. The event cannot supply
    /// or replace the expression.
    /// </summary>
    public IReadOnlyList<CiAiCompositeAdmissionWakeupResult> ApplyVerifiedGateEvent(
        string gateId,
        Func<CiAiRegisteredCompositeExpression, CiAiVerifiedCompositeEvidenceSet> resolveCompleteVerifiedEvidence,
        DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateId);
        ArgumentNullException.ThrowIfNull(resolveCompleteVerifiedEvidence);
        if (observedAt == default)
            throw new ArgumentException("A decision timestamp is required.", nameof(observedAt));
        var registry = _expressions ?? throw new InvalidOperationException(
            "A durable CiAi expression registry is required for event-driven reevaluation.");

        var results = new List<CiAiCompositeAdmissionWakeupResult>();
        foreach (var registration in registry.FindDependents(gateId))
        {
            var current = _admissions.Get(registration.Identity.TaskId)
                ?? throw new InvalidDataException(
                    "A registered expression has no corresponding durable composite admission.");
            if (current.Identity != registration.Identity)
                throw new InvalidDataException(
                    "Registered expression identity does not match the durable task admission.");
            if (current.State != CompositeDependencyAdmissionState.Waiting)
                continue;

            // Resolve evidence per task: dependent expressions may declare different leaves
            // and therefore require distinct complete evidence-set digests.
            var evidence = resolveCompleteVerifiedEvidence(registration)
                ?? throw new InvalidDataException("The host evidence resolver returned no evidence set.");
            ArgumentNullException.ThrowIfNull(evidence.LeafEvaluations);
            if (!evidence.LeafEvaluations.ContainsKey(gateId))
                throw new InvalidDataException(
                    "The task's complete verified evidence set does not contain the triggering gate.");

            results.Add(ApplyVerifiedEvidence(registration.Expression,
                evidence.LeafEvaluations, registration.Identity,
                evidence.EvidenceSetSha256, observedAt));
        }

        return Array.AsReadOnly(results.ToArray());
    }

    /// <summary>
    /// Applies a complete set of host-verified leaf evaluations to the task's registered
    /// expression. Call only after authentication and independent evidence verification.
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

        if (_expressions is not null)
        {
            var registered = _expressions.Get(identity.TaskId);
            if (registered.Identity != identity ||
                !string.Equals(registered.Expression.DefinitionSha256,
                    expression.DefinitionSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Evaluation expression is not the immutable expression registered for this task.");
            expression = registered.Expression;
        }

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
