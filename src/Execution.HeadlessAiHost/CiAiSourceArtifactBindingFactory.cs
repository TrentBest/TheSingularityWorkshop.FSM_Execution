namespace TheSingularityWorkshop.Execution;

/// <summary>
/// Provider-observed metadata for the nested TRX source artifact. Values must be read from
/// the exact provider run, attempt, job, and artifact detail endpoints; report JSON is not a source.
/// </summary>
public sealed record CiAiProviderSourceArtifactObservation(
    string Repository,
    long RunId,
    int RunAttempt,
    string CandidateSha,
    string WorkflowPath,
    long JobId,
    string JobName,
    string JobKey,
    long ArtifactId,
    string ArtifactName,
    string ProviderArchiveDigest,
    long ArchiveSizeBytes,
    DateTimeOffset JobStartedAt,
    DateTimeOffset JobCompletedAt,
    DateTimeOffset ArtifactCreatedAt,
    DateTimeOffset ArtifactExpiresAt);

/// <summary>
/// Host adapter boundary that turns independently observed provider metadata plus the exact
/// downloaded ZIP bytes into a non-forgeable source-artifact binding. This factory performs
/// local verification only; the caller remains responsible for obtaining metadata from the
/// authenticated provider API and for retrieving the archive from its trusted transport.
/// </summary>
public static class CiAiSourceArtifactBindingFactory
{
    public static CiAiHostObservedSourceArtifactBinding VerifyAndBind(
        CiAiRequestTaskMapping mapping,
        CiAiDispatchRunBinding run,
        CiAiProviderSourceArtifactObservation observation,
        ReadOnlyMemory<byte> archiveBytes,
        string expectedTrxEntryName,
        string expectedProvenanceEntryName,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(observation);
        mapping.Validate();
        run.ValidateAgainst(mapping);
        if (now == default ||
            !string.Equals(observation.Repository, mapping.Repository, StringComparison.OrdinalIgnoreCase) ||
            observation.RunId != run.RunId || observation.RunAttempt != run.RunAttempt ||
            !string.Equals(observation.CandidateSha, mapping.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            observation.WorkflowPath != mapping.WorkflowPath ||
            observation.JobName != mapping.JobName ||
            string.IsNullOrWhiteSpace(mapping.JobKey) ||
            observation.JobKey != mapping.JobKey ||
            string.IsNullOrWhiteSpace(mapping.SourceArtifactName) ||
            observation.ArtifactName != mapping.SourceArtifactName ||
            observation.JobId <= 0 || observation.ArtifactId <= 0 ||
            observation.JobCompletedAt > now || observation.ArtifactExpiresAt <= now)
            throw new UnauthorizedAccessException("Provider-observed source-artifact metadata does not match the exact persisted CiAi dispatch intent.");

        var archive = CiAiSourceArtifactArchiveVerifier.Verify(
            archiveBytes, observation.ProviderArchiveDigest, observation.ArchiveSizeBytes,
            expectedTrxEntryName, expectedProvenanceEntryName);

        return new CiAiHostObservedSourceArtifactBinding(
            observation.Repository,
            observation.RunId,
            observation.RunAttempt,
            observation.CandidateSha,
            observation.WorkflowPath,
            observation.JobId,
            observation.JobName,
            observation.JobKey,
            observation.ArtifactId,
            observation.ArtifactName,
            archive.ProviderArchiveDigest,
            archive.ArchiveSizeBytes,
            observation.JobStartedAt,
            observation.JobCompletedAt,
            observation.ArtifactCreatedAt,
            observation.ArtifactExpiresAt,
            archive.TrxSha256,
            archive.TrxSizeBytes).ValidateAgainst(mapping, run, now);
    }
}
