using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using TheSingularityWorkshop.FSM_Execution.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class GitHubWebhookEndpointTests
{
    private static readonly byte[] Secret = Enumerable.Range(11, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public async Task HttpsJsonDeliveryIsAcknowledgedOnlyAfterDurableAcceptance()
    {
        using var directory = new TemporaryDirectory();
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);
        var inbox = new JsonlGitHubWebhookDeliveryInbox(Path.Combine(directory.Path, "inbox.jsonl"));
        var ingress = new GitHubWebhookIngress(verifier,
            [new GitHubWebhookRepositoryEnrollment(12345, "TrentBest/Physics")], inbox);
        var body = Payload();
        var context = Context(body, Signature(body));

        await GitHubWebhookEndpoint.HandleAsync(context, ingress);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Single(inbox.GetPendingCandidates());
    }

    [Fact]
    public async Task NonHttpsRequestIsRejectedBeforeReadingBody()
    {
        using var directory = new TemporaryDirectory();
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);
        var inbox = new JsonlGitHubWebhookDeliveryInbox(Path.Combine(directory.Path, "inbox.jsonl"));
        var ingress = new GitHubWebhookIngress(verifier,
            [new GitHubWebhookRepositoryEnrollment(12345, "TrentBest/Physics")], inbox);
        var body = Payload();
        var context = Context(body, Signature(body));
        context.Request.Scheme = "http";

        await GitHubWebhookEndpoint.HandleAsync(context, ingress);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal((long)body.Length, context.Request.Body.Length - context.Request.Body.Position);
        Assert.Empty(inbox.GetPendingCandidates());
    }

    [Fact]
    public async Task OversizedStreamingBodyIsRejectedWithoutInboxMutation()
    {
        using var directory = new TemporaryDirectory();
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);
        var inbox = new JsonlGitHubWebhookDeliveryInbox(Path.Combine(directory.Path, "inbox.jsonl"));
        var ingress = new GitHubWebhookIngress(verifier,
            [new GitHubWebhookRepositoryEnrollment(12345, "TrentBest/Physics")], inbox);
        var body = new byte[GitHubWebhookSignatureVerifier.MaximumBodyBytes + 1];
        var context = Context(body, Signature(body));

        await GitHubWebhookEndpoint.HandleAsync(context, ingress);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Empty(inbox.GetPendingCandidates());
    }

    [Fact]
    public async Task InvalidSignatureIsUnauthorizedAndUnsupportedEventsAreAcknowledgedWithoutAdmission()
    {
        using var directory = new TemporaryDirectory();
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);
        var inbox = new JsonlGitHubWebhookDeliveryInbox(Path.Combine(directory.Path, "inbox.jsonl"));
        var ingress = new GitHubWebhookIngress(verifier,
            [new GitHubWebhookRepositoryEnrollment(12345, "TrentBest/Physics")], inbox);
        var body = Payload();
        var invalid = Context(body, "sha256=" + new string('0', 64));

        await GitHubWebhookEndpoint.HandleAsync(invalid, ingress);
        Assert.Equal(StatusCodes.Status401Unauthorized, invalid.Response.StatusCode);

        var unsupported = Context(Encoding.UTF8.GetBytes("{}"), Signature(Encoding.UTF8.GetBytes("{}")));
        unsupported.Request.Headers["X-GitHub-Event"] = "push";
        await GitHubWebhookEndpoint.HandleAsync(unsupported, ingress);
        Assert.Equal(StatusCodes.Status202Accepted, unsupported.Response.StatusCode);
        Assert.Empty(inbox.GetPendingCandidates());
    }

    [Fact]
    public async Task MissingRequiredHeadersAndNonJsonBodiesAreRejected()
    {
        using var directory = new TemporaryDirectory();
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);
        var inbox = new JsonlGitHubWebhookDeliveryInbox(Path.Combine(directory.Path, "inbox.jsonl"));
        var ingress = new GitHubWebhookIngress(verifier,
            [new GitHubWebhookRepositoryEnrollment(12345, "TrentBest/Physics")], inbox);
        var body = Payload();
        var missing = Context(body, Signature(body));
        missing.Request.Headers.Remove("X-GitHub-Event");
        await GitHubWebhookEndpoint.HandleAsync(missing, ingress);
        Assert.Equal(StatusCodes.Status400BadRequest, missing.Response.StatusCode);

        var wrongType = Context(body, Signature(body));
        wrongType.Request.ContentType = "text/plain";
        await GitHubWebhookEndpoint.HandleAsync(wrongType, ingress);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, wrongType.Response.StatusCode);
    }

    private static DefaultHttpContext Context(byte[] body, string signature)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.ContentType = "application/json; charset=utf-8";
        context.Request.Body = new MemoryStream(body);
        context.Request.Headers["X-Hub-Signature-256"] = signature;
        context.Request.Headers["X-GitHub-Delivery"] = "endpoint-delivery-1";
        context.Request.Headers["X-GitHub-Event"] = "issues";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static byte[] Payload() => Encoding.UTF8.GetBytes("""
    {
      "action": "opened",
      "repository": { "id": 12345, "full_name": "TrentBest/Physics" },
      "issue": {
        "id": 67890,
        "node_id": "I_kwDOExample123",
        "number": 17,
        "html_url": "https://github.com/TrentBest/Physics/issues/17",
        "updated_at": "2026-11-23T06:00:00Z",
        "title": "Endpoint test",
        "body": "untrusted"
      }
    }
    """);

    private static string Signature(ReadOnlySpan<byte> body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Secret, body)).ToLowerInvariant();

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fsm-execution-endpoint-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
