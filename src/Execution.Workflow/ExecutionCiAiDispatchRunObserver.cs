using System.IO.Compression;
using TheSingularityWorkshop.CiAi;

namespace TheSingularityWorkshop.Execution;

/// <summary>Trusted naming contract for the per-run dispatch marker artifact.</summary>
public sealed record CiAiDispatchMarkerArtifactProfile(string ArtifactPrefix, string EntryName)
{
    public static CiAiDispatchMarkerArtifactProfile Default { get; } =
        new("ciai-dispatch-marker", "dispatch-marker.json");

    public string ArtifactName(int runAttempt)
    {
        if (runAttempt <= 0)
            throw new ArgumentOutOfRangeException(nameof(runAttempt));
        if (string.IsNullOrWhiteSpace(ArtifactPrefix) || ArtifactPrefix.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(EntryName) || EntryName.Contains('/') ||
            EntryName.Contains('\\') || EntryName is "." or "..")
            throw new InvalidOperationException("Dispatch marker artifact profile is invalid.");
        return $"{ArtifactPrefix}-{runAttempt}";
    }
}

/// <summary>
/// Reads one exact marker file from a small GitHub Actions artifact ZIP. Provider archive digest
/// and run identity must already have been verified by GitHubActionsArtifactArchiveDownloader.
/// </summary>
public static class CiAiDispatchRunMarkerArtifactReader
{
    public const int MaximumArchiveBytes = 64 * 1024;

    public static CiAiDispatchRunMarker Read(
        ReadOnlyMemory<byte> archiveBytes,
        string expectedEntryName)
    {
        if (archiveBytes.Length is 0 or > MaximumArchiveBytes)
            throw new InvalidDataException("Dispatch marker archive is empty or exceeds its bounded size.");
        if (string.IsNullOrWhiteSpace(expectedEntryName) || expectedEntryName.Contains('/') ||
            expectedEntryName.Contains('\\') || expectedEntryName is "." or "..")
            throw new ArgumentException("Dispatch marker entry name must be one safe ZIP leaf name.", nameof(expectedEntryName));

        try
        {
            using var source = new MemoryStream(archiveBytes.ToArray(), writable: false);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count != 1)
                throw new InvalidDataException("Dispatch marker archive must contain exactly one entry.");

            var entry = archive.Entries[0];
            if (!string.Equals(entry.FullName, expectedEntryName, StringComparison.Ordinal) ||
                !string.Equals(entry.Name, expectedEntryName, StringComparison.Ordinal) ||
                entry.Length is <= 0 or > CiAiDispatchRunMarker.MaximumPayloadBytes)
                throw new InvalidDataException("Dispatch marker ZIP entry name or size is invalid.");

            using var entryStream = entry.Open();
            using var destination = new MemoryStream();
            var buffer = new byte[1024];
            while (true)
            {
                var read = entryStream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                if (destination.Length + read > CiAiDispatchRunMarker.MaximumPayloadBytes)
                    throw new InvalidDataException("Dispatch marker payload exceeds its 4 KiB limit.");
                destination.Write(buffer, 0, read);
            }

            return CiAiDispatchRunMarker.Parse(destination.ToArray());
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            throw new InvalidDataException("Dispatch marker archive is not a valid bounded ZIP.", exception);
        }
    }
}

/// <summary>
/// Independently discovers exact workflow-dispatch runs and reads each run's marker artifact.
/// The caller must pass a complete result to CiAiDispatchRunReconciler; a truncated run list or
/// missing marker artifact is an incomplete observation, never a reason to select the nearest run.
/// </summary>
public sealed class ExecutionCiAiDispatchRunObserver
{
    private readonly HttpClient _httpClient;
    private readonly IGitHubActionsReadCredentialProvider _credentials;
    private readonly IGitHubActionsArtifactArchiveDownloader _archiveDownloader;
    private readonly CiWorkflowObserverProfile _workflowProfile;
    private readonly CiAiDispatchMarkerArtifactProfile _markerProfile;

    public ExecutionCiAiDispatchRunObserver(
        HttpClient httpClient,
        IGitHubActionsReadCredentialProvider credentials,
        IGitHubActionsArtifactArchiveDownloader archiveDownloader,
        CiWorkflowObserverProfile workflowProfile,
        CiAiDispatchMarkerArtifactProfile? markerProfile = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _archiveDownloader = archiveDownloader ?? throw new ArgumentNullException(nameof(archiveDownloader));
        _workflowProfile = workflowProfile ?? throw new ArgumentNullException(nameof(workflowProfile));
        _markerProfile = markerProfile ?? CiAiDispatchMarkerArtifactProfile.Default;
    }

    public async Task<IReadOnlyList<CiAiDispatchRunObservation>> ObserveCandidatesAsync(
        CiAiRequestTaskMapping mapping,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        mapping.Validate();
        if (observedAt == default || observedAt < mapping.CreatedAt || observedAt > mapping.ExpiresAt)
            throw new ArgumentException("Dispatch observation time must fall within the persisted mapping lifetime.", nameof(observedAt));
        if (!string.Equals(mapping.WorkflowPath, _workflowProfile.WorkflowPath, StringComparison.Ordinal) ||
            !string.Equals(mapping.JobKey, _workflowProfile.RequiredJobKey, StringComparison.Ordinal) ||
            !string.Equals(mapping.JobName, _workflowProfile.RequiredJobName, StringComparison.Ordinal) ||
            !string.Equals(mapping.TrustedWorkflowRef, "refs/heads/development", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Dispatch mapping does not match the registered trusted workflow profile.");

        var expectedEventSha = mapping.TrustedEventSha ?? mapping.TrustedWorkflowSha;
        var token = await _credentials.GetTokenAsync(mapping.Repository, cancellationToken).ConfigureAwait(false);
        ValidateToken(token);

        var runCollector = new GitHubActionsWorkflowRunCollector(_httpClient, token);
        var runCollection = await runCollector.GetRunsForCommitAsync(
            mapping.Repository, expectedEventSha, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (runCollection.IsTruncated)
            throw new InvalidDataException("GitHub workflow-run discovery is truncated; dispatch reconciliation cannot safely select a run.");

        var artifactCollector = new GitHubActionsArtifactCollector(_httpClient, token);
        var observations = new List<CiAiDispatchRunObservation>();
        foreach (var run in runCollection.Runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (run.Event != "workflow_dispatch" ||
                !string.Equals(run.WorkflowPath, mapping.WorkflowPath, StringComparison.Ordinal) ||
                !string.Equals(run.WorkflowRef, mapping.TrustedWorkflowRef, StringComparison.Ordinal) ||
                !string.Equals(run.Run.Repository, mapping.Repository, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(run.Run.HeadSha, expectedEventSha, StringComparison.OrdinalIgnoreCase))
                continue;

            var artifactName = _markerProfile.ArtifactName(run.RunAttempt);
            var markerArtifact = await artifactCollector.GetRequiredArtifactAsync(
                mapping.Repository,
                run.Run.RunId,
                expectedEventSha,
                artifactName,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var markerArchive = await _archiveDownloader.DownloadAsync(
                markerArtifact,
                mapping.Repository,
                run.Run.RunId,
                expectedEventSha,
                observedAt,
                cancellationToken).ConfigureAwait(false);
            var marker = CiAiDispatchRunMarkerArtifactReader.Read(markerArchive, _markerProfile.EntryName);

            var binding = new CiAiDispatchRunBinding(
                run.Run.RunId,
                run.RunAttempt,
                mapping.Repository,
                mapping.WorkflowPath,
                mapping.TrustedWorkflowRef,
                mapping.TrustedWorkflowSha,
                mapping.CandidateRef,
                mapping.CandidateSha,
                run.Event,
                observedAt,
                expectedEventSha);
            observations.Add(marker.Bind(binding));
        }

        return Array.AsReadOnly(observations.ToArray());
    }

    private static void ValidateToken(string? token)
    {
        if (token is { Length: > 8192 } ||
            token is not null &&
            (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl) || token.Any(char.IsWhiteSpace)))
            throw new UnauthorizedAccessException("GitHub read credential provider returned an invalid token.");
    }
}
