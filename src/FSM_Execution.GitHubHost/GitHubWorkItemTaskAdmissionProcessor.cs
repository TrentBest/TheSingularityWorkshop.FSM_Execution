using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.FSM_Execution.GitHubHost;

/// <summary>
/// Trusted host snapshot of one exact GitHub work-item revision. For an Issue,
/// SnapshotSha is a separately resolved repository commit, never updated_at.
/// For a pull request it must equal the webhook's exact HeadSha.
/// </summary>
public sealed record GitHubWorkItemTaskSnapshot(
    string Repository,
    string ItemKind,
    string ItemNodeId,
    int Number,
    string ItemUrl,
    DateTimeOffset UpdatedAt,
    string SnapshotSha,
    IReadOnlyList<string> AllowedPaths,
    int MaximumAttempts);

/// <summary>
/// Resolves a current, exact GitHub item revision and its repository snapshot.
/// Implementations must reject a live GitHub item whose updated_at no longer
/// equals the authenticated webhook revision rather than silently reading newer data.
/// </summary>
public interface IGitHubWorkItemTaskSnapshotResolver
{
    ValueTask<GitHubWorkItemTaskSnapshot> ResolveAsync(
        GitHubWorkItemEvent workItemEvent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a current authenticated inbox candidate into one immutable, read-only
/// WorkTaskRequest. The existing PersistentWorkTaskCoordinator remains the sole
/// task lifecycle authority; this class does not schedule or dispatch an LLM.
/// </summary>
public sealed class GitHubWorkItemTaskAdmissionProcessor
{
    private readonly PersistentWorkTaskCoordinator _tasks;
    private readonly JsonlGitHubWorkItemRevisionFence _revisionFence;
    private readonly IGitHubWorkItemTaskSnapshotResolver _snapshotResolver;
    private readonly IAgentOnboardingAdmissionVerifier? _onboardingVerifier;

    public GitHubWorkItemTaskAdmissionProcessor(
        PersistentWorkTaskCoordinator tasks,
        JsonlGitHubWorkItemRevisionFence revisionFence,
        IGitHubWorkItemTaskSnapshotResolver snapshotResolver,
        IAgentOnboardingAdmissionVerifier? onboardingVerifier = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _revisionFence = revisionFence ?? throw new ArgumentNullException(nameof(revisionFence));
        _snapshotResolver = snapshotResolver ?? throw new ArgumentNullException(nameof(snapshotResolver));
        _onboardingVerifier = onboardingVerifier;
        if (!ReferenceEquals(_tasks.ClaimAdmissionPolicy, _revisionFence))
            throw new ArgumentException(
                "PersistentWorkTaskCoordinator must use this exact revision fence as its claimAdmissionPolicy.",
                nameof(tasks));
    }

    public async ValueTask<WorkTaskSnapshot> AdmitAsync(
        GitHubWebhookDeliveryReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Disposition != GitHubWebhookDeliveryDisposition.Accepted ||
            receipt.Event is null || receipt.Sequence <= 0 ||
            receipt.WorkItemIdentity != receipt.Event.WorkItemIdentity ||
            receipt.PayloadSha256 is null || receipt.PayloadSha256.Length != 64 ||
            !receipt.PayloadSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Only a current accepted, authenticated inbox candidate can be admitted.");

        var workItemEvent = receipt.Event;
        var taskId = JsonlGitHubWorkItemRevisionFence.CreateTaskId(receipt);
        var latest = _revisionFence.GetLatest(receipt.WorkItemIdentity);
        if (latest is null || latest.TaskId != taskId ||
            latest.UpdatedAt != workItemEvent.UpdatedAt ||
            !string.Equals(latest.PayloadSha256, receipt.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The candidate is not the exact current revision registered in the durable claim fence.");
        _revisionFence.EnsureClaimAllowed(taskId);

        var resolved = await _snapshotResolver.ResolveAsync(workItemEvent, cancellationToken).ConfigureAwait(false);
        ValidateSnapshot(workItemEvent, resolved);
        // A newer webhook may arrive while the trusted GitHub snapshot is being resolved.
        // Recheck immediately before immutable task creation; Claim also checks the fence.
        var stillCurrent = _revisionFence.GetLatest(receipt.WorkItemIdentity);
        if (stillCurrent is null || stillCurrent.TaskId != taskId ||
            stillCurrent.UpdatedAt != workItemEvent.UpdatedAt ||
            !string.Equals(stillCurrent.PayloadSha256, receipt.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A newer authenticated revision superseded this candidate during snapshot resolution.");
        _revisionFence.EnsureClaimAllowed(taskId);

        var provenance = new GitHubWorkItemTaskProvenance(
            workItemEvent.ItemKind,
            workItemEvent.ItemNodeId,
            workItemEvent.UpdatedAt,
            receipt.PayloadSha256.ToLowerInvariant(),
            resolved.SnapshotSha,
            workItemEvent.HeadSha,
            workItemEvent.BaseSha);

        var request = new WorkTaskRequest(
            WorkTaskRequest.CurrentSchemaVersion,
            taskId,
            workItemEvent.WorkItemIdentity,
            workItemEvent.Repository,
            workItemEvent.Number,
            resolved.ItemUrl,
            resolved.SnapshotSha,
            Array.AsReadOnly(resolved.AllowedPaths.ToArray()),
            resolved.MaximumAttempts)
        {
            GitHubProvenance = provenance
        }.Validate(requireAdmissionEvidence: _onboardingVerifier is null);

        try
        {
            var existing = _tasks.GetSnapshot(taskId);
            if (!SameRequest(existing.Request, request))
                throw new InvalidOperationException("An existing task ID is bound to different immutable GitHub revision provenance.");
            return existing;
        }
        catch (KeyNotFoundException)
        {
            // Crash recovery: fence registration may have completed before task creation.
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (_onboardingVerifier is null)
                return _tasks.Submit(request);
            return await _tasks.SubmitAsync(request, _onboardingVerifier, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Concurrent/replayed admission may win the task-ID race. Treat it as
            // idempotent only when the durable request is byte-for-byte equivalent.
            var existing = _tasks.GetSnapshot(taskId);
            if (!SameRequest(existing.Request, request))
                throw;
            return existing;
        }
    }

    private static void ValidateSnapshot(GitHubWorkItemEvent source, GitHubWorkItemTaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(snapshot.Repository, source.Repository, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ItemKind, source.ItemKind, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ItemNodeId, source.ItemNodeId, StringComparison.Ordinal) ||
            snapshot.Number != source.Number ||
            !string.Equals(snapshot.ItemUrl, source.HtmlUrl.AbsoluteUri, StringComparison.Ordinal) ||
            snapshot.UpdatedAt != source.UpdatedAt ||
            snapshot.SnapshotSha is null || snapshot.SnapshotSha.Length != 40 ||
            !snapshot.SnapshotSha.All(Uri.IsHexDigit) ||
            snapshot.AllowedPaths is null || snapshot.AllowedPaths.Count == 0 ||
            snapshot.AllowedPaths.Count > 128 ||
            snapshot.MaximumAttempts is < 1 or > 10)
            throw new InvalidDataException("Trusted GitHub snapshot does not match the exact authenticated work-item revision.");
        if (source.ItemKind == "pull_request" &&
            !string.Equals(snapshot.SnapshotSha, source.HeadSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Pull-request task snapshot must use the exact webhook head SHA.");
        if (source.ItemKind == "issue" && (source.HeadSha is not null || source.BaseSha is not null))
            throw new InvalidDataException("Issue webhook cannot supply pull-request head/base provenance.");
    }

    private static bool SameRequest(WorkTaskRequest left, WorkTaskRequest right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.TaskId == right.TaskId &&
        left.CorrelationId == right.CorrelationId &&
        left.Repository == right.Repository &&
        left.IssueNumber == right.IssueNumber &&
        left.IssueUrl == right.IssueUrl &&
        left.BaseSha == right.BaseSha &&
        left.MaximumAttempts == right.MaximumAttempts &&
        left.AllowedPaths.SequenceEqual(right.AllowedPaths, StringComparer.Ordinal) &&
        Equals(left.GitHubProvenance, right.GitHubProvenance) &&
        Equals(left.AdmissionEvidence, right.AdmissionEvidence);
}
