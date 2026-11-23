using System.Net;
using System.Text;
using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.FSM_Execution.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class GitHubRestWorkItemTaskSnapshotResolverTests
{
    [Fact]
    public async Task IssueUsesSeparateDefaultBranchCommitAndExactUpdatedAt()
    {
        var sha = new string('c', 40);
        using var handler = new QueueHandler(
            Json("{\"id\":123,\"full_name\":\"owner/repo\",\"default_branch\":\"main\"}"),
            Json("{\"id\":456,\"node_id\":\"I_node\",\"number\":17,\"html_url\":\"https://github.com/owner/repo/issues/17\",\"updated_at\":\"2026-11-23T07:00:00Z\"}"),
            Json($"{{\"sha\":\"{sha}\"}}"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var resolver = new GitHubRestWorkItemTaskSnapshotResolver(client, new FixedScopePolicy());
        var source = IssueEvent();

        var snapshot = await resolver.ResolveAsync(source);

        Assert.Equal(sha, snapshot.SnapshotSha);
        Assert.Equal(source.UpdatedAt, snapshot.UpdatedAt);
        Assert.Equal(new[] { "src", "tests" }, snapshot.AllowedPaths);
        Assert.Equal(3, snapshot.MaximumAttempts);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("/commits/main", handler.Requests[2]);
    }

    [Fact]
    public async Task PullRequestMustMatchExactHeadBaseRefsAndTargetRepository()
    {
        var head = new string('d', 40);
        var baseSha = new string('e', 40);
        using var handler = new QueueHandler(
            Json("{\"id\":123,\"full_name\":\"owner/repo\",\"default_branch\":\"main\"}"),
            Json($"{{\"id\":789,\"node_id\":\"PR_node\",\"number\":18,\"html_url\":\"https://github.com/owner/repo/pull/18\",\"updated_at\":\"2026-11-23T07:01:00Z\",\"head\":{{\"sha\":\"{head}\",\"ref\":\"feature\"}},\"base\":{{\"sha\":\"{baseSha}\",\"ref\":\"main\",\"repo\":{{\"id\":123,\"full_name\":\"owner/repo\"}}}}}}"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var resolver = new GitHubRestWorkItemTaskSnapshotResolver(client, new FixedScopePolicy());
        var source = PullRequestEvent(head, baseSha);

        var snapshot = await resolver.ResolveAsync(source);

        Assert.Equal(head, snapshot.SnapshotSha);
        Assert.Equal("pull_request", snapshot.ItemKind);
        Assert.Equal("https://github.com/owner/repo/pull/18", snapshot.ItemUrl);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ChangedIssueRevisionIsRejectedBeforeRepositoryCommitResolution()
    {
        using var handler = new QueueHandler(
            Json("{\"id\":123,\"full_name\":\"owner/repo\",\"default_branch\":\"main\"}"),
            Json("{\"id\":456,\"node_id\":\"I_node\",\"number\":17,\"html_url\":\"https://github.com/owner/repo/issues/17\",\"updated_at\":\"2026-11-23T07:02:00Z\"}"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var resolver = new GitHubRestWorkItemTaskSnapshotResolver(client, new FixedScopePolicy());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolver.ResolveAsync(IssueEvent()));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PullRequestFromDifferentBaseRepositoryIsRejected()
    {
        var head = new string('d', 40);
        var baseSha = new string('e', 40);
        using var handler = new QueueHandler(
            Json("{\"id\":123,\"full_name\":\"owner/repo\",\"default_branch\":\"main\"}"),
            Json($"{{\"id\":789,\"node_id\":\"PR_node\",\"number\":18,\"html_url\":\"https://github.com/owner/repo/pull/18\",\"updated_at\":\"2026-11-23T07:01:00Z\",\"head\":{{\"sha\":\"{head}\",\"ref\":\"feature\"}},\"base\":{{\"sha\":\"{baseSha}\",\"ref\":\"main\",\"repo\":{{\"id\":999,\"full_name\":\"someone/else\"}}}}}}"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var resolver = new GitHubRestWorkItemTaskSnapshotResolver(client, new FixedScopePolicy());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(PullRequestEvent(head, baseSha)));
    }

    private static GitHubWorkItemEvent IssueEvent() =>
        new(1, "delivery-issue", "issues", "edited", 123, "owner/repo", "issue", 456,
            "I_node", 17, new Uri("https://github.com/owner/repo/issues/17"),
            DateTimeOffset.Parse("2026-11-23T07:00:00Z"), null, null, null, null);

    private static GitHubWorkItemEvent PullRequestEvent(string head, string baseSha) =>
        new(1, "delivery-pr", "pull_request", "synchronize", 123, "owner/repo", "pull_request", 789,
            "PR_node", 18, new Uri("https://github.com/owner/repo/pull/18"),
            DateTimeOffset.Parse("2026-11-23T07:01:00Z"), "main", baseSha, "feature", head);

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FixedScopePolicy : IGitHubWorkItemTaskScopePolicy
    {
        public GitHubWorkItemTaskScope Resolve(string repository, string itemKind) =>
            new(["src", "tests"], 3);
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            if (!_responses.TryDequeue(out var response))
                throw new InvalidOperationException("Unexpected additional GitHub REST request.");
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                while (_responses.TryDequeue(out var response))
                    response.Dispose();
            base.Dispose(disposing);
        }
    }
}
