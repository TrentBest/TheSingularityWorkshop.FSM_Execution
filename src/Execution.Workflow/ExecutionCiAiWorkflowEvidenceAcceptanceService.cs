using System.Security.Cryptography;
using System.Text;
using TheSingularityWorkshop.CiAi;

namespace TheSingularityWorkshop.Execution;

/// <summary>
/// Composes the authenticated notice/retrieval receipt, independent provider observations,
/// CiAi's canonical V1 packet parser, protected workflow trust, and durable packet staging.
/// Returning from this method means the exact packet bytes have been durably staged; it does
/// not itself authorize a task transition or a repair dispatch.
/// </summary>
public static class ExecutionCiAiWorkflowEvidenceAcceptanceService
{
    /// <summary>
    /// Host-driven evidence path: query GitHub independently, validate the provider provenance
    /// artifact, build the canonical packet with CiAi's own factory, round-trip it through CiAi's
    /// parser, and durably stage the exact bytes. No model-supplied packet or verdict is accepted.
    /// </summary>
    public static ExecutionAcceptedCiAiWorkflowEvidence ObserveAndAccept(
        CiAiRequestTaskMappingSnapshot request,
        ExecutionObservedCiAiProviderRun independentlyObservedRun,
        ReadOnlyMemory<byte> providerProvenanceArchiveBytes,
        CiWorkflowObserverProfile observerProfile,
        ReadOnlyMemory<byte> workflowBytesAtRunRevision,
        ReadOnlyMemory<byte> workflowBytesAtApprovedRevision,
        JsonlCiAiWorkflowEvidencePacketStore durableStore,
        DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(independentlyObservedRun);
        ArgumentNullException.ThrowIfNull(observerProfile);
        ArgumentNullException.ThrowIfNull(durableStore);
        if (receivedAt == default)
            throw new ArgumentException("Evidence receipt time is required.", nameof(receivedAt));

        var mapping = request.Mapping.Validate();
        var runBinding = request.RunBinding
            ?? throw new InvalidOperationException("Provider evidence requires a persisted exact-run binding.");
        runBinding.ValidateAgainst(mapping);
        if (request.Cancelled)
            throw new InvalidOperationException("Cancelled dispatch intent cannot produce actionable evidence.");
        if (mapping.JobKey is null ||
            observerProfile.WorkflowPath != mapping.WorkflowPath ||
            observerProfile.RequiredJobKey != mapping.JobKey ||
            observerProfile.RequiredJobName != mapping.JobName)
            throw new UnauthorizedAccessException("Observer profile does not match the persisted workflow and required-check mapping.");

        var observedRun = independentlyObservedRun.Run;
        if (observedRun.RunId != runBinding.RunId ||
            observedRun.RunAttempt != runBinding.RunAttempt ||
            !string.Equals(observedRun.Repository, mapping.Repository, StringComparison.OrdinalIgnoreCase) ||
            observedRun.WorkflowPath != mapping.WorkflowPath ||
            observedRun.WorkflowRef != mapping.TrustedWorkflowRef ||
            !string.Equals(observedRun.WorkflowSha, mapping.TrustedWorkflowSha, StringComparison.OrdinalIgnoreCase) ||
            observedRun.Event != runBinding.Event)
            throw new UnauthorizedAccessException("Independent provider run observation does not match the persisted dispatch binding.");

        ValidateProviderArtifact(
            independentlyObservedRun.ProvenanceArtifact, mapping, runBinding,
            observerProfile.ProvenanceArtifactName(runBinding.RunId, runBinding.RunAttempt), receivedAt);
        ValidateProviderArtifact(
            independentlyObservedRun.TestResultsArtifact, mapping, runBinding,
            observerProfile.TestArtifactName(runBinding.RunId, runBinding.RunAttempt), receivedAt);

        var expectedEventSha = mapping.TrustedEventSha ?? mapping.TrustedWorkflowSha;
        var provenanceEvidence = CiWorkflowProvenanceArtifactReader.Read(
            providerProvenanceArchiveBytes,
            independentlyObservedRun.ProvenanceArtifact,
            observerProfile,
            mapping.Repository,
            runBinding.RunId,
            runBinding.RunAttempt,
            mapping.TrustedWorkflowSha,
            expectedEventSha,
            mapping.CandidateRef,
            mapping.CandidateSha,
            mapping.TrustedWorkflowRef,
            mapping.TrustedWorkflowSha,
            runBinding.Event,
            mapping.TrustedWorkflowRef);

        var trustPolicy = new CiWorkflowDefinitionTrustPolicy(
            mapping.Repository, mapping.WorkflowPath,
            mapping.TrustedWorkflowRef, mapping.TrustedWorkflowSha);
        var trustDecision = trustPolicy.Evaluate(
            provenanceEvidence.Provenance,
            workflowBytesAtRunRevision,
            workflowBytesAtApprovedRevision);
        var requiredChecks = new[]
        {
            new CiWorkflowRequiredCheckBinding(mapping.JobKey, mapping.JobName)
        };

        var checkoutEvidence = new CiWorkflowCandidateCheckoutEvidence(
            mapping.CandidateRef, mapping.CandidateSha, mapping.JobName,
            mapping.ResolutionStepName, mapping.VerificationStepName);
        var packet = CiWorkflowEvidencePacket.FromValidatedProvenance(
            observedRun,
            provenanceEvidence.Provenance,
            trustDecision,
            requiredChecks,
            candidateCheckoutEvidence: checkoutEvidence);
        var packetBytes = new UTF8Encoding(false, true).GetBytes(packet.ToJson());
        var packetSha256 = Convert.ToHexString(SHA256.HashData(packetBytes)).ToLowerInvariant();
        var expectedWorkflowRef =
            mapping.Repository + "/" + mapping.WorkflowPath + "@" + mapping.TrustedWorkflowRef;

        return CiAiWorkflowEvidencePacketConsumer.Consume(
            packetBytes,
            mapping.Repository,
            runBinding.RunId,
            runBinding.RunAttempt,
            packetSha256,
            requiredChecks,
            expectedWorkflowRef,
            mapping.TrustedWorkflowSha,
            mapping.CandidateRef,
            mapping.CandidateSha,
            runBinding.Event,
            mapping.TrustedWorkflowRef,
            expectedEventSha,
            observedRun,
            provenanceEvidence,
            trustPolicy,
            workflowBytesAtRunRevision,
            workflowBytesAtApprovedRevision,
            durableStore,
            mapping.TaskId,
            mapping.CorrelationId,
            receivedAt);
    }

    private static void ValidateProviderArtifact(
        CiProviderArtifactObservation artifact,
        CiAiRequestTaskMapping mapping,
        CiAiDispatchRunBinding runBinding,
        string expectedArtifactName,
        DateTimeOffset now)
    {
        if (!string.Equals(artifact.Repository, mapping.Repository, StringComparison.OrdinalIgnoreCase) ||
            artifact.RunId != runBinding.RunId ||
            !string.Equals(artifact.HeadSha, mapping.TrustedWorkflowSha, StringComparison.OrdinalIgnoreCase) ||
            artifact.ArtifactName != expectedArtifactName ||
            artifact.IsExpired || artifact.ExpiresAt <= now ||
            artifact.ArchiveSizeBytes is <= 0 or > CiTestResultArtifactArchive.DefaultMaximumArchiveBytes)
            throw new UnauthorizedAccessException("Provider artifact metadata does not match the exact registered run and observer profile.");
    }

    public static ExecutionAcceptedCiAiWorkflowEvidence Accept(
        CiAiArtifactNoticeIngressResult ingress,
        CiAiVerifiedArtifactContent retrievedArtifact,
        ExecutionObservedCiAiProviderRun independentlyObservedRun,
        ReadOnlyMemory<byte> providerProvenanceArchiveBytes,
        CiWorkflowObserverProfile observerProfile,
        string expectedCapabilityIdentity,
        string expectedCapabilityVersion,
        string expectedImplementationRevision,
        ReadOnlyMemory<byte> workflowBytesAtRunRevision,
        ReadOnlyMemory<byte> workflowBytesAtApprovedRevision,
        JsonlCiAiWorkflowEvidencePacketStore durableStore,
        DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(ingress);
        ArgumentNullException.ThrowIfNull(retrievedArtifact);
        ArgumentNullException.ThrowIfNull(independentlyObservedRun);
        ArgumentNullException.ThrowIfNull(observerProfile);
        ArgumentNullException.ThrowIfNull(durableStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCapabilityIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCapabilityVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedImplementationRevision);
        if (receivedAt == default)
            throw new ArgumentException("Evidence receipt time is required.", nameof(receivedAt));

        var notice = ingress.ValidatedNotice;
        if (!string.Equals(notice.CapabilityIdentity, expectedCapabilityIdentity, StringComparison.Ordinal) ||
            !string.Equals(notice.CapabilityVersion, expectedCapabilityVersion, StringComparison.Ordinal) ||
            !string.Equals(notice.ImplementationRevision, expectedImplementationRevision, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Artifact-ready notice does not identify the registered workflow-evidence capability revision.");

        if (!string.Equals(retrievedArtifact.Notice.NoticeId, notice.NoticeId, StringComparison.Ordinal) ||
            !string.Equals(retrievedArtifact.Notice.PayloadSha256, notice.PayloadSha256, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Retrieved artifact is not bound to the exact authenticated and validated ready notice.");

        // Verify the notice receipt and exact retrieved bytes before interpreting the JSON packet.
        var verifiedArtifact = ExecutionVerifiedCiAiReportArtifact.Verify(
            retrievedArtifact, ingress.Receipt, receivedAt);
        var request = verifiedArtifact.Request;
        var mapping = request.Mapping.Validate();
        var runBinding = request.RunBinding
            ?? throw new InvalidOperationException("Workflow evidence requires a persisted exact-run binding.");
        runBinding.ValidateAgainst(mapping);

        if (mapping.JobKey is null)
            throw new InvalidOperationException("Workflow evidence acceptance requires a host-registered required-check key.");
        if (observerProfile.WorkflowPath != mapping.WorkflowPath ||
            observerProfile.RequiredJobKey != mapping.JobKey ||
            observerProfile.RequiredJobName != mapping.JobName)
            throw new UnauthorizedAccessException("Observer profile does not match the host-registered workflow and required-check mapping.");

        var expectedEventSha = mapping.TrustedEventSha ?? mapping.TrustedWorkflowSha;
        var independentlyObservedProvenance = CiWorkflowProvenanceArtifactReader.Read(
            providerProvenanceArchiveBytes,
            independentlyObservedRun.ProvenanceArtifact,
            observerProfile,
            mapping.Repository,
            runBinding.RunId,
            runBinding.RunAttempt,
            mapping.TrustedWorkflowSha,
            expectedEventSha,
            mapping.CandidateRef,
            mapping.CandidateSha,
            mapping.TrustedWorkflowRef,
            mapping.TrustedWorkflowSha,
            runBinding.Event,
            mapping.TrustedWorkflowRef);

        var requiredChecks = new[]
        {
            new CiWorkflowRequiredCheckBinding(mapping.JobKey, mapping.JobName)
        };
        var protectedWorkflowPolicy = new CiWorkflowDefinitionTrustPolicy(
            mapping.Repository, mapping.WorkflowPath,
            mapping.TrustedWorkflowRef, mapping.TrustedWorkflowSha);
        var expectedWorkflowRef =
            mapping.Repository + "/" + mapping.WorkflowPath + "@" + mapping.TrustedWorkflowRef;

        return CiAiWorkflowEvidencePacketConsumer.Consume(
            verifiedArtifact.Bytes,
            mapping.Repository,
            runBinding.RunId,
            runBinding.RunAttempt,
            verifiedArtifact.Digest,
            requiredChecks,
            expectedWorkflowRef,
            mapping.TrustedWorkflowSha,
            mapping.CandidateRef,
            mapping.CandidateSha,
            runBinding.Event,
            mapping.TrustedWorkflowRef,
            expectedEventSha,
            independentlyObservedRun.Run,
            independentlyObservedProvenance,
            protectedWorkflowPolicy,
            workflowBytesAtRunRevision,
            workflowBytesAtApprovedRevision,
            durableStore,
            mapping.TaskId,
            mapping.CorrelationId,
            receivedAt);
    }
}
