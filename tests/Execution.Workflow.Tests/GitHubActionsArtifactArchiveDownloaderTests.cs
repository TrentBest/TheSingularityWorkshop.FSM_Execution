using System.Net;
using System.Security.Cryptography;
using System.Text;
using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Execution;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class GitHubActionsArtifactArchiveDownloaderTests
{
    private const string Repository = "TrentBest/Example";
    private static readonly string WorkflowSha = new('a', 40);
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DownloadsExactArchiveAndNeverForwardsGitHubTokenToSignedStorage()
    {
        var archiveBytes = Encoding.UTF8.GetBytes("provider-archive-payload");
        var artifact = await CreateProviderArtifactAsync(archiveBytes);
        var apiHandler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://storage.example.test/archive.zip?sig=opaque");
            return response;
        });
        var storageHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archiveBytes)
        });
        using var apiClient = new HttpClient(apiHandler);
        using var storageClient = new HttpClient(storageHandler);
        var clients = new Dictionary<string, HttpClient>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://api.github.com"] = apiClient,
            ["https://storage.example.test"] = storageClient
        };
        var downloader = new GitHubActionsArtifactArchiveDownloader(clients, new StaticCredentialProvider());

        var downloaded = await downloader.DownloadAsync(artifact, Repository, 42, WorkflowSha, Now);

        Assert.Equal(archiveBytes, downloaded.ToArray());
        Assert.Single(apiHandler.Requests);
        Assert.Equal("Bearer test-read-token", apiHandler.Requests[0].Authorization);
        Assert.Single(storageHandler.Requests);
        Assert.Null(storageHandler.Requests[0].Authorization);
        Assert.Equal("/archive.zip", storageHandler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("?sig=opaque", storageHandler.Requests[0].Uri.Query);
    }

    [Fact]
    public async Task RejectsRedirectToOriginNotInHostAllowlist()
    {
        var archiveBytes = Encoding.UTF8.GetBytes("provider-archive-payload");
        var artifact = await CreateProviderArtifactAsync(archiveBytes);
        var apiHandler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example.test/archive.zip?sig=opaque");
            return response;
        });
        using var apiClient = new HttpClient(apiHandler);
        var clients = new Dictionary<string, HttpClient>
        {
            ["https://api.github.com"] = apiClient
        };
        var downloader = new GitHubActionsArtifactArchiveDownloader(clients, new StaticCredentialProvider());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            downloader.DownloadAsync(artifact, Repository, 42, WorkflowSha, Now));
    }

    [Fact]
    public async Task RejectsSecondRedirectInsteadOfFollowingAnUnreviewedChain()
    {
        var archiveBytes = Encoding.UTF8.GetBytes("provider-archive-payload");
        var artifact = await CreateProviderArtifactAsync(archiveBytes);
        var apiHandler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://storage.example.test/archive.zip?sig=opaque");
            return response;
        });
        var storageHandler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://storage.example.test/second-hop?sig=opaque");
            return response;
        });
        using var apiClient = new HttpClient(apiHandler);
        using var storageClient = new HttpClient(storageHandler);
        var clients = new Dictionary<string, HttpClient>
        {
            ["https://api.github.com"] = apiClient,
            ["https://storage.example.test"] = storageClient
        };
        var downloader = new GitHubActionsArtifactArchiveDownloader(clients, new StaticCredentialProvider());

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            downloader.DownloadAsync(artifact, Repository, 42, WorkflowSha, Now));
        Assert.Single(storageHandler.Requests);
    }

    [Fact]
    public async Task RejectsDownloadedBytesThatDoNotMatchProviderDigest()
    {
        var expectedBytes = Encoding.UTF8.GetBytes("expected-provider-archive");
        var returnedBytes = Encoding.UTF8.GetBytes("tampered-provider-archive");
        var artifact = await CreateProviderArtifactAsync(expectedBytes);
        var apiHandler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://storage.example.test/archive.zip?sig=opaque");
            return response;
        });
        var storageHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(returnedBytes)
        });
        using var apiClient = new HttpClient(apiHandler);
        using var storageClient = new HttpClient(storageHandler);
        var clients = new Dictionary<string, HttpClient>
        {
            ["https://api.github.com"] = apiClient,
            ["https://storage.example.test"] = storageClient
        };
        var downloader = new GitHubActionsArtifactArchiveDownloader(clients, new StaticCredentialProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(artifact, Repository, 42, WorkflowSha, Now));
    }

    [Fact]
    public async Task RejectsMismatchedRunBindingBeforeNetworkAccess()
    {
        var artifact = await CreateProviderArtifactAsync(Encoding.UTF8.GetBytes("provider-archive-payload"));
        var apiHandler = new StubHandler(_ => throw new InvalidOperationException("Network must not be called."));
        using var apiClient = new HttpClient(apiHandler);
        var clients = new Dictionary<string, HttpClient>
        {
            ["https://api.github.com"] = apiClient
        };
        var downloader = new GitHubActionsArtifactArchiveDownloader(clients, new StaticCredentialProvider());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            downloader.DownloadAsync(artifact, Repository, 42, new string('b', 40), Now));
        Assert.Empty(apiHandler.Requests);
    }

    private static async Task<CiProviderArtifactObservation> CreateProviderArtifactAsync(byte[] archiveBytes)
    {
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/actions/runs/42/artifacts", StringComparison.Ordinal))
                return Json("""
                {
                  "total_count": 1,
                  "artifacts": [{ "id": 7, "name": "ciai-workflow-provenance-2" }]
                }
                """);
            if (path.EndsWith("/actions/artifacts/7", StringComparison.Ordinal))
                return Json($$"""
                {
                  "id": 7,
                  "name": "ciai-workflow-provenance-2",
                  "size_in_bytes": {{archiveBytes.Length}},
                  "digest": "{{digest}}",
                  "expired": false,
                  "created_at": "2026-11-27T20:00:00Z",
                  "expires_at": "2026-11-28T20:00:00Z",
                  "workflow_run": { "id": 42, "head_sha": "{{WorkflowSha}}" },
                  "archive_download_url": "https://api.github.com/repos/TrentBest/Example/actions/artifacts/7/zip"
                }
                """);
            throw new InvalidOperationException("Unexpected artifact metadata request: " + request.RequestUri);
        });
        using var client = new HttpClient(handler);
        return await new GitHubActionsArtifactCollector(client)
            .GetRequiredArtifactAsync(Repository, 42, WorkflowSha, "ciai-workflow-provenance-2");
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StaticCredentialProvider : IGitHubActionsReadCredentialProvider
    {
        public ValueTask<string?> GetTokenAsync(string repository, CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>("test-read-token");
    }

    private sealed record RequestCapture(Uri Uri, string? Authorization);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RequestCapture> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RequestCapture(
                request.RequestUri!,
                request.Headers.Authorization?.ToString()));
            return Task.FromResult(responseFactory(request));
        }
    }
}
