using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.FSM_Execution;
using TheSingularityWorkshop.Foundry;
using TheSingularityWorkshop.Foundry.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiCompositeAdmissionGateEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 5, 30, 0, TimeSpan.Zero);

    [Fact]
    public void GateEventReevaluatesOnlyDependentsUsingEachTasksCompleteEvidenceSet()
    {
        var admissionPath = NewPath("admissions");
        var outboxPath = NewPath("outbox");
        var expressionPath = NewPath("expressions");
        try
        {
            var admissions = new JsonlCompositeDependencyAdmissionStore(admissionPath);
            var outbox = new JsonlCiAiTaskWakeupOutbox(outboxPath);
            var registry = new JsonlCiAiCompositeExpressionRegistry(expressionPath);
            var coordinator = new CiAiCompositeAdmissionWakeupCoordinator(admissions, outbox, registry);

            var first = Expression(
                Leaf("physics", "TrentBest/TheSingularityWorkshop.Physics", "physics-a", '1'),
                Leaf("core-a", "TrentBest/FSM_API", "core-a", '2'));
            var second = Expression(
                Leaf("physics", "TrentBest/TheSingularityWorkshop.Physics", "physics-b", '3'),
                Leaf("ui", "TrentBest/GUI", "ui-candidate", '4'));
            var unrelated = Expression(
                Leaf("story", "TrentBest/StoryBuilder", "story-candidate", '5'),
                Leaf("core-b", "TrentBest/FSM_API", "core-b", '6'));

            var firstIdentity = Identity("task-first", first);
            var secondIdentity = Identity("task-second", second);
            var unrelatedIdentity = Identity("task-unrelated", unrelated);
            coordinator.RegisterExpression(firstIdentity, first);
            coordinator.RegisterExpression(secondIdentity, second);
            coordinator.RegisterExpression(unrelatedIdentity, unrelated);
            admissions.CreateWaiting(firstIdentity, Now);
            admissions.CreateWaiting(secondIdentity, Now);
            admissions.CreateWaiting(unrelatedIdentity, Now);

            var results = coordinator.ApplyVerifiedGateEvent("physics", registration =>
                registration.Identity.TaskId switch
                {
                    "task-first" => Evidence(
                        ("physics", Ready("TrentBest/TheSingularityWorkshop.Physics", "physics-a", '1')),
                        ("core-a", Ready("TrentBest/FSM_API", "core-a", '2'))),
                    "task-second" => Evidence(
                        ("physics", Ready("TrentBest/TheSingularityWorkshop.Physics", "physics-b", '3')),
                        ("ui", Ready("TrentBest/GUI", "ui-candidate", '4'))),
                    _ => throw new InvalidOperationException("Unexpected task selected by the gate index.")
                }, Now.AddMinutes(1));

            Assert.Equal(new[] { "task-first", "task-second" },
                results.Select(result => result.Admission.Identity.TaskId));
            Assert.All(results, result =>
                Assert.Equal(CompositeDependencyAdmissionState.Satisfied, result.Admission.State));
            Assert.Equal(2, outbox.GetPending().Count);
            Assert.Equal(CompositeDependencyAdmissionState.Waiting,
                admissions.Get("task-unrelated")!.State);
        }
        finally
        {
            Delete(admissionPath);
            Delete(outboxPath);
            Delete(expressionPath);
        }
    }

    [Fact]
    public void UnrelatedGateEventDoesNotInvokeEvidenceResolver()
    {
        var admissionPath = NewPath("admissions");
        var outboxPath = NewPath("outbox");
        var expressionPath = NewPath("expressions");
        try
        {
            var admissions = new JsonlCompositeDependencyAdmissionStore(admissionPath);
            var outbox = new JsonlCiAiTaskWakeupOutbox(outboxPath);
            var registry = new JsonlCiAiCompositeExpressionRegistry(expressionPath);
            var coordinator = new CiAiCompositeAdmissionWakeupCoordinator(admissions, outbox, registry);
            var expression = Expression(
                Leaf("physics", "TrentBest/TheSingularityWorkshop.Physics", "physics-a", '1'),
                Leaf("core", "TrentBest/FSM_API", "core-a", '2'));
            var identity = Identity("task-one", expression);
            coordinator.RegisterExpression(identity, expression);
            admissions.CreateWaiting(identity, Now);

            var invoked = false;
            var results = coordinator.ApplyVerifiedGateEvent("unrelated", _ =>
            {
                invoked = true;
                throw new InvalidOperationException("Resolver must not run for an unrelated gate.");
            }, Now.AddMinutes(1));

            Assert.Empty(results);
            Assert.False(invoked);
            Assert.Empty(outbox.GetPending());
        }
        finally
        {
            Delete(admissionPath);
            Delete(outboxPath);
            Delete(expressionPath);
        }
    }

    private static CiAiVerifiedCompositeEvidenceSet Evidence(
        params (string GateId, DependencyReleaseGateEvaluation Evaluation)[] values) =>
        new(values.ToDictionary(value => value.GateId, value => value.Evaluation),
            new string('a', 64));

    private static DependencyReleaseGateEvaluation Ready(string repository, string candidate, char sha) =>
        new(candidate, repository, new string(sha, 40), "Package", "1.0.0",
            DependencyReleaseGateState.ReadyForConsumerVerification,
            Array.Empty<string>(), Array.Empty<string>());

    private static CiAiPipelineGateExpression Expression(params CiAiGateExpression[] children) =>
        CiAiPipelineGateExpression.ForPipeline("Ecosystem", new CiAiAllOf(children));

    private static CiAiRepositoryGateReference Leaf(string gateId, string repository, string candidate, char sha) =>
        new(gateId, repository, candidate, new string(sha, 40));

    private static CompositeDependencyAdmissionIdentity Identity(
        string taskId, CiAiPipelineGateExpression expression) =>
        new(taskId, "TrentBest/raWWar", taskId + "-candidate", new string('9', 40),
            expression.Pipeline, expression.DefinitionSha256, "policy-v1");

    private static string NewPath(string name) => Path.Combine(Path.GetTempPath(),
        "foundry-ciai-gate-event-" + name + "-" + Guid.NewGuid().ToString("N") + ".jsonl");

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
