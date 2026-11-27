using System.Net;
using System.Text;
using TheSingularityWorkshop.Execution;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class ExecutionCiAiProviderRunObserverTests
{
    [Fact]
    public async Task ObservesExactRunAttemptAndVerifiesCandidateCheckoutSteps()
    {
        var now = new DateTimeOffset(2026, 11, 27, 21, 0, 0, TimeSpan.Zero);
        var workflowSha = new string('a', 40);
        var eventSha = new string('b', 40);
        var candidateSha = new string('c', 40);
        var mapping = new CiAiRequestTaskMapping(
            1, "request-42", "operation-42", "idempotency-42",
            "task-42", "correlation-42", "TrentBest/Example",
            "refs/heads/feature/candidate", candidateSha,
            ".github/workflows/ci.yml", "refs/heads/development", workflowSha,
            "contract", "Resolve candidate", "Verify candidate checkout",
            "CiAi", "https://artifacts.example.test",
            "https://execution.example.test/api/v1/ack", "execution-read",
            now.AddHours(-1), now.AddHours(1), eventSha)
        {
            JobKey = "contract"
        };
        var runBinding = new CiAiDispatchRunBinding(
            42, 2, mapping.Repository, mapping.WorkflowPath,
            mapping.TrustedWorkflowRef, mapping.TrustedWorkflowSha,
            mapping.CandidateRef, mapping.CandidateSha,
            "workflow_dispatch", now, eventSha);
        var snapshot = new CiAiRequestTaskMappingSnapshot(mapping, runBinding, false);

        var runJson = $$"""
        {
          "total_count": 1,
          "workflow_runs": [{
            "id": 42,
            "repository": { "full_name": "TrentBest/Example" },
            "name": "CI",
            "head_sha": "{{workflowSha}}",
            "status": "completed",
            "conclusion": "success",
            "html_url": "https://github.com/TrentBest/Example/actions/runs/42",
            "updated_at": "2026-11-27T20:59:00Z",
            "event": "workflow_dispatch",
            "path": ".github/workflows/ci.yml@refs/heads/development",
            "run_attempt": 2,
            "pull_requests": []
          }]
        }
        """;
        var jobsJson = $$"""
        {
          "total_count": 1,
          "jobs": [{
            "id": 100,
            "run_id": 42,
            "run_attempt": 2,
            "name": "contract",
            "status": "completed",
            "conclusion": "success",
            "html_url": "https://github.com/TrentBest/Example/actions/runs/42/job/100",
            "started_at": "2026-11-27T20:50:00Z",
            "completed_at": "2026-11-27T20:58:00Z",
            "steps": [
              {
                "number": 1,
                "name": "Resolve candidate",
                "status": "completed",
                "conclusion": "success",
                "completed_at": "2026-11-27T20:50:30Z"
              },
              {
                "number": 2,
                "name": "Verify candidate checkout",
                "status": "completed",
                "conclusion": "success",
                "completed_at": "2026-11-27T20:51:00Z"
              }
            ]
          }]
        }
        """;

        var handler = new StubGitHubHandler(runJson, jobsJson);
        using var http = new HttpClient(handler);
        var observer = new ExecutionCiAiProviderRunObserver(http, new StaticCredentialProvider());
        var result = await observer.ObserveAsync(snapshot, now);

        Assert.Equal(42, result.Run.RunId);
        Assert.Equal(2, result.Run.RunAttempt);
        Assert.Equal(mapping.WorkflowPath, result.Run.WorkflowPath);
        Assert.Equal(mapping.TrustedWorkflowRef, result.Run.WorkflowRef);
        Assert.Equal(mapping.TrustedWorkflowSha, result.Run.WorkflowSha);
        Assert.Equal(mapping.CandidateRef, result.Run.CandidateRef);
        Assert.Equal(mapping.CandidateSha, result.Run.CandidateSha);
        Assert.True(result.Run.CandidateCheckoutVerified);
        Assert.True(result.Run.IsSuccessful);
        Assert.Equal(2, handler.RequestCount);
    }

    private sealed class StaticCredentialProvider : IGitHubActionsReadCredentialProvider
    {
        public ValueTask<string?> GetTokenAsync(string repository, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>("test-read-token");
    }

    private sealed class StubGitHubHandler(string runJson, string jobsJson) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var payload = path.EndsWith("/actions/runs", StringComparison.Ordinal)
                ? runJson
                : path.EndsWith("/attempts/2/jobs", StringComparison.Ordinal)
                    ? jobsJson
                    : throw new InvalidOperationException("Unexpected GitHub API request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
