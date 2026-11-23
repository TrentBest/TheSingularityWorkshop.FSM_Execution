using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class JsonlCiAiTaskWakeupOutboxTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WaitingAndBlockedAdmissionsCannotCreateWakeups()
    {
        var path = NewPath();
        try
        {
            var outbox = new JsonlCiAiTaskWakeupOutbox(path);
            Assert.Throws<InvalidOperationException>(() => outbox.EnqueueSatisfied(Admission(CompositeDependencyAdmissionState.Waiting), Now));
            Assert.Throws<InvalidOperationException>(() => outbox.EnqueueSatisfied(Admission(CompositeDependencyAdmissionState.Blocked), Now));
            Assert.Empty(outbox.GetPending());
        }
        finally { Delete(path); }
    }

    [Fact]
    public void SatisfiedAdmissionCreatesStableIdempotentWakeup()
    {
        var path = NewPath();
        try
        {
            var admission = Admission(CompositeDependencyAdmissionState.Satisfied);
            var outbox = new JsonlCiAiTaskWakeupOutbox(path);
            var first = outbox.EnqueueSatisfied(admission, Now);
            var duplicate = outbox.EnqueueSatisfied(admission, Now.AddMinutes(1));
            Assert.Equal(first.EventId, duplicate.EventId);
            Assert.Equal(first.IdempotencyKey, duplicate.IdempotencyKey);
            Assert.Single(outbox.GetPending());
        }
        finally { Delete(path); }
    }

    [Fact]
    public void StartupReconciliationRecoversCrashBetweenAdmissionAndOutboxAppend()
    {
        var admissionPath = NewPath(); var outboxPath = NewPath();
        try
        {
            var admissions = new JsonlCompositeDependencyAdmissionStore(admissionPath);
            var identity = Identity();
            admissions.CreateWaiting(identity, Now);
            admissions.ApplyVerifiedDecision(identity, CompositeDependencyAdmissionState.Satisfied, 1,
                new string('a', 64), Now.AddMinutes(1));
            var outbox = new JsonlCiAiTaskWakeupOutbox(outboxPath);
            Assert.Single(outbox.ReconcileSatisfiedAdmissions(admissions, Now.AddMinutes(2)));
            var restored = new JsonlCiAiTaskWakeupOutbox(outboxPath);
            Assert.Single(restored.ReconcileSatisfiedAdmissions(admissions, Now.AddMinutes(3)));
            Assert.Single(restored.GetPending());
        }
        finally { Delete(admissionPath); Delete(outboxPath); }
    }

    [Fact]
    public void DeliveryIsAtLeastOnceAndRequiresAttemptBeforeAcknowledgement()
    {
        var path = NewPath();
        try
        {
            var outbox = new JsonlCiAiTaskWakeupOutbox(path);
            var queued = outbox.EnqueueSatisfied(Admission(CompositeDependencyAdmissionState.Satisfied), Now);
            Assert.Throws<InvalidOperationException>(() => outbox.MarkDelivered(queued.EventId, Now.AddSeconds(1)));
            var attempted = outbox.RecordDeliveryAttempt(queued.EventId, Now.AddSeconds(1));
            Assert.Equal(1, attempted.DeliveryAttempts);
            Assert.Single(outbox.GetPending());
            var delivered = outbox.MarkDelivered(queued.EventId, Now.AddSeconds(2));
            Assert.Equal(CiAiTaskWakeupState.Delivered, delivered.State);
            Assert.Empty(outbox.GetPending());
            Assert.Equal(delivered, outbox.MarkDelivered(queued.EventId, Now.AddSeconds(3)));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void PendingEventSurvivesRestartAndRetriesWithSameKey()
    {
        var path = NewPath();
        try
        {
            var outbox = new JsonlCiAiTaskWakeupOutbox(path);
            var queued = outbox.EnqueueSatisfied(Admission(CompositeDependencyAdmissionState.Satisfied), Now);
            outbox.RecordDeliveryAttempt(queued.EventId, Now.AddSeconds(1));
            var restored = new JsonlCiAiTaskWakeupOutbox(path);
            var pending = Assert.Single(restored.GetPending());
            Assert.Equal(queued.IdempotencyKey, pending.IdempotencyKey);
            Assert.Equal(1, pending.DeliveryAttempts);
            Assert.Equal(2, restored.RecordDeliveryAttempt(queued.EventId, Now.AddSeconds(2)).DeliveryAttempts);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void ConflictingAdmissionCannotReuseTaskSequence()
    {
        var path = NewPath();
        try
        {
            var outbox = new JsonlCiAiTaskWakeupOutbox(path);
            outbox.EnqueueSatisfied(Admission(CompositeDependencyAdmissionState.Satisfied), Now);
            var conflict = Admission(CompositeDependencyAdmissionState.Satisfied) with { EvidenceSetSha256 = new string('b', 64) };
            Assert.Throws<InvalidDataException>(() => outbox.EnqueueSatisfied(conflict, Now));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void TornFinalRecordFailsClosed()
    {
        var path = NewPath();
        try
        {
            new JsonlCiAiTaskWakeupOutbox(path).EnqueueSatisfied(Admission(CompositeDependencyAdmissionState.Satisfied), Now);
            File.AppendAllText(path, "{\"truncated\":");
            Assert.Throws<InvalidDataException>(() => new JsonlCiAiTaskWakeupOutbox(path));
        }
        finally { Delete(path); }
    }

    private static CompositeDependencyAdmissionSnapshot Admission(CompositeDependencyAdmissionState state) =>
        new(Identity(), state, state == CompositeDependencyAdmissionState.Waiting ? 0 : 1,
            state == CompositeDependencyAdmissionState.Waiting ? new string('0', 64) : new string('a', 64),
            Now);

    private static CompositeDependencyAdmissionIdentity Identity() => new(
        "task-wakeup-1", "TrentBest/raWWar", "rawwar-candidate-1", new string('1', 40),
        "Ecosystem", new string('2', 64), "policy-v1");
    private static string NewPath() => Path.Combine(Path.GetTempPath(), "ciai-wakeup-" + Guid.NewGuid().ToString("N") + ".jsonl");
    private static void Delete(string path) { if (File.Exists(path)) File.Delete(path); }
}
