using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.FSM_Execution;
using TheSingularityWorkshop.Foundry;
using TheSingularityWorkshop.Foundry.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiCompositeAdmissionWakeupCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WaitingAdmissionDoesNotCreateWakeup()
    {
        var expression = Expression(new CiAiAllOf(new CiAiGateExpression[]
        {
            Leaf("core", "TrentBest/FSM_API", "core-candidate", '1'),
            Leaf("physics", "TrentBest/TheSingularityWorkshop.Physics", "physics-candidate", '2')
        }));
        WithStores(expression, (coordinator, admissions, outbox, identity) =>
        {
            var result = coordinator.ApplyVerifiedEvidence(expression,
                new Dictionary<string, DependencyReleaseGateEvaluation>
                {
                    ["core"] = Ready("TrentBest/FSM_API", "core-candidate", '1')
                }, identity, new string('a', 64), Now.AddMinutes(1));

            Assert.Equal(CompositeDependencyAdmissionState.Waiting, result.Admission.State);
            Assert.Null(result.Wakeup);
            Assert.Empty(outbox.GetPending());
            Assert.Equal(CompositeDependencyAdmissionState.Waiting, admissions.Get(identity.TaskId)!.State);
        });
    }

    [Fact]
    public void SatisfiedAdmissionIsPersistedBeforeStableWakeupIsEnqueued()
    {
        var expression = Expression(new CiAiAllOf(new CiAiGateExpression[]
        {
            Leaf("core", "TrentBest/FSM_API", "core-candidate", '1'),
            Leaf("physics", "TrentBest/TheSingularityWorkshop.Physics", "physics-candidate", '2')
        }));
        WithStores(expression, (coordinator, admissions, outbox, identity) =>
        {
            var result = coordinator.ApplyVerifiedEvidence(expression,
                new Dictionary<string, DependencyReleaseGateEvaluation>
                {
                    ["core"] = Ready("TrentBest/FSM_API", "core-candidate", '1'),
                    ["physics"] = Ready("TrentBest/TheSingularityWorkshop.Physics", "physics-candidate", '2')
                }, identity, new string('b', 64), Now.AddMinutes(1));

            Assert.Equal(CompositeDependencyAdmissionState.Satisfied, admissions.Get(identity.TaskId)!.State);
            Assert.NotNull(result.Wakeup);
            Assert.Equal(CiAiTaskWakeupState.Pending, result.Wakeup!.State);
            Assert.Equal(result.Wakeup.EventId, result.Wakeup.IdempotencyKey);
            Assert.Single(outbox.GetPending());
        });
    }

    [Fact]
    public void BlockedAdmissionNeverCreatesWakeup()
    {
        var expression = Expression(new CiAiAllOf(new CiAiGateExpression[]
        {
            Leaf("core", "TrentBest/FSM_API", "core-candidate", '1'),
            Leaf("physics", "TrentBest/TheSingularityWorkshop.Physics", "physics-candidate", '2')
        }));
        WithStores(expression, (coordinator, admissions, outbox, identity) =>
        {
            var rejected = new DependencyReleaseGateEvaluation("physics-candidate",
                "TrentBest/TheSingularityWorkshop.Physics", new string('2', 40), "Package", "1.0.0",
                DependencyReleaseGateState.Blocked, new[] { "provider_failed" }, Array.Empty<string>());
            var result = coordinator.ApplyVerifiedEvidence(expression,
                new Dictionary<string, DependencyReleaseGateEvaluation> { ["physics"] = rejected },
                identity, new string('c', 64), Now.AddMinutes(1));

            Assert.Equal(CompositeDependencyAdmissionState.Blocked, result.Admission.State);
            Assert.Null(result.Wakeup);
            Assert.Empty(outbox.GetPending());
        });
    }

    [Fact]
    public void StartupReconciliationRepairsSatisfiedAdmissionWithoutDuplicateWakeup()
    {
        var expression = Expression(new CiAiAnyOf(new CiAiGateExpression[]
        {
            Leaf("core", "TrentBest/FSM_API", "core-candidate", '1'),
            Leaf("physics", "TrentBest/TheSingularityWorkshop.Physics", "physics-candidate", '2')
        }));
        WithStores(expression, (coordinator, admissions, outbox, identity) =>
        {
            var satisfied = CiAiCompositeDependencyAdmissionBridge.EvaluateAndPersist(expression,
                new Dictionary<string, DependencyReleaseGateEvaluation>
                {
                    ["core"] = Ready("TrentBest/FSM_API", "core-candidate", '1')
                }, identity, new string('d', 64), 1, Now.AddMinutes(1), admissions);

            Assert.Equal(CompositeDependencyAdmissionState.Satisfied, satisfied.State);
            Assert.Empty(outbox.GetPending());

            var recovered = coordinator.ReconcileSatisfiedAdmissions(Now.AddMinutes(2));
            Assert.Single(recovered);
            Assert.Single(outbox.GetPending());
            Assert.Single(coordinator.ReconcileSatisfiedAdmissions(Now.AddMinutes(3)));
            Assert.Single(outbox.GetPending());
        });
    }

    private static void WithStores(CiAiPipelineGateExpression expression,
        Action<CiAiCompositeAdmissionWakeupCoordinator, JsonlCompositeDependencyAdmissionStore,
            JsonlCiAiTaskWakeupOutbox, CompositeDependencyAdmissionIdentity> action)
    {
        var admissionPath = NewPath("admission");
        var outboxPath = NewPath("outbox");
        try
        {
            var admissions = new JsonlCompositeDependencyAdmissionStore(admissionPath);
            var outbox = new JsonlCiAiTaskWakeupOutbox(outboxPath);
            var identity = new CompositeDependencyAdmissionIdentity("task-wakeup-1",
                "TrentBest/raWWar", "rawwar-candidate", new string('9', 40),
                expression.Pipeline, expression.DefinitionSha256, "policy-v1");
            admissions.CreateWaiting(identity, Now);
            action(new CiAiCompositeAdmissionWakeupCoordinator(admissions, outbox),
                admissions, outbox, identity);
        }
        finally
        {
            if (File.Exists(admissionPath)) File.Delete(admissionPath);
            if (File.Exists(outboxPath)) File.Delete(outboxPath);
        }
    }

    private static CiAiPipelineGateExpression Expression(CiAiGateExpression root) =>
        CiAiPipelineGateExpression.ForPipeline("Ecosystem", root);

    private static CiAiRepositoryGateReference Leaf(string id, string repository, string candidate, char sha) =>
        new(id, repository, candidate, new string(sha, 40));

    private static DependencyReleaseGateEvaluation Ready(string repository, string candidate, char sha) =>
        new(candidate, repository, new string(sha, 40), "Package", "1.0.0",
            DependencyReleaseGateState.ReadyForConsumerVerification, Array.Empty<string>(), Array.Empty<string>());

    private static string NewPath(string suffix) => Path.Combine(Path.GetTempPath(),
        "foundry-ciai-wakeup-coordinator-" + suffix + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
}
