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
    public static ExecutionAcceptedCiAiWorkflowEvidence Accept(
        CiAiArtifactNoticeIngressResult ingress,
        CiAiVerifiedArtifactContent retrievedArtifact,
        ExecutionObservedCiAiProviderRun independentlyObservedRun,
        CiWorkflowProvenanceArtifactEvidence independentlyObservedProvenance,
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
        ArgumentNullException.ThrowIfNull(independentlyObservedProvenance);
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

        var providerArtifact = independentlyObservedProvenance.ProviderArtifact;
        if (!string.Equals(providerArtifact.Repository, mapping.Repository, StringComparison.OrdinalIgnoreCase) ||
            providerArtifact.RunId != runBinding.RunId ||
            !string.Equals(providerArtifact.HeadSha, mapping.TrustedWorkflowSha, StringComparison.OrdinalIgnoreCase) ||
            providerArtifact.ArtifactName != observerProfile.ProvenanceArtifactName(runBinding.RunId, runBinding.RunAttempt) ||
            providerArtifact.IsExpired || providerArtifact.ExpiresAt <= receivedAt ||
            providerArtifact.ArchiveSizeBytes is <= 0 or > CiTestResultArtifactArchive.DefaultMaximumArchiveBytes ||
            independentlyObservedProvenance.PayloadSizeBytes is <= 0 or > CiWorkflowProvenanceArtifactReader.MaximumPayloadBytes)
            throw new UnauthorizedAccessException("Workflow provenance artifact metadata does not match the exact registered provider run and observer profile.");

        var requiredChecks = new[]
        {
            new CiWorkflowRequiredCheckBinding(mapping.JobKey, mapping.JobName)
        };
        var protectedWorkflowPolicy = new CiWorkflowDefinitionTrustPolicy(
            mapping.Repository, mapping.WorkflowPath,
            mapping.TrustedWorkflowRef, mapping.TrustedWorkflowSha);
        var expectedWorkflowRef =
            mapping.Repository + "/" + mapping.WorkflowPath + "@" + mapping.TrustedWorkflowRef;
        var expectedEventSha = mapping.TrustedEventSha ?? mapping.TrustedWorkflowSha;

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
