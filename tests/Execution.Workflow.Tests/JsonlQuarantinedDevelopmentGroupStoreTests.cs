using System.Security.Cryptography;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class JsonlQuarantinedDevelopmentGroupStoreTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 28, 7, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Candidate = [0, 255, 17, 42, 0, 128];

    private static DevelopmentGroupRevision Create(JsonlQuarantinedDevelopmentGroupStore store,
        byte[]? bytes = null) =>
        store.Create("group-1", "revision-1", bytes ?? Candidate, "fsm-serialization/v1",
            "HeadlessAi", "mandate-1", "task-1", "turn-1",
            ["research:sha256:abc", "docs:sha256:def"], Created);

    [Fact]
    public void OpaqueBytesRemainExactAndAreNotAvailableBeforeApproval()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adp-quarantine-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new JsonlQuarantinedDevelopmentGroupStore(path);
            var revision = Create(store);
            Assert.Throws<InvalidOperationException>(() => store.GetApprovedPayload("group-1", "revision-1"));

            var reopened = new JsonlQuarantinedDevelopmentGroupStore(path);
            var persisted = Assert.Single(reopened.Get("group-1").Revisions);
            Assert.Equal(Candidate, persisted.SerializedBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Candidate)).ToLowerInvariant(), persisted.Sha256);
            Assert.Equal(Candidate.Length, persisted.ByteLength);
            Assert.Equal(revision.SupportingMaterialSha256, persisted.SupportingMaterialSha256);
            Assert.Throws<InvalidOperationException>(() => reopened.GetApprovedPayload("group-1", "revision-1"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ApprovalIsBoundToExactRevisionAndSupportingMaterial()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adp-quarantine-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new JsonlQuarantinedDevelopmentGroupStore(path);
            var revision = Create(store);
            Assert.Throws<InvalidOperationException>(() => store.Decide("group-1", "revision-1",
                new string('0', 64), revision.SupportingMaterialSha256,
                DevelopmentGroupReviewStatus.Approved, "human:owner", "Reviewed.", Created.AddMinutes(1)));
            Assert.Throws<InvalidOperationException>(() => store.Decide("group-1", "revision-1",
                revision.Sha256, new string('0', 64),
                DevelopmentGroupReviewStatus.Approved, "human:owner", "Reviewed.", Created.AddMinutes(1)));

            store.Decide("group-1", "revision-1", revision.Sha256, revision.SupportingMaterialSha256,
                DevelopmentGroupReviewStatus.Approved, "human:owner", "Reviewed exact bytes and references.",
                Created.AddMinutes(2));
            var payload = store.GetApprovedPayload("group-1", "revision-1");
            Assert.Equal(Candidate, payload.Bytes);
            Assert.Equal(revision.Sha256, payload.Decision.RevisionSha256);
            Assert.Throws<InvalidOperationException>(() => store.Decide("group-1", "revision-1",
                revision.Sha256, revision.SupportingMaterialSha256,
                DevelopmentGroupReviewStatus.Rejected, "human:owner", "Second decision.", Created.AddMinutes(3)));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RejectionNeverExposesBytesAndAReplacementRevisionNeedsItsOwnDecision()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adp-quarantine-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new JsonlQuarantinedDevelopmentGroupStore(path);
            var first = Create(store);
            store.Decide("group-1", first.RevisionId, first.Sha256, first.SupportingMaterialSha256,
                DevelopmentGroupReviewStatus.Rejected, "human:owner", "Needs revision.", Created.AddMinutes(1));
            Assert.Throws<InvalidOperationException>(() => store.GetApprovedPayload("group-1", first.RevisionId));

            var second = store.AddRevision("group-1", "revision-2", [9, 8, 7],
                "fsm-serialization/v1", "HeadlessAi", "mandate-1", "task-1", "turn-2",
                ["research:sha256:ghi"], Created.AddMinutes(2));
            Assert.Equal(first.RevisionId, second.PreviousRevisionId);
            Assert.Throws<InvalidOperationException>(() => store.GetApprovedPayload("group-1", second.RevisionId));
            Assert.Equal(2, store.Get("group-1").Revisions.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MaterializationOutcomeRequiresExactApprovalAndSurvivesRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adp-quarantine-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new JsonlQuarantinedDevelopmentGroupStore(path);
            var revision = Create(store);
            Assert.Throws<InvalidOperationException>(() => store.RecordMaterializationOutcome(
                "group-1", revision.RevisionId, revision.Sha256, false, "schema-invalid",
                "The owning domain rejected the payload.", Created.AddMinutes(1)));

            store.Decide("group-1", revision.RevisionId, revision.Sha256, revision.SupportingMaterialSha256,
                DevelopmentGroupReviewStatus.Approved, "human:owner", "Approved materialization attempt only.",
                Created.AddMinutes(1));
            store.RecordMaterializationOutcome("group-1", revision.RevisionId, revision.Sha256,
                false, "schema-invalid", "The owning domain rejected the payload.", Created.AddMinutes(2));

            var reopened = new JsonlQuarantinedDevelopmentGroupStore(path);
            var outcome = Assert.Single(reopened.Get("group-1").MaterializationOutcomes);
            Assert.False(outcome.Succeeded);
            Assert.Empty(reopened.Get("group-1").MaterializationOutcomes.Where(x => x.Succeeded));
            Assert.Throws<InvalidOperationException>(() => reopened.RecordMaterializationOutcome(
                "group-1", revision.RevisionId, revision.Sha256, true, "valid",
                "Cannot overwrite prior outcome.", Created.AddMinutes(3)));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void JournalReplayRejectsTamperedOpaquePayload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adp-quarantine-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new JsonlQuarantinedDevelopmentGroupStore(path);
            Create(store);
            var json = File.ReadAllText(path);
            var tampered = json.Replace(Convert.ToBase64String(Candidate),
                Convert.ToBase64String([1, 2, 3]), StringComparison.Ordinal);
            Assert.NotEqual(json, tampered);
            File.WriteAllText(path, tampered);
            Assert.Throws<InvalidDataException>(() => new JsonlQuarantinedDevelopmentGroupStore(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
