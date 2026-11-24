using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Foundry;
using TheSingularityWorkshop.Foundry.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiWorkflowEvidencePacketValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 19, 0, 0, TimeSpan.Zero);
    private const string Repository = "TrentBest/raWWar";
    private const string CandidateRef = "refs/heads/feature/physics-motion";
    private const string CandidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TrustedSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string WorkflowPath = ".github/workflows/build.yml";

    [Fact]
    public void AcceptsAuthoritativePacketOnlyWhenEveryRunAndCandidateIdentityMatches()
    {
        var fixture = Fixture();
        var bytes = Packet(fixture.Mapping.Mapping, fixture.Binding, CandidateSha);
        var verified = new CiAiWorkflowEvidencePacketValidator().Validate(bytes, fixture.Mapping, Now);

        Assert.Equal("request-1", verified.RequestId);
        Assert.Equal("task-1", verified.TaskId);
        Assert.Equal(fixture.Binding.RunId, verified.RunBinding.RunId);
        Assert.Equal(fixture.Binding.RunAttempt, verified.RunBinding.RunAttempt);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            verified.PacketSha256);
        Assert.True(verified.IsSuccessful);
    }

    [Fact]
    public void RejectsCandidateShaMismatchEvenWhenPacketIsInternallyValid()
    {
        var fixture = Fixture();
        var bytes = Packet(fixture.Mapping.Mapping, fixture.Binding,
            "cccccccccccccccccccccccccccccccccccccccc");

        Assert.Throws<UnauthorizedAccessException>(() =>
            new CiAiWorkflowEvidencePacketValidator().Validate(bytes, fixture.Mapping, Now));
    }

    [Fact]
    public void RejectsWrongRunAttemptAndDuplicateJsonProperties()
    {
        var fixture = Fixture();
        var wrongAttempt = Packet(fixture.Mapping.Mapping, fixture.Binding, CandidateSha, attempt: 1);
        Assert.Throws<UnauthorizedAccessException>(() =>
            new CiAiWorkflowEvidencePacketValidator().Validate(wrongAttempt, fixture.Mapping, Now));

        var valid = System.Text.Encoding.UTF8.GetString(Packet(fixture.Mapping.Mapping, fixture.Binding, CandidateSha));
        var duplicate = System.Text.Encoding.UTF8.GetBytes(valid.Replace(
            "\"schema\": \"ciai.workflow-evidence.v2\"",
            "\"schema\": \"ciai.workflow-evidence.v2\", \"schema\": \"ciai.workflow-evidence.v2\"",
            StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() =>
            new CiAiWorkflowEvidencePacketValidator().Validate(duplicate, fixture.Mapping, Now));
    }

    [Fact]
    public void RejectsUnboundExpiredAndCancelledMappings()
    {
        var fixture = Fixture();
        var validator = new CiAiWorkflowEvidencePacketValidator();
        var bytes = Packet(fixture.Mapping.Mapping, fixture.Binding, CandidateSha);

        Assert.Throws<UnauthorizedAccessException>(() =>
            validator.Validate(bytes, fixture.Mapping with { RunBinding = null }, Now));
        Assert.Throws<UnauthorizedAccessException>(() =>
            validator.Validate(bytes, fixture.Mapping with { Cancelled = true }, Now));
        Assert.Throws<UnauthorizedAccessException>(() =>
            validator.Validate(bytes, fixture.Mapping, Now.AddDays(2)));
    }

    private static (CiAiRequestTaskMappingSnapshot Mapping, CiAiDispatchRunBinding Binding) Fixture()
    {
        var mapping = new CiAiRequestTaskMapping(1, "request-1", "operation-1", "idempotency-1",
            "task-1", "correlation-1", Repository, CandidateRef, CandidateSha, WorkflowPath,
            "refs/heads/development", TrustedSha, "Build", "Resolve candidate", "Verify candidate",
            "ciai-provider", "https://artifacts.example.test/",
            "https://ciai.example.test/acknowledge", "ack-profile", Now.AddMinutes(-5), Now.AddHours(1));
        var binding = new CiAiDispatchRunBinding(123, 2, Repository, WorkflowPath,
            "refs/heads/development", TrustedSha, CandidateRef, CandidateSha,
            "workflow_dispatch", Now.AddMinutes(-4));
        return (new CiAiRequestTaskMappingSnapshot(mapping, binding, false), binding);
    }

    private static byte[] Packet(CiAiRequestTaskMapping mapping, CiAiDispatchRunBinding binding,
        string candidateSha, int? attempt = null)
    {
        var run = new CiRunSnapshot(Repository, binding.RunId, "Build", TrustedSha,
            CiRunStatus.Completed, CiRunConclusion.Success,
            new Uri($"https://github.com/{Repository}/actions/runs/{binding.RunId}"), Now);
        var runObservation = new CiWorkflowRunObservation(run, attempt ?? binding.RunAttempt,
            WorkflowPath, "workflow_dispatch", Array.Empty<int>())
        {
            WorkflowRef = "refs/heads/development",
            ProviderStatus = "completed"
        };
        var runs = new CiWorkflowRunCollection(Repository, TrustedSha,
            new[] { runObservation }, 1, 1, false);

        var job = new CiWorkflowJobObservation(456, binding.RunId, attempt ?? binding.RunAttempt,
            "Build", "completed", "success",
            new Uri($"https://github.com/{Repository}/actions/runs/{binding.RunId}/job/456"),
            new[]
            {
                new CiWorkflowStepObservation(1, "Resolve candidate", "completed", "success", Now.ToString("O")),
                new CiWorkflowStepObservation(2, "Verify candidate", "completed", "success", Now.ToString("O")),
                new CiWorkflowStepObservation(3, "Build", "completed", "success", Now.ToString("O"))
            });
        var jobs = new CiWorkflowJobCollection(Repository, binding.RunId, attempt ?? binding.RunAttempt,
            new[] { job }, 1, 1, false);
        var checkout = new CiWorkflowCandidateCheckoutEvidence(CandidateRef, candidateSha,
            mapping.JobName, mapping.ResolutionStepName, mapping.VerificationStepName);
        var packet = CiWorkflowEvidencePacket.FromCollections(runs, jobs, binding.RunId,
            attempt ?? binding.RunAttempt, Now, checkout);
        return System.Text.Encoding.UTF8.GetBytes(packet.ToJson());
    }
}
