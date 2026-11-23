using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.FSM_Execution.GitHubHost;
using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class GitHubWorkItemTaskAdmissionProcessorTests
{
    [Fact]
    public async Task IssueAdmissionPinsIndependentRepositorySnapshotAndIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var fence = new JsonlGitHubWorkItemRevisionFence(Path.Combine(directory.Path, "fence.jsonl"));
        var receipt = Receipt("issues", "issue", "https://github.com/owner/repo/issues/17",
            "2026-11-23T07:00:00Z", null, null, "a");
        fence.Reconcile([receipt]);
        var tasks = Coordinator(directory.Path, fence);
        var resolver = new FixedResolver(new GitHubWorkItemTaskSnapshot(
            "owner/repo", "issue", "I_node", 17, receipt.Event.HtmlUrl.AbsoluteUri,
            receipt.Event.UpdatedAt, Sha('c'), ["src"], 3));
        var processor = new GitHubWorkItemTaskAdmissionProcessor(tasks, fence, resolver);

        var admitted = await processor.AdmitAsync(receipt);
        var repeated = await processor.AdmitAsync(receipt);

        Assert.Equal(WorkTaskStatus.Queued, admitted.Status);
        Assert.Equal(admitted.Request.TaskId, repeated.Request.TaskId);
        Assert.Equal(WorkTaskStatus.Queued, repeated.Status);
        Assert.Equal(Sha('c'), admitted.Request.BaseSha);
        Assert.NotEqual(receipt.Event.UpdatedAt.ToString("O"), admitted.Request.BaseSha);
        Assert.Equal("issue", admitted.Request.GitHubProvenance!.ItemKind);
        Assert.Null(admitted.Request.GitHubProvenance.HeadSha);
        var claimed = tasks.Claim(admitted.Request.TaskId, "agent", TimeSpan.FromMinutes(1),
            DateTimeOffset.Parse("2026-11-23T07:02:00Z"));
        Assert.Equal(WorkTaskStatus.Claimed, claimed.Status);
    }

    [Fact]
    public async Task PullRequestAdmissionPinsExactHeadAndBaseAndSupportsCanonicalPullUrl()
    {
        using var directory = new TemporaryDirectory();
        var fence = new JsonlGitHubWorkItemRevisionFence(Path.Combine(directory.Path, "fence.jsonl"));
        var head = Sha('d');
        var baseSha = Sha('e');
        var receipt = Receipt("pull_request", "pull_request", "https://github.com/owner/repo/pull/18",
            "2026-11-23T07:01:00Z", head, baseSha, "b");
        fence.Reconcile([receipt]);
        var tasks = Coordinator(directory.Path, fence);
        var resolver = new FixedResolver(new GitHubWorkItemTaskSnapshot(
            "owner/repo", "pull_request", "PR_node", 18, receipt.Event.HtmlUrl.AbsoluteUri,
            receipt.Event.UpdatedAt, head, ["src", "tests"], 2));
        var processor = new GitHubWorkItemTaskAdmissionProcessor(tasks, fence, resolver);

        var admitted = await processor.AdmitAsync(receipt);

        Assert.Equal(head, admitted.Request.BaseSha);
        Assert.Equal(head, admitted.Request.GitHubProvenance!.HeadSha);
        Assert.Equal(baseSha, admitted.Request.GitHubProvenance.EventBaseSha);
        Assert.EndsWith("/pull/18", admitted.Request.IssueUrl);
    }

    [Fact]
    public async Task SupersededCandidateCannotCreateTaskAndOldTaskCannotBeClaimed()
    {
        using var directory = new TemporaryDirectory();
        var fence = new JsonlGitHubWorkItemRevisionFence(Path.Combine(directory.Path, "fence.jsonl"));
        var first = Receipt("issues", "issue", "https://github.com/owner/repo/issues/17",
            "2026-11-23T07:00:00Z", null, null, "a");
        fence.Reconcile([first]);
        var tasks = Coordinator(directory.Path, fence);
        var resolver = new FixedResolver(new GitHubWorkItemTaskSnapshot(
            "owner/repo", "issue", "I_node", 17, first.Event.HtmlUrl.AbsoluteUri,
            first.Event.UpdatedAt, Sha('c'), ["src"], 3));
        var processor = new GitHubWorkItemTaskAdmissionProcessor(tasks, fence, resolver);
        var admitted = await processor.AdmitAsync(first);

        var newer = Receipt("issues", "issue", "https://github.com/owner/repo/issues/17",
            "2026-11-23T07:02:00Z", null, null, "f");
        fence.Reconcile([newer]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await processor.AdmitAsync(first));
        Assert.Throws<InvalidOperationException>(() =>
            tasks.Claim(admitted.Request.TaskId, "agent", TimeSpan.FromMinutes(1), DateTimeOffset.Parse("2026-11-23T07:03:00Z")));
    }

    [Fact]
    public async Task MismatchedSnapshotRevisionIsRejectedBeforeTaskPersistence()
    {
        using var directory = new TemporaryDirectory();
        var fence = new JsonlGitHubWorkItemRevisionFence(Path.Combine(directory.Path, "fence.jsonl"));
        var receipt = Receipt("issues", "issue", "https://github.com/owner/repo/issues/17",
            "2026-11-23T07:00:00Z", null, null, "a");
        fence.Reconcile([receipt]);
        var tasks = Coordinator(directory.Path, fence);
        var resolver = new FixedResolver(new GitHubWorkItemTaskSnapshot(
            "owner/repo", "issue", "I_node", 17, receipt.Event.HtmlUrl.AbsoluteUri,
            receipt.Event.UpdatedAt.AddSeconds(1), Sha('c'), ["src"], 3));
        var processor = new GitHubWorkItemTaskAdmissionProcessor(tasks, fence, resolver);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await processor.AdmitAsync(receipt));
        Assert.Throws<KeyNotFoundException>(() => tasks.GetSnapshot(JsonlGitHubWorkItemRevisionFence.CreateTaskId(receipt)));
    }

    [Fact]
    public void ProvenanceBearingTaskCannotBypassRevisionRegistration()
    {
        using var directory = new TemporaryDirectory();
        var fence = new JsonlGitHubWorkItemRevisionFence(Path.Combine(directory.Path, "fence.jsonl"));
        var tasks = Coordinator(directory.Path, fence);
        var provenance = new GitHubWorkItemTaskProvenance(
            "issue", "I_node", DateTimeOffset.Parse("2026-11-23T07:00:00Z"),
            new string('a', 64), Sha('c'), null, null);
        var request = new WorkTaskRequest(
            WorkTaskRequest.CurrentSchemaVersion, "github-unregistered", "123:issue:I_node",
            "owner/repo", 17, "https://github.com/owner/repo/issues/17",
            Sha('c'), ["src"], 2)
        {
            GitHubProvenance = provenance
        };

        tasks.Submit(request);
        Assert.Throws<InvalidOperationException>(() =>
            tasks.Claim(request.TaskId, "agent", TimeSpan.FromMinutes(1), DateTimeOffset.Parse("2026-11-23T07:03:00Z")));
    }

    private static PersistentWorkTaskCoordinator Coordinator(string path, JsonlGitHubWorkItemRevisionFence fence) =>
        new(new JsonlWorkTaskStore(Path.Combine(path, "tasks.jsonl")), claimAdmissionPolicy: fence);

    private static GitHubWebhookDeliveryReceipt Receipt(
        string eventName, string itemKind, string url, string updated,
        string? head, string? baseSha, string digestChar)
    {
        var number = itemKind == "issue" ? 17 : 18;
        var item = new GitHubWorkItemEvent(1, "delivery-" + digestChar, eventName,
            itemKind == "issue" ? "opened" : "synchronize", 123, "owner/repo",
            itemKind, number + 100, itemKind == "issue" ? "I_node" : "PR_node",
            number, new Uri(url), DateTimeOffset.Parse(updated), head is null ? null : "main", baseSha,
            head is null ? null : "feature", head);
        return new GitHubWebhookDeliveryReceipt(item.DeliveryId, new string(digestChar, 64),
            item.WorkItemIdentity, 1, GitHubWebhookDeliveryDisposition.Accepted, item,
            DateTimeOffset.Parse(updated));
    }

    private static string Sha(char value) => new(value, 40);

    private sealed class FixedResolver(GitHubWorkItemTaskSnapshot snapshot) : IGitHubWorkItemTaskSnapshotResolver
    {
        public ValueTask<GitHubWorkItemTaskSnapshot> ResolveAsync(
            GitHubWorkItemEvent workItemEvent, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "fsm-work-item-admission-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
