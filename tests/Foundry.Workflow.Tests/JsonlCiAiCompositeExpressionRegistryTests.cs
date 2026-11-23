using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Foundry;
using TheSingularityWorkshop.Foundry.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class JsonlCiAiCompositeExpressionRegistryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RegistrationSurvivesRestartAndFindsOnlyExactGateDependents()
    {
        var path = NewPath();
        try
        {
            var core = Expression("core", "TrentBest/FSM_API", '1');
            var physics = Expression("physics", "TrentBest/TheSingularityWorkshop.Physics", '2');
            var registry = new JsonlCiAiCompositeExpressionRegistry(path);
            var coreIdentity = Identity("task-core", core);
            var physicsIdentity = Identity("task-physics", physics);
            registry.Register(coreIdentity, core);
            registry.Register(physicsIdentity, physics);

            var restored = new JsonlCiAiCompositeExpressionRegistry(path);
            Assert.Equal(coreIdentity, restored.Get("task-core").Identity);
            Assert.Equal(core.DefinitionSha256, restored.Get("task-core").Expression.DefinitionSha256);
            Assert.Equal(new[] { "task-physics" },
                restored.FindDependents("physics").Select(item => item.Identity.TaskId));
            Assert.Empty(restored.FindDependents("unrelated"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void RegistrationIsImmutableAndDuplicateRegistrationIsIdempotent()
    {
        var path = NewPath();
        try
        {
            var expression = Expression("physics", "TrentBest/TheSingularityWorkshop.Physics", '2');
            var identity = Identity("task-immutable", expression);
            var registry = new JsonlCiAiCompositeExpressionRegistry(path);
            registry.Register(identity, expression);
            registry.Register(identity, expression);

            var replacement = Expression("core", "TrentBest/FSM_API", '1');
            Assert.Throws<InvalidDataException>(() => registry.Register(identity, replacement));
            Assert.Single(new JsonlCiAiCompositeExpressionRegistry(path).FindDependents("physics"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void ExpressionDigestMismatchAndTornJournalFailClosed()
    {
        var path = NewPath();
        try
        {
            var expression = Expression("physics", "TrentBest/TheSingularityWorkshop.Physics", '2');
            var identity = Identity("task-mismatch", expression) with
            {
                ExpressionSha256 = new string('f', 64)
            };
            var registry = new JsonlCiAiCompositeExpressionRegistry(path);
            Assert.Throws<InvalidDataException>(() => registry.Register(identity, expression));
            Assert.False(File.Exists(path));
            File.WriteAllText(path, "{");
            Assert.Throws<InvalidDataException>(() => new JsonlCiAiCompositeExpressionRegistry(path));
        }
        finally { Delete(path); }
    }

    private static CiAiPipelineGateExpression Expression(string gateId, string repository, char sha) =>
        CiAiPipelineGateExpression.ForPipeline("Ecosystem",
            new CiAiAllOf(new CiAiGateExpression[]
            {
                new CiAiRepositoryGateReference(gateId, repository, gateId + "-candidate", new string(sha, 40)),
                new CiAiRepositoryGateReference("sentinel", "TrentBest/FSM_API", "sentinel-candidate", new string('9', 40))
            }));

    private static CompositeDependencyAdmissionIdentity Identity(
        string taskId, CiAiPipelineGateExpression expression) =>
        new(taskId, "TrentBest/raWWar", taskId + "-candidate", new string('8', 40),
            expression.Pipeline, expression.DefinitionSha256, "policy-v1");

    private static string NewPath() => Path.Combine(Path.GetTempPath(),
        "foundry-ciai-expression-registry-" + Guid.NewGuid().ToString("N") + ".jsonl");

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
