using TheSingularityWorkshop.CiAi;

namespace TheSingularityWorkshop.Execution;

/// <summary>
/// Supplies a short-lived GitHub API read token for the authenticated host's provider observer.
/// Return null only when the target repository is intentionally read anonymously.
/// </summary>
public interface IGitHubActionsReadCredentialProvider
{
    ValueTask<string?> GetTokenAsync(string repository, CancellationToken cancellationToken);
}

/// <summary>Exact provider run/job observations assembled independently of CiAi packet contents.</summary>
public sealed record ExecutionObservedCiAiProviderRun(
    CiWorkflowRunEvidenceObservation Run,
    CiWorkflowRunCollection RunCollection,
    CiWorkflowJobCollection JobCollection,
    CiProviderArtifactObservation ProvenanceArtifact,
    CiProviderArtifactObservation TestResultsArtifact);

/// <summary>
/// Builds independent run/job evidence from GitHub's read-only Actions API using the exact
/// persisted dispatch mapping. This observer never reads run identities or job outcomes from a
/// CiAi packet or model output.
/// </summary>
public sealed class ExecutionCiAiProviderRunObserver
{
    private readonly HttpClient _httpClient;
    private readonly IGitHubActionsReadCredentialProvider _credentials;

    public ExecutionCiAiProviderRunObserver(
        HttpClient httpClient,
        IGitHubActionsReadCredentialProvider credentials)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public async Task<ExecutionObservedCiAiProviderRun> ObserveAsync(
        CiAiRequestTaskMappingSnapshot request,
        CiWorkflowObserverProfile observerProfile,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observerProfile);
        if (observedAt == default)
            throw new ArgumentException("A provider observation timestamp is required.", nameof(observedAt));

        var mapping = request.Mapping.Validate();
        var runBinding = request.RunBinding
            ?? throw new InvalidOperationException("Provider observation requires a persisted exact-run binding.");
        runBinding.ValidateAgainst(mapping);
        if (observerProfile.WorkflowPath != mapping.WorkflowPath ||
            observerProfile.RequiredJobKey != mapping.JobKey ||
            observerProfile.RequiredJobName != mapping.JobName)
            throw new UnauthorizedAccessException("Observer profile does not match the persisted workflow and required-check mapping.");
        if (request.Cancelled)
            throw new InvalidOperationException("Cancelled dispatch intent cannot be observed as actionable evidence.");

        var token = await _credentials.GetTokenAsync(mapping.Repository, cancellationToken).ConfigureAwait(false);
        if (token is { Length: > 8192 } ||
            token is not null && (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl) || token.Any(char.IsWhiteSpace)))
            throw new UnauthorizedAccessException("GitHub read credential provider returned an invalid token.");

        // A workflow_dispatch run is checked out from the trusted workflow revision; the
        // isolated candidate SHA is a separate identity proved by the registered checkout steps.
        var runCollector = new GitHubActionsWorkflowRunCollector(_httpClient, token);
        var runs = await runCollector.GetRunsForCommitAsync(
            mapping.Repository, mapping.TrustedWorkflowSha,
            maxPages: 10, perPage: 100, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (runs.IsTruncated)
            throw new InvalidDataException("GitHub run discovery is truncated; exact provider evidence is incomplete.");

        var jobsCollector = new GitHubActionsWorkflowJobCollector(_httpClient, token);
        var jobs = await jobsCollector.GetJobsForAttemptAsync(
            mapping.Repository, runBinding.RunId, runBinding.RunAttempt,
            maxPages: 10, perPage: 100, cancellationToken).ConfigureAwait(false);
        if (jobs.IsTruncated)
            throw new InvalidDataException("GitHub job discovery is truncated; exact provider evidence is incomplete.");

        var artifactCollector = new GitHubActionsArtifactCollector(_httpClient, token);
        var provenanceArtifact = await artifactCollector.GetRequiredArtifactAsync(
            mapping.Repository, runBinding.RunId, mapping.TrustedWorkflowSha,
            observerProfile.ProvenanceArtifactName(runBinding.RunId, runBinding.RunAttempt),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var testResultsArtifact = await artifactCollector.GetRequiredArtifactAsync(
            mapping.Repository, runBinding.RunId, mapping.TrustedWorkflowSha,
            observerProfile.TestArtifactName(runBinding.RunId, runBinding.RunAttempt),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var artifactReferences = new[]
        {
            new Uri($"https://github.com/{mapping.Repository}/actions/runs/{runBinding.RunId}/artifacts/{provenanceArtifact.ArtifactId}"),
            new Uri($"https://github.com/{mapping.Repository}/actions/runs/{runBinding.RunId}/artifacts/{testResultsArtifact.ArtifactId}")
        };
        var checkoutEvidence = new CiWorkflowCandidateCheckoutEvidence(
            mapping.CandidateRef, mapping.CandidateSha, mapping.JobName,
            mapping.ResolutionStepName, mapping.VerificationStepName);
        var observed = CiWorkflowRunEvidenceObservation.FromCollections(
            runs, jobs, runBinding.RunId, runBinding.RunAttempt,
            observedAt, checkoutEvidence, artifactReferences);

        if (!string.Equals(observed.Repository, mapping.Repository, StringComparison.OrdinalIgnoreCase) ||
            observed.WorkflowPath != mapping.WorkflowPath ||
            observed.WorkflowRef != mapping.TrustedWorkflowRef ||
            !string.Equals(observed.WorkflowSha, mapping.TrustedWorkflowSha, StringComparison.OrdinalIgnoreCase) ||
            observed.Event != runBinding.Event ||
            observed.RunId != runBinding.RunId ||
            observed.RunAttempt != runBinding.RunAttempt)
            throw new UnauthorizedAccessException("Independent GitHub run/job facts do not match the persisted dispatch and trusted workflow mapping.");

        return new ExecutionObservedCiAiProviderRun(
            observed, runs, jobs, provenanceArtifact, testResultsArtifact);
    }
}
