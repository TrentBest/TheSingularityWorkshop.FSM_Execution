namespace TheSingularityWorkshop.Execution;

/// <summary>
/// Host boundary for submitting an already-persisted CI workflow intent.
/// Implementations own GitHub credentials and transport; they must not infer
/// workflow trust from the candidate ref or from caller-supplied run metadata.
/// </summary>
public interface ICiAiWorkflowDispatcher
{
    /// <summary>
    /// Submits the registered workflow using the exact trusted workflow and
    /// candidate provenance in the mapping. GitHub dispatch normally does not
    /// return the run ID; callers must reconcile the resulting run separately.
    /// </summary>
    Task DispatchAsync(CiAiRequestTaskMapping mapping, CancellationToken cancellationToken);
}

public enum CiAiDispatchSubmissionOutcome
{
    SubmittedAwaitingReconciliation,
    ExistingIntentRequiresReconciliation,
    Conflict
}

public sealed record CiAiDispatchSubmissionResult(
    CiAiDispatchSubmissionOutcome Outcome,
    string RequestId);

/// <summary>
/// Persists a dispatch intent before crossing the host/network boundary.
/// It never resubmits an existing intent: after restart or an ambiguous transport
/// failure, the host must reconcile the exact run marker before considering a
/// separately authorized new dispatch.
/// </summary>
public static class CiAiDispatchSubmissionCoordinator
{
    public static Task<CiAiDispatchSubmissionResult> SubmitAsync(
        JsonlCiAiRequestTaskMappingJournal journal,
        CiAiRequestTaskMapping mapping,
        ICiAiWorkflowDispatcher dispatcher,
        CancellationToken cancellationToken = default) =>
        SubmitAsync(journal, mapping, dispatcher, null, cancellationToken);

    public static async Task<CiAiDispatchSubmissionResult> SubmitAsync(
        JsonlCiAiRequestTaskMappingJournal journal,
        CiAiRequestTaskMapping mapping,
        ICiAiWorkflowDispatcher dispatcher,
        IAgentOnboardingAdmissionVerifier? admissionVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(dispatcher);

        // Validate ordinary mapping fields before contacting the host. Governed targets
        // must pass CiAi's exact-candidate-SHA validator before a durable intent exists.
        mapping.Validate(requireAdmissionEvidence: false);
        if (AgentOnboardingTargetPolicy.RequiresAdmission(mapping.Repository))
        {
            if (admissionVerifier is null)
                throw new UnauthorizedAccessException("A trusted exact-SHA onboarding verifier is required for this CI dispatch.");
            var evidence = await admissionVerifier.VerifyAsync(mapping.Repository,
                mapping.CandidateSha, cancellationToken).ConfigureAwait(false);
            evidence.ValidateFor(mapping.Repository, mapping.CandidateSha);
            if (!evidence.IsEligible)
                throw new UnauthorizedAccessException("CiAi onboarding validation rejected this CI dispatch candidate.");
            mapping = mapping with { AdmissionEvidence = evidence };
        }
        mapping.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var intent = journal.CreateDispatchIntent(mapping);
        if (intent == CiAiRequestMappingAppendOutcome.Conflict)
            return new(CiAiDispatchSubmissionOutcome.Conflict, mapping.RequestId);
        if (intent == CiAiRequestMappingAppendOutcome.Duplicate)
            return new(CiAiDispatchSubmissionOutcome.ExistingIntentRequiresReconciliation, mapping.RequestId);

        // Revalidate after intent persistence and immediately before the network boundary.
        // A changed or revoked candidate remains durably pending but is never dispatched.
        if (AgentOnboardingTargetPolicy.RequiresAdmission(mapping.Repository))
        {
            var finalEvidence = await admissionVerifier!.VerifyAsync(mapping.Repository,
                mapping.CandidateSha, cancellationToken).ConfigureAwait(false);
            finalEvidence.ValidateFor(mapping.Repository, mapping.CandidateSha);
            if (!finalEvidence.IsEligible || finalEvidence != mapping.AdmissionEvidence)
                throw new UnauthorizedAccessException(
                    "Candidate onboarding evidence changed before CI dispatch; the durable intent was not dispatched.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await dispatcher.DispatchAsync(mapping, cancellationToken).ConfigureAwait(false);
        return new(CiAiDispatchSubmissionOutcome.SubmittedAwaitingReconciliation, mapping.RequestId);
    }
}
