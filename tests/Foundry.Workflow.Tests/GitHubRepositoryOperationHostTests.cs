using System.Net.Http.Headers;
using TheSingularityWorkshop.Foundry.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class GitHubRepositoryOperationHostTests
{
    private const string Repository = "TrentBest/Sample";
    private const string TrustedWorkflowSha = "abcdef0123456789abcdef0123456789abcdef01";

    [Fact]
    public void CreateRejectsApiClientWithAmbientAuthorization()
    {
        using var api = CreateApiClient();
        using var download = new HttpClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ambient");
        var dependencies = Dependencies();

        Assert.Throws<ArgumentException>(() => GitHubRepositoryOperationHost.Create(
            api, download, dependencies.Tokens, dependencies.Policy, dependencies.Workflows,
            dependencies.Journal, dependencies.Staging, TrustedWorkflowSha));
    }

    [Fact]
    public void CreateRejectsArtifactClientWithAmbientAuthorization()
    {
        using var api = CreateApiClient();
        using var download = new HttpClient();
        download.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ambient");
        var dependencies = Dependencies();

        Assert.Throws<ArgumentException>(() => GitHubRepositoryOperationHost.Create(
            api, download, dependencies.Tokens, dependencies.Policy, dependencies.Workflows,
            dependencies.Journal, dependencies.Staging, TrustedWorkflowSha));
    }

    [Fact]
    public void CreateComposesAdapterDispatcherAndProvenanceReader()
    {
        using var api = CreateApiClient();
        using var download = new HttpClient();
        var dependencies = Dependencies();

        var host = GitHubRepositoryOperationHost.Create(
            api, download, dependencies.Tokens, dependencies.Policy, dependencies.Workflows,
            dependencies.Journal, dependencies.Staging, TrustedWorkflowSha);

        Assert.NotNull(host.RepositoryOperations);
        Assert.NotNull(host.ActionsDispatcher);
        Assert.NotNull(host.ProvenanceReader);
    }

    [Fact]
    public void CreateRejectsApiClientOutsideFixedGitHubOrigin()
    {
        using var api = new HttpClient { BaseAddress = new Uri("https://example.com/") };
        using var download = new HttpClient();
        var dependencies = Dependencies();

        Assert.Throws<ArgumentException>(() => GitHubRepositoryOperationHost.Create(
            api, download, dependencies.Tokens, dependencies.Policy, dependencies.Workflows,
            dependencies.Journal, dependencies.Staging, TrustedWorkflowSha));
    }

    [Fact]
    public void CreateRejectsMalformedTrustedWorkflowSha()
    {
        using var api = CreateApiClient();
        using var download = new HttpClient();
        var dependencies = Dependencies();

        Assert.Throws<ArgumentException>(() => GitHubRepositoryOperationHost.Create(
            api, download, dependencies.Tokens, dependencies.Policy, dependencies.Workflows,
            dependencies.Journal, dependencies.Staging, "not-a-commit-sha"));
    }

    private static HttpClient CreateApiClient() =>
        new() { BaseAddress = new Uri("https://api.github.com/") };

    private static DependenciesBundle Dependencies()
    {
        var root = Path.Combine(Path.GetTempPath(), "FoundryHostCompositionTests", Guid.NewGuid().ToString("N"));
        return new DependenciesBundle(
            new TestTokenProvider(),
            new GitHubRepositoryOperationPolicy([Repository]),
            new GitHubActionsWorkflowAllowlist([
                new GitHubActionsWorkflowDefinition(
                    "proof", "foundry-fsm-api-probe.yml",
                    ".github/workflows/foundry-fsm-api-probe.yml")
            ]),
            new JsonlGitHubActionsDispatchJournal(Path.Combine(root, "dispatch.jsonl")),
            new JsonlGitHubRepositoryStagingStore(Path.Combine(root, "staging.jsonl")));
    }

    private sealed record DependenciesBundle(
        IGitHubAccessTokenProvider Tokens,
        GitHubRepositoryOperationPolicy Policy,
        GitHubActionsWorkflowAllowlist Workflows,
        JsonlGitHubActionsDispatchJournal Journal,
        JsonlGitHubRepositoryStagingStore Staging);

    private sealed class TestTokenProvider : IGitHubAccessTokenProvider
    {
        public ValueTask<string> GetTokenAsync(string repository, CancellationToken cancellationToken) =>
            ValueTask.FromResult("composition-test-token");
    }
}
