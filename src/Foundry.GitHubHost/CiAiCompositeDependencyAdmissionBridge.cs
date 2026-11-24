using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>
/// Bridges verified leaf evidence into CiAi's authoritative AND/OR evaluator and
/// persists only that evaluator's three-valued result. It does not authenticate or
/// verify provider evidence; those remain host responsibilities.
/// </summary>
public static class CiAiCompositeDependencyAdmissionBridge
{
    /// <summary>
    /// Evaluates the exact persisted expression using CiAi's evaluator, then appends
    /// the result to the durable composite admission journal. The evidence digest must
    /// be computed by the host over the complete set of independently verified leaf
    /// evidence represented by <paramref name="verifiedLeafEvaluations"/>.
    /// </summary>
    public static CompositeDependencyAdmissionSnapshot EvaluateAndPersist(
        CiAiPipelineGateExpression expression,
        IReadOnlyDictionary<string, DependencyReleaseGateEvaluation> verifiedLeafEvaluations,
        CompositeDependencyAdmissionIdentity identity,
        string completeEvidenceSetSha256,
        long sequence,
        DateTimeOffset observedAt,
        JsonlCompositeDependencyAdmissionStore admissionStore)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(verifiedLeafEvaluations);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(admissionStore);
        identity.Validate();

        if (!string.Equals(expression.Pipeline, identity.Pipeline, StringComparison.Ordinal) ||
            !string.Equals(expression.DefinitionSha256, identity.ExpressionSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The evaluated CiAi expression does not match the task's persisted pipeline and canonical expression digest.");

        var declaredGateIds = expression.Leaves
            .Select(leaf => leaf.GateId)
            .ToHashSet(StringComparer.Ordinal);
        if (verifiedLeafEvaluations.Keys.Any(gateId => !declaredGateIds.Contains(gateId)))
            throw new InvalidDataException("Leaf evidence contains a gate identity not declared by the persisted CiAi expression.");

        foreach (var (gateId, evaluation) in verifiedLeafEvaluations)
        {
            if (evaluation is null || !declaredGateIds.Contains(gateId))
                throw new InvalidDataException("Leaf evidence is null or does not belong to the persisted CiAi expression.");
        }

        var evaluated = CiAiPipelineGateEvaluator.Evaluate(expression, verifiedLeafEvaluations);
        var persisted = evaluated switch
        {
            CiAiPipelineGateState.Waiting => CompositeDependencyAdmissionState.Waiting,
            CiAiPipelineGateState.Satisfied => CompositeDependencyAdmissionState.Satisfied,
            CiAiPipelineGateState.Blocked => CompositeDependencyAdmissionState.Blocked,
            _ => throw new InvalidDataException("CiAi returned an unsupported composite gate state.")
        };

        return admissionStore.ApplyVerifiedDecision(identity, persisted, sequence,
            completeEvidenceSetSha256, observedAt);
    }
}
