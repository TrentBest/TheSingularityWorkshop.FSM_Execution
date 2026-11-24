using System.Security.Cryptography;
using System.Text;
using TheSingularityWorkshop.FSM_Execution.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class GitHubWebhookIngressTests
{
    private static readonly byte[] Secret = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void AuthenticatedEnrolledIssueIsPersistedAndDuplicateDeliveryIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);
        var inbox = new JsonlGitHubWebhookDeliveryInbox(Path.Combine(directory.Path, "inbox.jsonl"));
        var ingress = new GitHubWebhookIngress(verifier,
            [new GitHubWebhookRepositoryEnrollment(12345, "TrentBest/Physics")], inbox);

        var body = IssuePayload("delivery-1", "2026-11-23T06:00:00Z");
        var signature = Signature(body);
        var accepted = ingress.Accept(body, signature, "delivery-1", "issues");
        var duplicate = ingress.Accept(body, signature, "delivery-1", "issues");

        Assert.Equal(GitHubWebhookDeliveryDisposition.Accepted, accepted.Disposition);
        Assert.Equal(GitHubWebhookDeliveryDisposition.DuplicateDelivery, duplicate.Disposition);
        Assert.Equal(accepted.Sequence, duplicate.Sequence);
        Assert.Single(inbox.GetPendingCandidates());
    }

    [Fact]
    public void IngressAdvancesDurableClaimFenceBeforeAcknowledgingDelivery()
    {
        using var directory = new TemporaryDirectory();
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);
        var inbox = new JsonlGitHubWebhookDeliveryInbox(Path.Combine(directory.Path, "inbox.jsonl"));
        var fence = new JsonlGitHubWorkItemRevisionFence(Path.Combine(directory.Path, "fence.jsonl"));
        var ingress = new GitHubWebhookIngress(verifier,
            [new GitHubWebhookRepositoryEnrollment(12345, "TrentBest/Physics")], inbox,
            revisionFence: fence);
        var body = IssuePayload("delivery-fence", "2026-11-23T06:00:00Z");

        var accepted = ingress.Accept(body, Signature(body), "delivery-fence", "issues");

        Assert.Equal(GitHubWebhookDeliveryDisposition.Accepted, accepted.Disposition);
        Assert.Equal(JsonlGitHubWorkItemRevisionFence.CreateTaskId(accepted),
            fence.GetLatest(accepted.WorkItemIdentity)!.TaskId);
    }

    [Fact]
    public void InvalidSignatureIsRejectedBeforePayloadParsing()
    {
        using var directory = new TemporaryDirectory();
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);
        var inbox = new JsonlGitHubWebhookDeliveryInbox(Path.Combine(directory.Path, "inbox.jsonl"));
        var ingress = new GitHubWebhookIngress(verifier,
            [new GitHubWebhookRepositoryEnrollment(12345, "TrentBest/Physics")], inbox);

        Assert.Throws<UnauthorizedAccessException>(() =>
            ingress.Accept(Encoding.UTF8.GetBytes("not-json"), "sha256=" + new string('0', 64), "delivery-1", "issues"));
        Assert.Empty(inbox.GetPendingCandidates());
    }

    [Fact]
    public void UnenrolledRepositoryIsRejectedWithoutInboxMutation()
    {
        using var directory = new TemporaryDirectory();
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);
        var inbox = new JsonlGitHubWebhookDeliveryInbox(Path.Combine(directory.Path, "inbox.jsonl"));
        var ingress = new GitHubWebhookIngress(verifier,
            [new GitHubWebhookRepositoryEnrollment(12345, "TrentBest/Physics")], inbox);
        var body = IssuePayload("delivery-2", "2026-11-23T06:00:00Z", repositoryId: 99999);

        Assert.Throws<UnauthorizedAccessException>(() =>
            ingress.Accept(body, Signature(body), "delivery-2", "issues"));
        Assert.Empty(inbox.GetPendingCandidates());
    }

    [Fact]
    public void NewerRevisionReplacesPendingCandidateAndOlderRevisionIsRetainedAsSuperseded()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "inbox.jsonl");
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);
        var inbox = new JsonlGitHubWebhookDeliveryInbox(path);
        var ingress = new GitHubWebhookIngress(verifier,
            [new GitHubWebhookRepositoryEnrollment(12345, "TrentBest/Physics")], inbox);

        var firstBody = IssuePayload("delivery-3", "2026-11-23T06:00:00Z");
        var newerBody = IssuePayload("delivery-4", "2026-11-23T06:05:00Z");
        var olderBody = IssuePayload("delivery-5", "2026-11-23T05:59:00Z");

        Assert.Equal(GitHubWebhookDeliveryDisposition.Accepted,
            ingress.Accept(firstBody, Signature(firstBody), "delivery-3", "issues").Disposition);
        Assert.Equal(GitHubWebhookDeliveryDisposition.Accepted,
            ingress.Accept(newerBody, Signature(newerBody), "delivery-4", "issues").Disposition);
        Assert.Equal(GitHubWebhookDeliveryDisposition.SupersededRevision,
            ingress.Accept(olderBody, Signature(olderBody), "delivery-5", "issues").Disposition);
        Assert.Equal("delivery-4", Assert.Single(inbox.GetPendingCandidates()).DeliveryId);

        var recovered = new JsonlGitHubWebhookDeliveryInbox(path);
        Assert.Equal("delivery-4", Assert.Single(recovered.GetPendingCandidates()).DeliveryId);
        Assert.Equal(GitHubWebhookDeliveryDisposition.DuplicateDelivery,
            recovered.Accept(newerEvent("delivery-4"), newerBody, DateTimeOffset.Parse("2026-11-23T06:06:00Z")).Disposition);
    }

    [Fact]
    public void ConflictingDeliveryIdReuseAndAmbiguousEqualTimestampRevisionFailClosed()
    {
        using var directory = new TemporaryDirectory();
        var inbox = new JsonlGitHubWebhookDeliveryInbox(Path.Combine(directory.Path, "inbox.jsonl"));
        var body = IssuePayload("delivery-6", "2026-11-23T06:00:00Z");
        var parsed = TheSingularityWorkshop.CiAi.GitHubWorkItemEvent.Parse("issues", "delivery-6", body);
        inbox.Accept(parsed, body, DateTimeOffset.Parse("2026-11-23T06:01:00Z"));

        var altered = IssuePayload("delivery-6", "2026-11-23T06:00:00Z", bodyText: "tampered");
        Assert.Throws<InvalidDataException>(() =>
            inbox.Accept(parsed, altered, DateTimeOffset.Parse("2026-11-23T06:02:00Z")));

        var conflictBody = IssuePayload("delivery-7", "2026-11-23T06:00:00Z", bodyText: "different");
        var conflict = TheSingularityWorkshop.CiAi.GitHubWorkItemEvent.Parse("issues", "delivery-7", conflictBody);
        Assert.Throws<InvalidDataException>(() =>
            inbox.Accept(conflict, conflictBody, DateTimeOffset.Parse("2026-11-23T06:02:00Z")));
    }

    [Fact]
    public void TornFinalJournalRecordFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "inbox.jsonl");
        File.WriteAllText(path, "{\"schemaVersion\":1}");
        Assert.Throws<InvalidDataException>(() => new JsonlGitHubWebhookDeliveryInbox(path));
    }

    private static byte[] IssuePayload(string deliveryId, string updatedAt, long repositoryId = 12345, string bodyText = "source") =>
        Encoding.UTF8.GetBytes($$"""
        {
          "action": "opened",
          "repository": { "id": {{repositoryId}}, "full_name": "TrentBest/Physics" },
          "issue": {
            "id": 67890,
            "node_id": "I_kwDOExample123",
            "number": 17,
            "html_url": "https://github.com/TrentBest/Physics/issues/17",
            "updated_at": "{{updatedAt}}",
            "title": "Example",
            "body": "{{bodyText}}"
          }
        }
        """);

    private static TheSingularityWorkshop.CiAi.GitHubWorkItemEvent newerEvent(string deliveryId)
    {
        var body = IssuePayload(deliveryId, "2026-11-23T06:05:00Z");
        return TheSingularityWorkshop.CiAi.GitHubWorkItemEvent.Parse("issues", deliveryId, body);
    }

    private static string Signature(ReadOnlySpan<byte> body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Secret, body)).ToLowerInvariant();

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fsm-execution-webhook-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
