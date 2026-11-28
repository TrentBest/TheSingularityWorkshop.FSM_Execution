using TheSingularityWorkshop.Execution;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class CandidateBoundRepositoryAdmissionTests
{
    private const string Repository = "TrentBest/raWWar";
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 2, 0, 0, TimeSpan.Zero);
    private static readonly string BaseSha = new('a', 40);
    private static readonly string CandidateSha = new('b', 40);

    [Fact]
    public async Task RepositoryMutationAuditsEvidenceForCandidateShaNotTaskBaseSha()
    {
        using var fixture = await GovernedFixture.CreateAsync();
        fixture.Verifier.Reset();
        var operation = new WriteRepositoryFileOperation(
            "governed-task", Repository, "agents/issue-39", CandidateSha,
            "src/example.cs", "safe content");
        var capability = fixture.Capabilities.Grant(fixture.TaskSnapshot, "agents/issue-39", CandidateSha,
            new[] { "src" }, new[] { WorkTaskAction.WriteFiles }, Now, TimeSpan.FromMinutes(10));
        var dispatcher = new AuthorizedRepositoryOperationDispatcher(
            fixture.Tasks, fixture.Capabilities, fixture.Adapter, fixture.Journal,
            () => Now.AddMinutes(1), fixture.Verifier);

        await dispatcher.ExecuteAsync(capability.CapabilityId, operation, Now.AddMinutes(1));

        var entry = Assert.Single(fixture.Journal.GetAll());
        Assert.Equal(RepositoryOperationAuditStatus.Completed, entry.Status);
        Assert.NotNull(entry.AdmissionEvidence);
        Assert.Equal(CandidateSha, entry.AdmissionEvidence!.TargetSha);
        Assert.NotEqual(BaseSha, entry.AdmissionEvidence.TargetSha);
        Assert.Equal(2, fixture.Verifier.Calls);
        Assert.Equal(1, fixture.Adapter.Calls);

        var reopened = new JsonlRepositoryOperationJournal(
            Path.Combine(fixture.DirectoryPath, "operations.jsonl"));
        Assert.Equal(CandidateSha, Assert.Single(reopened.GetAll()).AdmissionEvidence!.TargetSha);
    }

    [Fact]
    public async Task ChangedCandidateEvidenceAfterAuditReservationBlocksAdapter()
    {
        using var fixture = await GovernedFixture.CreateAsync();
        fixture.Verifier.Reset();
        fixture.Verifier.ChangeAfterFirstCall = true;
        var operation = new WriteRepositoryFileOperation(
            "governed-task", Repository, "agents/issue-39", CandidateSha,
            "src/example.cs", "safe content");
        var capability = fixture.Capabilities.Grant(fixture.TaskSnapshot, "agents/issue-39", CandidateSha,
            new[] { "src" }, new[] { WorkTaskAction.WriteFiles }, Now, TimeSpan.FromMinutes(10));
        var dispatcher = new AuthorizedRepositoryOperationDispatcher(
            fixture.Tasks, fixture.Capabilities, fixture.Adapter, fixture.Journal,
            () => Now.AddMinutes(1), fixture.Verifier);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await dispatcher.ExecuteAsync(capability.CapabilityId, operation, Now.AddMinutes(1)));

        Assert.Equal(0, fixture.Adapter.Calls);
        var entry = Assert.Single(fixture.Journal.GetAll());
        Assert.Equal(RepositoryOperationAuditStatus.Failed, entry.Status);
        Assert.Equal(CandidateSha, entry.AdmissionEvidence!.TargetSha);
    }

    [Fact]
    public async Task MissingTrustedVerifierFailsBeforeAuditOrAdapter()
    {
        using var fixture = await GovernedFixture.CreateAsync();
        var operation = new WriteRepositoryFileOperation(
            "governed-task", Repository, "agents/issue-39", CandidateSha,
            "src/example.cs", "safe content");
        var capability = fixture.Capabilities.Grant(fixture.TaskSnapshot, "agents/issue-39", CandidateSha,
            new[] { "src" }, new[] { WorkTaskAction.WriteFiles }, Now, TimeSpan.FromMinutes(10));
        var dispatcher = new AuthorizedRepositoryOperationDispatcher(
            fixture.Tasks, fixture.Capabilities, fixture.Adapter, fixture.Journal,
            () => Now.AddMinutes(1));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await dispatcher.ExecuteAsync(capability.CapabilityId, operation, Now.AddMinutes(1)));

        Assert.Equal(0, fixture.Adapter.Calls);
        Assert.Empty(fixture.Journal.GetAll());
    }

    private sealed class GovernedFixture : IDisposable
    {
        private GovernedFixture(string directoryPath, PersistentWorkTaskCoordinator tasks,
            WorkTaskSnapshot taskSnapshot, FakeVerifier verifier, FakeAdapter adapter,
            JsonlRepositoryOperationJournal journal)
        {
            DirectoryPath = directoryPath;
            Tasks = tasks;
            TaskSnapshot = taskSnapshot;
            Verifier = verifier;
            Adapter = adapter;
            Journal = journal;
        }

        public string DirectoryPath { get; }
        public PersistentWorkTaskCoordinator Tasks { get; }
        public WorkTaskSnapshot TaskSnapshot { get; }
        public WorkTaskCapabilityRegistry Capabilities { get; } = new();
        public FakeVerifier Verifier { get; }
        public FakeAdapter Adapter { get; }
        public JsonlRepositoryOperationJournal Journal { get; }

        public static async Task<GovernedFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "execution-candidate-admission", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var verifier = new FakeVerifier();
            var tasks = new PersistentWorkTaskCoordinator(
                new JsonlWorkTaskStore(Path.Combine(directory, "tasks.jsonl")));
            var request = new WorkTaskRequest(1, "governed-task", "correlation-governed",
                Repository, 39, "https://github.com/TrentBest/raWWar/issues/39",
                BaseSha, new[] { "src" }, 3);
            await tasks.SubmitAsync(request, verifier);
            var task = await tasks.ClaimAsync("governed-task", "agent-adp",
                TimeSpan.FromMinutes(20), Now, verifier);
            return new GovernedFixture(directory, tasks, task, verifier, new FakeAdapter(),
                new JsonlRepositoryOperationJournal(Path.Combine(directory, "operations.jsonl")));
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(DirectoryPath))
                System.IO.Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class FakeVerifier : IAgentOnboardingAdmissionVerifier
    {
        public int Calls { get; private set; }
        public bool ChangeAfterFirstCall { get; set; }
        public void Reset() => Calls = 0;

        public ValueTask<AgentOnboardingAdmissionEvidence> VerifyAsync(
            string repository, string targetSha, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var digest = ChangeAfterFirstCall && Calls > 1 ? new string('d', 64) : new string('c', 64);
            return ValueTask.FromResult(new AgentOnboardingAdmissionEvidence(
                repository, targetSha, AgentOnboardingAdmissionEvidence.RequiredPath,
                AgentOnboardingAdmissionEvidence.RequiredContractVersion, true, "ELIGIBLE",
                null, "Contract validated for exact candidate commit.", digest));
        }
    }

    private sealed class FakeAdapter : IRepositoryOperationAdapter
    {
        public int Calls { get; private set; }

        public ValueTask<RepositoryOperationResult> ExecuteAsync(
            RepositoryOperation operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(new RepositoryOperationResult(
                "candidate-admission-test", operation.CandidateSha, operation.CandidateSha));
        }
    }
}
