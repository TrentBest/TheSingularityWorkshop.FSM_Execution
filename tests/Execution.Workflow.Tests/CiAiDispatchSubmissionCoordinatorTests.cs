using TheSingularityWorkshop.Execution;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class CiAiDispatchSubmissionCoordinatorTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

    private static CiAiRequestTaskMapping Mapping(string candidateSha = null!) =>
        new(1, "request-1", "operation-1", "idempotency-1", "task-1", "correlation-1",
            "TrentBest/TheSingularityWorkshop.Execution", "refs/heads/work/task-1",
            candidateSha ?? new string('a', 40), ".github/workflows/build.yml",
            "refs/heads/development", new string('b', 40), "build", "Resolve candidate",
            "Verify candidate", "ciai-production", "https://artifacts.example.test/",
            "https://ciai.example.test/api/v1/integrations/ciai/artifact-receipts",
            "ciai-ack-profile", Created, Created.AddHours(2));

    private sealed class RecordingDispatcher : ICiAiWorkflowDispatcher
    {
        public int Calls { get; private set; }
        public Func<CiAiRequestTaskMapping, CancellationToken, Task>? OnDispatch { get; init; }

        public Task DispatchAsync(CiAiRequestTaskMapping mapping, CancellationToken cancellationToken)
        {
            Calls++;
            return OnDispatch?.Invoke(mapping, cancellationToken) ?? Task.CompletedTask;
        }
    }

    [Fact]
    public async Task PersistsIntentBeforeCallingHostDispatcher()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-dispatch-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            var dispatcher = new RecordingDispatcher
            {
                OnDispatch = (mapping, _) =>
                {
                    var persisted = journal.Get(mapping.RequestId);
                    Assert.Equal(mapping, persisted.Mapping);
                    Assert.Null(persisted.RunBinding);
                    return Task.CompletedTask;
                }
            };

            var result = await CiAiDispatchSubmissionCoordinator.SubmitAsync(journal, Mapping(), dispatcher);

            Assert.Equal(CiAiDispatchSubmissionOutcome.SubmittedAwaitingReconciliation, result.Outcome);
            Assert.Equal("request-1", result.RequestId);
            Assert.Equal(1, dispatcher.Calls);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ExactExistingIntentRequiresReconciliationAndIsNeverRedispatched()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-dispatch-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            var dispatcher = new RecordingDispatcher();

            var first = await CiAiDispatchSubmissionCoordinator.SubmitAsync(journal, Mapping(), dispatcher);
            var retry = await CiAiDispatchSubmissionCoordinator.SubmitAsync(journal, Mapping(), dispatcher);

            Assert.Equal(CiAiDispatchSubmissionOutcome.SubmittedAwaitingReconciliation, first.Outcome);
            Assert.Equal(CiAiDispatchSubmissionOutcome.ExistingIntentRequiresReconciliation, retry.Outcome);
            Assert.Equal(1, dispatcher.Calls);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ConflictingIntentNeverCallsHostDispatcher()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-dispatch-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            journal.CreateDispatchIntent(Mapping());
            var dispatcher = new RecordingDispatcher();

            var result = await CiAiDispatchSubmissionCoordinator.SubmitAsync(
                journal, Mapping(new string('c', 40)), dispatcher);

            Assert.Equal(CiAiDispatchSubmissionOutcome.Conflict, result.Outcome);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task AmbiguousDispatchFailureLeavesIntentDurableAndPreventsBlindRetry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-dispatch-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            var dispatcher = new RecordingDispatcher
            {
                OnDispatch = (_, _) => Task.FromException(new TimeoutException("Dispatch response was lost."))
            };

            await Assert.ThrowsAsync<TimeoutException>(() =>
                CiAiDispatchSubmissionCoordinator.SubmitAsync(journal, Mapping(), dispatcher));

            Assert.Equal(Mapping(), journal.Get("request-1").Mapping);
            Assert.Null(journal.Get("request-1").RunBinding);

            var retry = await CiAiDispatchSubmissionCoordinator.SubmitAsync(journal, Mapping(), dispatcher);

            Assert.Equal(CiAiDispatchSubmissionOutcome.ExistingIntentRequiresReconciliation, retry.Outcome);
            Assert.Equal(1, dispatcher.Calls);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task GovernedDispatchPersistsExactCandidateAdmissionBeforeDispatchAndRevalidates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-dispatch-admission-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            var evidence = AdmissionEvidence('d');
            var verifier = new SequenceAdmissionVerifier(evidence, evidence);
            var dispatcher = new RecordingDispatcher();

            var result = await CiAiDispatchSubmissionCoordinator.SubmitAsync(
                journal, GovernedMapping(), dispatcher, verifier);

            Assert.Equal(CiAiDispatchSubmissionOutcome.SubmittedAwaitingReconciliation, result.Outcome);
            Assert.Equal(2, verifier.Calls);
            Assert.Equal(1, dispatcher.Calls);
            Assert.Equal(evidence, journal.Get("request-1").Mapping.AdmissionEvidence);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task GovernedDispatchRejectsBeforeIntentWhenCandidateIsNotEligible()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-dispatch-admission-reject-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            var rejected = AdmissionEvidence('d') with
            {
                IsEligible = false,
                Outcome = "NOT_ELIGIBLE",
                FailureCode = "onboarding.missing",
                ContentSha256 = null
            };
            var verifier = new SequenceAdmissionVerifier(rejected);
            var dispatcher = new RecordingDispatcher();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                CiAiDispatchSubmissionCoordinator.SubmitAsync(
                    journal, GovernedMapping(), dispatcher, verifier));

            Assert.Equal(1, verifier.Calls);
            Assert.Equal(0, dispatcher.Calls);
            Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task GovernedDispatchDoesNotDispatchWhenAdmissionChangesAfterIntentPersistence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"execution-dispatch-admission-change-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new JsonlCiAiRequestTaskMappingJournal(path);
            var initial = AdmissionEvidence('d');
            var changed = AdmissionEvidence('e');
            var verifier = new SequenceAdmissionVerifier(initial, changed);
            var dispatcher = new RecordingDispatcher();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                CiAiDispatchSubmissionCoordinator.SubmitAsync(
                    journal, GovernedMapping(), dispatcher, verifier));

            Assert.Equal(2, verifier.Calls);
            Assert.Equal(0, dispatcher.Calls);
            Assert.Equal(initial, journal.Get("request-1").Mapping.AdmissionEvidence);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static CiAiRequestTaskMapping GovernedMapping() =>
        Mapping() with { Repository = "TrentBest/raWWar" };

    private static AgentOnboardingAdmissionEvidence AdmissionEvidence(char digest) => new(
        "TrentBest/raWWar", new string('a', 40),
        AgentOnboardingAdmissionEvidence.RequiredPath,
        AgentOnboardingAdmissionEvidence.RequiredContractVersion,
        true, "ELIGIBLE", null, "CiAi v1 onboarding contract passed at exact candidate SHA.",
        new string(digest, 64));

    private sealed class SequenceAdmissionVerifier(params AgentOnboardingAdmissionEvidence[] evidence)
        : IAgentOnboardingAdmissionVerifier
    {
        public int Calls { get; private set; }

        public ValueTask<AgentOnboardingAdmissionEvidence> VerifyAsync(string repository,
            string targetSha, CancellationToken cancellationToken = default)
        {
            var result = evidence[Math.Min(Calls, evidence.Length - 1)];
            Calls++;
            return ValueTask.FromResult(result);
        }
    }

}
