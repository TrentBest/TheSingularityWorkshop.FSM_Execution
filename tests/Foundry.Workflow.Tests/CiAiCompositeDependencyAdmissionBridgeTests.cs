using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Foundry;
using TheSingularityWorkshop.Foundry.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiCompositeDependencyAdmissionBridgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AndExpressionPersistsCiAiWaitingUntilEveryLeafIsVerified()
    {
        var expression = Expression(new CiAiAllOf(new CiAiGateExpression[]
        {
            Leaf("core", "TrentBest/FSM_API", "core-candidate", '1'),
            Leaf("physics", "TrentBest/TheSingularityWorkshop.Physics", "physics-candidate", '2')
        }));
        var identity = Identity(expression);
        var path = NewPath();
        try
        {
            var store = new JsonlCompositeDependencyAdmissionStore(path);
            store.CreateWaiting(identity, Now);
            var partial = new Dictionary<string, DependencyReleaseGateEvaluation>
            {
                ["core"] = Ready("TrentBest/FSM_API", "core-candidate", '1')
            };

            var waiting = CiAiCompositeDependencyAdmissionBridge.EvaluateAndPersist(
                expression, partial, identity, new string('a', 64), 1, Now.AddMinutes(1), store);

            Assert.Equal(CompositeDependencyAdmissionState.Waiting, waiting.State);
            var complete = new Dictionary<string, DependencyReleaseGateEvaluation>(partial)
            {
                ["physics"] = Ready("TrentBest/TheSingularityWorkshop.Physics", "physics-candidate", '2')
            };
            var satisfied = CiAiCompositeDependencyAdmissionBridge.EvaluateAndPersist(
                expression, complete, identity, new string('b', 64), 2, Now.AddMinutes(2), store);
            Assert.Equal(CompositeDependencyAdmissionState.Satisfied, satisfied.State);
            Assert.Equal(satisfied, new JsonlCompositeDependencyAdmissionStore(path).Get(identity.TaskId));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void AnyExpressionUsesCiAiSemanticsAndAcceptsOneVerifiedLeaf()
    {
        var expression = Expression(new CiAiAnyOf(new CiAiGateExpression[]
        {
            Leaf("core", "TrentBest/FSM_API", "core-candidate", '1'),
            Leaf("physics", "TrentBest/TheSingularityWorkshop.Physics", "physics-candidate", '2')
        }));
        var identity = Identity(expression);
        var path = NewPath();
        try
        {
            var store = new JsonlCompositeDependencyAdmissionStore(path);
            store.CreateWaiting(identity, Now);
            var evidence = new Dictionary<string, DependencyReleaseGateEvaluation>
            {
                ["physics"] = Ready("TrentBest/TheSingularityWorkshop.Physics", "physics-candidate", '2')
            };

            var result = CiAiCompositeDependencyAdmissionBridge.EvaluateAndPersist(
                expression, evidence, identity, new string('c', 64), 1, Now.AddMinutes(1), store);

            Assert.Equal(CompositeDependencyAdmissionState.Satisfied, result.State);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void ExpressionMismatchAndUndeclaredEvidenceFailBeforeJournalMutation()
    {
        var expression = Expression(new CiAiAllOf(new CiAiGateExpression[]
        {
            Leaf("core", "TrentBest/FSM_API", "core-candidate", '1'),
            Leaf("physics", "TrentBest/TheSingularityWorkshop.Physics", "physics-candidate", '2')
        }));
        var identity = Identity(expression);
        var path = NewPath();
        try
        {
            var store = new JsonlCompositeDependencyAdmissionStore(path);
            store.CreateWaiting(identity, Now);
            var evidence = new Dictionary<string, DependencyReleaseGateEvaluation>
            {
                ["rogue"] = Ready("TrentBest/FSM_API", "core-candidate", '1')
            };

            Assert.Throws<InvalidDataException>(() => CiAiCompositeDependencyAdmissionBridge.EvaluateAndPersist(
                expression, evidence, identity, new string('d', 64), 1, Now.AddMinutes(1), store));
            Assert.Throws<InvalidDataException>(() => CiAiCompositeDependencyAdmissionBridge.EvaluateAndPersist(
                expression, new Dictionary<string, DependencyReleaseGateEvaluation>(), identity with
                {
                    ExpressionSha256 = new string('e', 64)
                }, new string('d', 64), 1, Now.AddMinutes(1), store));
            Assert.Equal(CompositeDependencyAdmissionState.Waiting, store.Get(identity.TaskId)!.State);
        }
        finally { Delete(path); }
    }

    private static CiAiPipelineGateExpression Expression(CiAiGateExpression root) =>
        CiAiPipelineGateExpression.ForPipeline("Ecosystem", root);

    private static CiAiRepositoryGateReference Leaf(string id, string repository, string candidate, char sha) =>
        new(id, repository, candidate, new string(sha, 40));

    private static DependencyReleaseGateEvaluation Ready(string repository, string candidate, char sha) =>
        new(candidate, repository, new string(sha, 40), "Package", "1.0.0",
            DependencyReleaseGateState.ReadyForConsumerVerification, Array.Empty<string>(), Array.Empty<string>());

    private static CompositeDependencyAdmissionIdentity Identity(CiAiPipelineGateExpression expression) =>
        new("task-bridge-1", "TrentBest/raWWar", "rawwar-candidate", new string('9', 40),
            expression.Pipeline, expression.DefinitionSha256, "policy-v1");

    private static string NewPath() => Path.Combine(Path.GetTempPath(),
        "foundry-ciai-composite-bridge-" + Guid.NewGuid().ToString("N") + ".jsonl");

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
