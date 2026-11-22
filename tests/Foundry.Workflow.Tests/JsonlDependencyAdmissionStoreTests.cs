using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class JsonlDependencyAdmissionStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WaitingStateSurvivesRestartWithoutInventingProviderFailure()
    {
        var path = NewPath();
        try
        {
            var gate = Gate();
            var first = new JsonlDependencyAdmissionStore(path);
            var waiting = first.CreateWaiting("task-1", gate, Now);
            Assert.Equal(DependencyAdmissionStatus.Waiting, waiting.Status);
            Assert.Null(waiting.RunId);

            var restored = new JsonlDependencyAdmissionStore(path).Get("task-1");
            Assert.NotNull(restored);
            Assert.Equal(DependencyAdmissionStatus.Waiting, restored!.Status);
            Assert.Equal(0, restored.Sequence);
            Assert.Equal(gate, restored.Gate);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void ExactVerifiedProviderEvidenceSatisfiesOnlyItsBoundGate()
    {
        var path = NewPath();
        try
        {
            var store = new JsonlDependencyAdmissionStore(path);
            var gate = Gate();
            store.CreateWaiting("task-1", gate, Now);
            var satisfied = store.ApplyVerifiedObservation(new DependencyGateObservation(
                "task-1", gate, DependencyAdmissionStatus.Satisfied, 1,
                451, 2, new string('a', 64), Now.AddMinutes(5)));

            Assert.Equal(DependencyAdmissionStatus.Satisfied, satisfied.Status);
            Assert.Equal((long?)451, satisfied.RunId);
            Assert.Equal((int?)2, satisfied.RunAttempt);
            Assert.Equal(new string('a', 64), satisfied.EvidenceSha256);
            Assert.Equal(satisfied, new JsonlDependencyAdmissionStore(path).Get("task-1"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void MismatchedProviderIdentityAndStaleSequenceFailClosed()
    {
        var path = NewPath();
        try
        {
            var store = new JsonlDependencyAdmissionStore(path);
            var gate = Gate();
            store.CreateWaiting("task-1", gate, Now);
            var staleSha = gate with { SourceSha = new string('b', 40) };
            Assert.Throws<InvalidDataException>(() => store.ApplyVerifiedObservation(
                new DependencyGateObservation("task-1", staleSha, DependencyAdmissionStatus.Satisfied,
                    1, 451, 1, new string('a', 64), Now.AddMinutes(1))));
            Assert.Throws<InvalidDataException>(() => store.ApplyVerifiedObservation(
                new DependencyGateObservation("task-1", gate, DependencyAdmissionStatus.Satisfied,
                    2, 451, 1, new string('a', 64), Now.AddMinutes(1))));
            Assert.Equal(DependencyAdmissionStatus.Waiting, store.Get("task-1")!.Status);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void IdenticalDeliveryIsIdempotentButConflictsAndTerminalRegressionAreRejected()
    {
        var path = NewPath();
        try
        {
            var store = new JsonlDependencyAdmissionStore(path);
            var gate = Gate();
            store.CreateWaiting("task-1", gate, Now);
            var observation = new DependencyGateObservation("task-1", gate,
                DependencyAdmissionStatus.Blocked, 1, 451, 1, new string('c', 64), Now.AddMinutes(2));
            var blocked = store.ApplyVerifiedObservation(observation);
            Assert.Equal(blocked, store.ApplyVerifiedObservation(observation));

            var conflict = observation with { EvidenceSha256 = new string('d', 64) };
            Assert.Throws<InvalidDataException>(() => store.ApplyVerifiedObservation(conflict));

            var lateSuccess = observation with
            {
                Status = DependencyAdmissionStatus.Satisfied,
                Sequence = 2,
                RunId = 452,
                EvidenceSha256 = new string('e', 64),
                ObservedAt = Now.AddMinutes(3)
            };
            Assert.Throws<InvalidOperationException>(() => store.ApplyVerifiedObservation(lateSuccess));
            Assert.Equal(DependencyAdmissionStatus.Blocked,
                new JsonlDependencyAdmissionStore(path).Get("task-1")!.Status);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void WaitingObservationCannotSmuggleRunEvidence()
    {
        var path = NewPath();
        try
        {
            var store = new JsonlDependencyAdmissionStore(path);
            var gate = Gate();
            store.CreateWaiting("task-1", gate, Now);
            Assert.Throws<ArgumentException>(() => store.ApplyVerifiedObservation(
                new DependencyGateObservation("task-1", gate, DependencyAdmissionStatus.Waiting,
                    1, 451, 1, new string('a', 64), Now.AddMinutes(1))));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void PersistentCoordinatorCannotClaimWaitingTaskAndCanClaimAfterVerifiedWakeEvent()
    {
        var admissionPath = NewPath();
        var taskPath = NewPath();
        try
        {
            var gate = Gate();
            var admission = new JsonlDependencyAdmissionStore(admissionPath);
            var coordinator = new PersistentWorkTaskCoordinator(
                new JsonlWorkTaskStore(taskPath), dependencyAdmissionStore: admission);
            coordinator.SubmitWaiting(new WorkTaskRequest(1, "task-1", "correlation-1",
                "external/AnyRepo", 1, "https://github.com/external/AnyRepo/issues/1",
                new string('f', 40), new[] { "src" }, 3), gate, Now);

            Assert.Throws<InvalidOperationException>(() => coordinator.Claim(
                "task-1", "agent-a", TimeSpan.FromMinutes(2), Now));

            admission.ApplyVerifiedObservation(new DependencyGateObservation(
                "task-1", gate, DependencyAdmissionStatus.Satisfied, 1,
                451, 1, new string('a', 64), Now.AddMinutes(5)));
            var claimed = coordinator.Claim("task-1", "agent-a", TimeSpan.FromMinutes(2),
                Now.AddMinutes(6));
            Assert.Equal(WorkTaskStatus.Claimed, claimed.Status);
        }
        finally
        {
            Delete(admissionPath);
            Delete(taskPath);
        }
    }

    [Fact]
    public void PersistentCoordinatorKeepsTerminallyBlockedTaskUnclaimable()
    {
        var admissionPath = NewPath();
        var taskPath = NewPath();
        try
        {
            var gate = Gate();
            var admission = new JsonlDependencyAdmissionStore(admissionPath);
            var coordinator = new PersistentWorkTaskCoordinator(
                new JsonlWorkTaskStore(taskPath), dependencyAdmissionStore: admission);
            coordinator.SubmitWaiting(new WorkTaskRequest(1, "task-1", "correlation-1",
                "external/AnyRepo", 1, "https://github.com/external/AnyRepo/issues/1",
                new string('f', 40), new[] { "src" }, 3), gate, Now);
            admission.ApplyVerifiedObservation(new DependencyGateObservation(
                "task-1", gate, DependencyAdmissionStatus.Blocked, 1,
                451, 1, new string('b', 64), Now.AddMinutes(5)));

            Assert.Throws<InvalidOperationException>(() => coordinator.Claim(
                "task-1", "agent-a", TimeSpan.FromMinutes(2), Now.AddMinutes(6)));
        }
        finally
        {
            Delete(admissionPath);
            Delete(taskPath);
        }
    }

    private static DependencyGateIdentity Gate() => new(
        "rawwar.physics.motion",
        "TrentBest/TheSingularityWorkshop.Physics",
        "physics-candidate-1",
        "0123456789abcdef0123456789abcdef01234567",
        "physics.kinematics.prescribed-motion.v1",
        "dependency-readiness.v1");

    private static string NewPath() => Path.Combine(Path.GetTempPath(),
        $"dependency-admission-{Guid.NewGuid():N}.jsonl");

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
