using System.Net;
using System.Text.Json;
using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Execution;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class GitHubActionsCiAiWorkflowDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 21, 0, 0, TimeSpan.Zero);
    private static readonly CiWorkflowObserverProfile Profile = new(
        "execution-fsm-api-probe-v1",
        ".github/workflows/execution-fsm-api-probe.yml",
        "ciai-workflow-provenance-{attempt}",
        "ciai-test-results-{attempt}",
        "workflow-provenance.json",
        "results.trx",
        "GitHub Actions",
        "fsm-api-proof",
        "Build, execute, and capture state evidence",
        "Resolve candidate",
        "Verify candidate checkout");

    [Fact]
    public async Task DispatchesExactCandidateAndDurableRequestIdentityToProtectedDevelopmentWorkflow()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new HttpClient(handler);
        var dispatcher = new GitHubActionsCiAiWorkflowDispatcher(client, new StaticDispatchCredentialProvider(), Profile);
        var mapping = CreateMapping();

        await dispatcher.DispatchAsync(mapping);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "https://api.github.com/repos/TrentBest/Example/actions/workflows/execution-fsm-api-probe.yml/dispatches",
            request.Uri.AbsoluteUri);
        Assert.Equal("Bearer test-dispatch-token", request.Authorization);
        Assert.Equal("application/json", request.ContentType);

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal("development", root.GetProperty("ref").GetString());
        var inputs = root.GetProperty("inputs");
        Assert.Equal(mapping.CandidateRef, inputs.GetProperty("candidate_ref").GetString());
        Assert.Equal(mapping.CandidateSha, inputs.GetProperty("candidate_sha").GetString());
        Assert.Equal(mapping.RequestId, inputs.GetProperty("request_id").GetString());
        Assert.Equal(mapping.OperationId, inputs.GetProperty("operation_id").GetString());
        Assert.Equal(mapping.IdempotencyKey, inputs.GetProperty("idempotency_key").GetString());
    }

    [Fact]
    public async Task RejectsMissingTrustedEventShaBeforeNetworkAccess()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Network must not be called."));
        using var client = new HttpClient(handler);
        var dispatcher = new GitHubActionsCiAiWorkflowDispatcher(client, new StaticDispatchCredentialProvider(), Profile);
        var mapping = CreateMapping() with { TrustedEventSha = null };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dispatcher.DispatchAsync(mapping));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RejectsWrongRegisteredWorkflowContractBeforeNetworkAccess()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Network must not be called."));
        using var client = new HttpClient(handler);
        var dispatcher = new GitHubActionsCiAiWorkflowDispatcher(client, new StaticDispatchCredentialProvider(), Profile);
        var mapping = CreateMapping() with { WorkflowPath = ".github/workflows/unregistered.yml" };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dispatcher.DispatchAsync(mapping));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RejectsNon204ProviderResponse()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        using var client = new HttpClient(handler);
        var dispatcher = new GitHubActionsCiAiWorkflowDispatcher(client, new StaticDispatchCredentialProvider(), Profile);

        await Assert.ThrowsAsync<HttpRequestException>(() => dispatcher.DispatchAsync(CreateMapping()));
    }

    [Fact]
    public async Task RequiresSeparateDispatchCredentialProvider()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new HttpClient(handler);
        var dispatcher = new GitHubActionsCiAiWorkflowDispatcher(client, new EmptyDispatchCredentialProvider(), Profile);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dispatcher.DispatchAsync(CreateMapping()));
        Assert.Empty(handler.Requests);
    }

    private static CiAiRequestTaskMapping CreateMapping()
    {
        var workflowSha = new string('a', 40);
        var eventSha = new string('b', 40);
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
            Now,
            Now.AddHours(2),
            eventSha)
        {
            JobKey = Profile.RequiredJobKey,
            SourceArtifactName = Profile.TestArtifactNameForDispatch(1)
        };
    }

    private sealed class StaticDispatchCredentialProvider : IGitHubActionsWorkflowDispatchCredentialProvider
    {
        public ValueTask<string?> GetTokenAsync(string repository, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>("test-dispatch-token");
    }

    private sealed class EmptyDispatchCredentialProvider : IGitHubActionsWorkflowDispatchCredentialProvider
    {
        public ValueTask<string?> GetTokenAsync(string repository, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }

    private sealed record RequestCapture(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? ContentType,
        string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RequestCapture> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestCapture(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                body));
            return responseFactory(request);
        }
    }
}
