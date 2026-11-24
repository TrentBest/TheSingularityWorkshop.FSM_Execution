using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.FSM_Execution.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class GitHubWorkItemRevisionFenceTests
{
    [Fact]
    public void NewerRevisionSupersedesPriorTaskAndSurvivesRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"github-revision-fence-{Guid.NewGuid():N}.jsonl");
        try
        {
            var fence = new JsonlGitHubWorkItemRevisionFence(path);
            var first = Receipt("delivery-1", "a".PadLeft(64, '0'), "2026-11-23T07:00:00Z");
            var second = Receipt("delivery-2", "b".PadLeft(64, '0'), "2026-11-23T07:01:00Z");
            fence.Register(first, "task-old");
            fence.EnsureClaimAllowed("task-old");
            fence.Register(second, "task-new");
            Assert.Throws<InvalidOperationException>(() => fence.EnsureClaimAllowed("task-old"));
            fence.EnsureClaimAllowed("task-new");

            var restored = new JsonlGitHubWorkItemRevisionFence(path);
            Assert.Throws<InvalidOperationException>(() => restored.EnsureClaimAllowed("task-old"));
            restored.EnsureClaimAllowed("task-new");
            Assert.Equal("task-new", restored.GetLatest(first.WorkItemIdentity)!.TaskId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SameRevisionRegistrationIsIdempotentButConflictsFailClosed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"github-revision-fence-{Guid.NewGuid():N}.jsonl");
        try
        {
            var fence = new JsonlGitHubWorkItemRevisionFence(path);
            var receipt = Receipt("delivery-1", "a".PadLeft(64, '0'), "2026-11-23T07:00:00Z");
            var first = fence.Register(receipt, "task-1");
            var duplicate = fence.Register(receipt with { Disposition = GitHubWebhookDeliveryDisposition.DuplicateRevision }, "task-1");
            Assert.Equal(first, duplicate);
            Assert.Throws<InvalidOperationException>(() => fence.Register(receipt, "task-other"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void InboxReconciliationUsesDeterministicTaskIdsAndSupersedesAfterRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"github-revision-fence-{Guid.NewGuid():N}.jsonl");
        try
        {
            var first = Receipt("delivery-1", "a".PadLeft(64, '0'), "2026-11-23T07:00:00Z");
            var second = Receipt("delivery-2", "b".PadLeft(64, '0'), "2026-11-23T07:01:00Z");
            var oldTask = JsonlGitHubWorkItemRevisionFence.CreateTaskId(first);
            var newTask = JsonlGitHubWorkItemRevisionFence.CreateTaskId(second);
            Assert.NotEqual(oldTask, newTask);

            var fence = new JsonlGitHubWorkItemRevisionFence(path);
            fence.Reconcile([first]);
            fence.EnsureClaimAllowed(oldTask);
            fence.Reconcile([first]);
            fence.EnsureClaimAllowed(oldTask);
            fence.Reconcile([second]);
            Assert.Throws<InvalidOperationException>(() => fence.EnsureClaimAllowed(oldTask));
            fence.EnsureClaimAllowed(newTask);

            var restored = new JsonlGitHubWorkItemRevisionFence(path);
            Assert.Throws<InvalidOperationException>(() => restored.EnsureClaimAllowed(oldTask));
            restored.EnsureClaimAllowed(newTask);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ReconcileIgnoresAnOlderInboxSnapshotAfterConcurrentNewerRegistration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"github-revision-fence-{Guid.NewGuid():N}.jsonl");
        try
        {
            var fence = new JsonlGitHubWorkItemRevisionFence(path);
            var newer = Receipt("delivery-new", "b".PadLeft(64, '0'), "2026-11-23T07:01:00Z");
            var older = Receipt("delivery-old", "a".PadLeft(64, '0'), "2026-11-23T07:00:00Z");
            fence.Reconcile([newer]);
            fence.Reconcile([older]);
            Assert.Equal(JsonlGitHubWorkItemRevisionFence.CreateTaskId(newer),
                fence.GetLatest(newer.WorkItemIdentity)!.TaskId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void StaleRevisionCannotReplaceCurrentTask()
    {
        var path = Path.Combine(Path.GetTempPath(), $"github-revision-fence-{Guid.NewGuid():N}.jsonl");
        try
        {
            var fence = new JsonlGitHubWorkItemRevisionFence(path);
            fence.Register(Receipt("delivery-new", "b".PadLeft(64, '0'), "2026-11-23T07:01:00Z"), "task-new");
            Assert.Throws<InvalidOperationException>(() =>
                fence.Register(Receipt("delivery-old", "a".PadLeft(64, '0'), "2026-11-23T07:00:00Z"), "task-old"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static GitHubWebhookDeliveryReceipt Receipt(string delivery, string digest, string updated)
    {
        var item = new GitHubWorkItemEvent(1, delivery, "issues", "edited", 123, "owner/repo",
            "issue", 456, "I_node", 7, new Uri("https://github.com/owner/repo/issues/7"),
            DateTimeOffset.Parse(updated), null, null, null, null);
        return new GitHubWebhookDeliveryReceipt(delivery, digest, item.WorkItemIdentity, 1,
            GitHubWebhookDeliveryDisposition.Accepted, item, DateTimeOffset.Parse(updated));
    }
}
