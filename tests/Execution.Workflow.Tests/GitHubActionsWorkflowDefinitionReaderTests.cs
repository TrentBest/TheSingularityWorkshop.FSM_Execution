using System.Net;
using System.Text;
using TheSingularityWorkshop.Execution;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class GitHubActionsWorkflowDefinitionReaderTests
{
    private const string Repository = "TrentBest/Example";
    private static readonly string CommitSha = new('a', 40);

    [Fact]
    public async Task ReadsRawWorkflowBytesAtExactCommitAndUsesReadCredential()
    {
        var workflowBytes = Encoding.UTF8.GetBytes("name: CI\njobs:\n  build:\n    runs-on: ubuntu-latest\n");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(workflowBytes)
        });
        using var client = new HttpClient(handler);
        var reader = new GitHubActionsWorkflowDefinitionReader(client, new StaticCredentialProvider());

        var result = await reader.ReadAsync(Repository, ".github/workflows/ci.yml", CommitSha);

        Assert.Equal(workflowBytes, result.ToArray());
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/TrentBest/Example/contents/.github/workflows/ci.yml?ref=" + CommitSha,
            request.Uri.AbsoluteUri);
        Assert.Equal("Bearer test-read-token", request.Authorization);
        Assert.Equal("application/vnd.github.raw+json", request.Accept);
    }

    [Fact]
    public async Task RejectsRedirectInsteadOfFollowingAnUnreviewedWorkflowOrigin()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://raw.githubusercontent.com/TrentBest/Example/main/.github/workflows/ci.yml");
            return response;
        });
        using var client = new HttpClient(handler);
        var reader = new GitHubActionsWorkflowDefinitionReader(client, new StaticCredentialProvider());

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            reader.ReadAsync(Repository, ".github/workflows/ci.yml", CommitSha));
    }

    [Fact]
    public async Task RejectsJsonEnvelopeInsteadOfRawWorkflowBytes()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":\"bmFtZTogQ0k=\"}", Encoding.UTF8, "application/vnd.github+json")
        });
        using var client = new HttpClient(handler);
        var reader = new GitHubActionsWorkflowDefinitionReader(client, new StaticCredentialProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadAsync(Repository, ".github/workflows/ci.yml", CommitSha));
    }

    [Fact]
    public async Task RejectsOversizedWorkflowDefinition()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[GitHubActionsWorkflowDefinitionReader.MaximumWorkflowDefinitionBytes + 1])
        });
        using var client = new HttpClient(handler);
        var reader = new GitHubActionsWorkflowDefinitionReader(client, new StaticCredentialProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadAsync(Repository, ".github/workflows/ci.yml", CommitSha));
    }

    [Fact]
    public async Task RejectsPathTraversalBeforeNetworkAccess()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Network must not be called."));
        using var client = new HttpClient(handler);
        var reader = new GitHubActionsWorkflowDefinitionReader(client, new StaticCredentialProvider());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadAsync(Repository, ".github/workflows/../secrets.yml", CommitSha));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RejectsInvalidUtf8AndNulBytes()
    {
        var invalidUtf8Handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0xC3, 0x28 })
        });
        using var invalidUtf8Client = new HttpClient(invalidUtf8Handler);
        var invalidUtf8Reader = new GitHubActionsWorkflowDefinitionReader(invalidUtf8Client, new StaticCredentialProvider());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            invalidUtf8Reader.ReadAsync(Repository, ".github/workflows/ci.yml", CommitSha));

        var nulHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { (byte)'n', (byte)'a', (byte)'m', (byte)'e', 0, (byte)':' })
        });
        using var nulClient = new HttpClient(nulHandler);
        var nulReader = new GitHubActionsWorkflowDefinitionReader(nulClient, new StaticCredentialProvider());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            nulReader.ReadAsync(Repository, ".github/workflows/ci.yml", CommitSha));
    }

    private sealed class StaticCredentialProvider : IGitHubActionsReadCredentialProvider
    {
        public ValueTask<string?> GetTokenAsync(string repository, CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>("test-read-token");
    }

    private sealed record RequestCapture(Uri Uri, string? Authorization, string? Accept);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RequestCapture> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RequestCapture(
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.Accept.FirstOrDefault()?.MediaType + "+" + request.Headers.Accept.FirstOrDefault()?.Parameters.FirstOrDefault()?.Value?.Trim('"')));
            return Task.FromResult(responseFactory(request));
        }
    }
}
