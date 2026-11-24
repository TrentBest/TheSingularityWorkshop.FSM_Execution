using System.Security.Cryptography;
using System.Text;
using TheSingularityWorkshop.Execution;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class JsonlHumanApprovalRequestStoreTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 24, 4, 0, 0, TimeSpan.Zero);
    private static HumanApprovalRequest Request(string id = "approval-1") =>
        HumanApprovalRequest.Create(id, "task-1", "correlation-1",
            "TrentBest/TheSingularityWorkshop.Execution", new string('a', 40),
            "HeadlessAi/provider-model", "CreatePullRequest",
            "Create a draft pull request", "The proposed change requires human review.",
            "{\"title\":\"Fix failing contract\",\"draft\":true}",
            Created, Created.AddDays(2));

    [Fact]
    public void PendingRequestsSurviveRestartAndCanBeFilteredForGui()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adp-approvals-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new JsonlHumanApprovalRequestStore(path);
            store.Create(Request());
            store.Create(Request("approval-2") with { Repository = "TrentBest/TheSingularityWorkshop.CiAi" });
            var reopened = new JsonlHumanApprovalRequestStore(path);

            Assert.Equal(2, reopened.GetPending().Count);
            Assert.Single(reopened.GetPending("TrentBest/TheSingularityWorkshop.Execution"));
            Assert.Equal("CreatePullRequest", reopened.Get("approval-1").Operation);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DecisionIsDurableAndRemovesRequestFromPendingProjection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adp-approvals-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new JsonlHumanApprovalRequestStore(path);
            store.Create(Request());
            var decided = store.Decide("approval-1", HumanApprovalStatus.Approved,
                "human:owner", "Reviewed proposed diff and approved draft PR creation.",
                Created.AddHours(1));

            Assert.Equal(2, decided.Sequence);
            Assert.Equal(HumanApprovalStatus.Approved, decided.Status);
            Assert.Empty(store.GetPending());
            var reopened = new JsonlHumanApprovalRequestStore(path);
            Assert.Equal(HumanApprovalStatus.Approved, reopened.Get("approval-1").Status);
            Assert.Empty(reopened.GetPending());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RejectsSecondDecisionAndProposalMutationDuringReplay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adp-approvals-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new JsonlHumanApprovalRequestStore(path);
            store.Create(Request());
            store.Decide("approval-1", HumanApprovalStatus.Rejected,
                "human:owner", "Not authorized.", Created.AddMinutes(5));
            Assert.Throws<InvalidOperationException>(() => store.Decide("approval-1",
                HumanApprovalStatus.Approved, "human:owner", "Changed mind.", Created.AddMinutes(6)));

            var lines = File.ReadAllLines(path);
            var terminal = System.Text.Json.JsonSerializer.Deserialize<HumanApprovalRequest>(lines[1],
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            var tampered = terminal with { ArgumentsJson = "{\"title\":\"different\"}" };
            lines[1] = System.Text.Json.JsonSerializer.Serialize(tampered,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            File.WriteAllLines(path, lines);
            Assert.Throws<InvalidDataException>(() => new JsonlHumanApprovalRequestStore(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RejectsInvalidArgumentDigestAndUnboundedExpiry()
    {
        var request = Request();
        Assert.Throws<ArgumentException>(() => (request with { ArgumentsSha256 = new string('0', 64) }).Validate());
        Assert.Throws<ArgumentException>(() => (request with { ExpiresAt = Created.AddDays(8) }).Validate());
    }

    [Fact]
    public void ApprovalDoesNotMutateOrGrantAnExecutionCapability()
    {
        var request = Request();
        var approved = request.Decide(HumanApprovalStatus.Approved, "human:owner",
            "Approved for execution.", Created.AddMinutes(1));

        Assert.Equal(request.ArgumentsSha256, approved.ArgumentsSha256);
        Assert.Equal(request.CandidateSha, approved.CandidateSha);
        Assert.Equal(request.Operation, approved.Operation);
        Assert.Equal(HumanApprovalStatus.Approved, approved.Status);
    }
}
