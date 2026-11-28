using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Execution;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class ExecutionCiAiDispatchRunObserverTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 21, 0, 0, TimeSpan.Zero);
    private static readonly CiWorkflowObserverProfile Profile = CiWorkflowObserverProfile.CiAiDefault;

    [Fact]
    public void MarkerArchiveReaderAcceptsOneExactMarkerEntry()
    {
        var archive = CreateMarkerArchive("request-17", "operation-17", "idempotency-17", 42, 1);

        var marker = CiAiDispatchRunMarkerArtifactReader.Read(archive, "dispatch-marker.json");

        Assert.Equal("request-17", marker.RequestId);
        Assert.Equal("operation-17", marker.OperationId);
        Assert.Equal("idempotency-17", marker.IdempotencyKey);
        Assert.Equal(42, marker.RunId);
        Assert.Equal(1, marker.RunAttempt);
    }

    [Fact]
    public void MarkerArchiveReaderRejectsExtraEntries()
    {
        var archive = CreateMarkerArchive(
            "request-17", "operation-17", "idempotency-17", 42, 1,
            includeExtraEntry: true);

        Assert.Throws<InvalidDataException>(() =>
            CiAiDispatchRunMarkerArtifactReader.Read(archive, "dispatch-marker.json"));
    }

    [Fact]
    public async Task ObserverBindsMarkerToIndependentlyObservedWorkflowRunAndEventSha()
    {
        var eventSha = new string('b', 40);
        var mapping = CreateMapping(eventSha);
        var markerArchive = CreateMarkerArchive(
            mapping.RequestId, mapping.OperationId, mapping.IdempotencyKey, 42, 1);
        var apiHandler = new StubApiHandler(markerArchive, eventSha);
        using var apiClient = new HttpClient(apiHandler);
        var downloader = new StaticArchiveDownloader(markerArchive);
        var observer = new ExecutionCiAiDispatchRunObserver(
            apiClient, new StaticReadCredentialProvider(), downloader, Profile);

        var observations = await observer.ObserveCandidatesAsync(mapping, Now);

        var observation = Assert.Single(observations);
        Assert.Equal(42, observation.Binding.RunId);
        Assert.Equal(1, observation.Binding.RunAttempt);
        Assert.Equal(mapping.TrustedWorkflowSha, observation.Binding.WorkflowSha);
        Assert.Equal(eventSha, observation.Binding.EventSha);
        Assert.Equal(mapping.CandidateRef, observation.Binding.CandidateRef);
        Assert.Equal(mapping.CandidateSha, observation.Binding.CandidateSha);
        Assert.Equal(mapping.RequestId, observation.Marker.RequestId);
        Assert.Equal(eventSha, apiHandler.ObservedHeadSha);
        Assert.Equal(eventSha, downloader.ExpectedHeadSha);
    }

    private static CiAiRequestTaskMapping CreateMapping(string eventSha)
    {
        var workflowSha = new string('a', 40);
        var candidateSha = new string('c', 40);
        return new CiAiRequestTaskMapping(
            CiAiRequestTaskMapping.CurrentSchemaVersion,
            "request-17",
            "operation-17",
            "idempotency-17",
            "task-17",
            "correlation-17",
            "TrentBest/Example",
            "refs/heads/feature/candidate",
            candidateSha,
            Profile.WorkflowPath,
            "refs/heads/development",
            workflowSha,
            Profile.RequiredJobName,
            "Resolve candidate",
            "Verify candidate checkout",
            "github-actions",
            "https://api.github.com",
            "https://example.test/approval",
            "github-app-approval",
            Now.AddMinutes(-1),
            Now.AddHours(2),
            eventSha)
        {
            JobKey = Profile.RequiredJobKey,
            SourceArtifactName = Profile.TestArtifactNameForDispatch(1)
        };
    }

    private static ReadOnlyMemory<byte> CreateMarkerArchive(
        string requestId,
        string operationId,
        string idempotencyKey,
        long runId,
        int runAttempt,
        bool includeExtraEntry = false)
    {
        var markerJson = $$"""
        {
          "contract": "{{CiAiDispatchRunMarker.Contract}}",
          "requestId": "{{requestId}}",
          "operationId": "{{operationId}}",
          "idempotencyKey": "{{idempotencyKey}}",
          "runId": {{runId}},
          "runAttempt": {{runAttempt}}
        }
        """;
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("dispatch-marker.json", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                writer.Write(markerJson);
            if (includeExtraEntry)
            {
                var extra = archive.CreateEntry("extra.txt", CompressionLevel.NoCompression);
                using var writer = new StreamWriter(extra.Open());
                writer.Write("unexpected");
            }
        }
        return stream.ToArray();
    }

    private sealed class StaticReadCredentialProvider : IGitHubActionsReadCredentialProvider
    {
        public ValueTask<string?> GetTokenAsync(string repository, CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>("test-read-token");
    }

    private sealed class StaticArchiveDownloader(ReadOnlyMemory<byte> archive) : IGitHubActionsArtifactArchiveDownloader
    {
        public string? ExpectedHeadSha { get; private set; }

        public Task<ReadOnlyMemory<byte>> DownloadAsync(
            CiProviderArtifactObservation artifact,
            string repository,
            long runId,
            string expectedHeadSha,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(repository, artifact.Repository);
            Assert.Equal(runId, artifact.RunId);
            Assert.Equal(expectedHeadSha, artifact.HeadSha);
            ExpectedHeadSha = expectedHeadSha;
            return Task.FromResult(archive);
        }
    }

    private sealed class StubApiHandler(ReadOnlyMemory<byte> markerArchive, string eventSha) : HttpMessageHandler
    {
        public string? ObservedHeadSha { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/actions/runs", StringComparison.Ordinal))
            {
                ObservedHeadSha = request.RequestUri.Query.Split('&')
                    .First(part => part.StartsWith("?head_sha=", StringComparison.Ordinal))[10..];
                return Task.FromResult(Json($$"""
                {
                  "total_count": 1,
                  "workflow_runs": [{
                    "repository": { "full_name": "TrentBest/Example" },
                    "head_sha": "{{eventSha}}",
                    "id": 42,
                    "run_attempt": 1,
                    "html_url": "https://github.com/TrentBest/Example/actions/runs/42",
                    "name": "CI",
                    "status": "completed",
                    "conclusion": "success",
                    "updated_at": "2026-11-27T20:55:00Z",
                    "event": "workflow_dispatch",
                    "path": "{{Profile.WorkflowPath}}",
                    "head_branch": "development",
                    "pull_requests": []
                  }]
                }
                """));
            }

            if (path.EndsWith("/actions/runs/42/artifacts", StringComparison.Ordinal))
                return Task.FromResult(Json("""
                {
                  "total_count": 1,
                  "artifacts": [{ "id": 7, "name": "ciai-dispatch-marker-1" }]
                }
                """));

            if (path.EndsWith("/actions/artifacts/7", StringComparison.Ordinal))
            {
                var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(markerArchive.Span)).ToLowerInvariant();
                return Task.FromResult(Json($$"""
                {
                  "id": 7,
                  "name": "ciai-dispatch-marker-1",
                  "size_in_bytes": {{markerArchive.Length}},
                  "digest": "{{digest}}",
                  "expired": false,
                  "created_at": "2026-11-27T20:00:00Z",
                  "expires_at": "2026-11-28T20:00:00Z",
                  "workflow_run": { "id": 42, "head_sha": "{{eventSha}}" },
                  "archive_download_url": "https://api.github.com/repos/TrentBest/Example/actions/artifacts/7/zip"
                }
                """));
            }

            throw new InvalidOperationException("Unexpected GitHub API request: " + request.RequestUri);
        }

        private static HttpResponseMessage Json(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
