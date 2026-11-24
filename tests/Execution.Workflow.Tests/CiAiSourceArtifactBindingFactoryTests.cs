using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using TheSingularityWorkshop.Execution;
using Xunit;

namespace Execution.Workflow.Tests;

public sealed class CiAiSourceArtifactBindingFactoryTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-31T10:00:00Z");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-31T10:40:00Z");
    private const string Repository = "TrentBest/TheSingularityWorkshop.Execution";
    private const string WorkflowPath = ".github/workflows/build.yml";
    private const string WorkflowSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EventSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string CandidateSha = "cccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void CreatesBindingOnlyFromExactRunJobArtifactObservationAndVerifiedArchiveBytes()
    {
        var mapping = CreateMapping();
        var run = CreateRun(mapping);
        var trx = Encoding.UTF8.GetBytes("<TestRun id=\"exact-source\" />");
        var archive = CreateArchive(trx);
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        var observation = new CiAiProviderSourceArtifactObservation(
            Repository, 1001, 1, CandidateSha, WorkflowPath,
            2001, "Build and Test", "required-check", "completed", "failure",
            3001, "test-results", digest, archive.Length,
            Created.AddMinutes(2), Created.AddMinutes(20),
            Created.AddMinutes(21), Created.AddDays(7));

        var binding = CiAiSourceArtifactBindingFactory.VerifyAndBind(
            mapping, run, observation, archive, "results.trx", "provenance.json", Now);

        Assert.Equal(digest, binding.ProviderArchiveDigest);
        Assert.Equal(archive.Length, binding.ArchiveSizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(trx)).ToLowerInvariant(), binding.PayloadSha256);
        Assert.Equal(trx.Length, binding.PayloadSizeBytes);
        Assert.Equal("completed", binding.JobStatus);
        Assert.Equal("failure", binding.JobConclusion);
        Assert.Equal(64, binding.IdentitySha256.Length);
    }

    [Fact]
    public void RejectsProviderObservationForDifferentJobOrRunAttempt()
    {
        var mapping = CreateMapping();
        var run = CreateRun(mapping);
        var archive = CreateArchive(Encoding.UTF8.GetBytes("<TestRun />"));
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        var observation = new CiAiProviderSourceArtifactObservation(
            Repository, 1001, 2, CandidateSha, WorkflowPath,
            2001, "Build and Test", "required-check", "completed", "failure",
            3001, "test-results", digest, archive.Length,
            Created.AddMinutes(2), Created.AddMinutes(20),
            Created.AddMinutes(21), Created.AddDays(7));

        Assert.Throws<UnauthorizedAccessException>(() =>
            CiAiSourceArtifactBindingFactory.VerifyAndBind(
                mapping, run, observation, archive, "results.trx", "provenance.json", Now));
    }

    [Fact]
    public void RejectsArchiveBytesThatDoNotMatchProviderDigest()
    {
        var mapping = CreateMapping();
        var run = CreateRun(mapping);
        var archive = CreateArchive(Encoding.UTF8.GetBytes("<TestRun />"));
        var observation = new CiAiProviderSourceArtifactObservation(
            Repository, 1001, 1, CandidateSha, WorkflowPath,
            2001, "Build and Test", "required-check", "completed", "failure",
            3001, "test-results", "sha256:" + new string('f', 64), archive.Length,
            Created.AddMinutes(2), Created.AddMinutes(20),
            Created.AddMinutes(21), Created.AddDays(7));

        Assert.Throws<InvalidDataException>(() =>
            CiAiSourceArtifactBindingFactory.VerifyAndBind(
                mapping, run, observation, archive, "results.trx", "provenance.json", Now));
    }

    private static CiAiRequestTaskMapping CreateMapping() =>
        new(1, "request-1", "operation-1", "idempotency-1", "task-1", "correlation-1",
            Repository, "refs/heads/work/task-1", CandidateSha, WorkflowPath,
            "refs/heads/development", WorkflowSha,
            "Build and Test", "resolve", "verify", "ciai-production",
            "https://artifacts.example.test/",
            "https://ciai.example.test/api/v1/integrations/ciai/artifact-receipts",
            "ciai-ack-profile", Created, Created.AddHours(2), TrustedEventSha: EventSha)
        {
            JobKey = "required-check",
            SourceArtifactName = "test-results"
        }.Validate();

    private static CiAiDispatchRunBinding CreateRun(CiAiRequestTaskMapping mapping) =>
        new(1001, 1, Repository, WorkflowPath, "refs/heads/development", WorkflowSha,
            "refs/heads/work/task-1", CandidateSha, "workflow_dispatch",
            Created.AddMinutes(1), EventSha: EventSha).ValidateAgainst(mapping);

    private static byte[] CreateArchive(byte[] trx)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var trxEntry = archive.CreateEntry("results.trx", CompressionLevel.NoCompression);
            using (var output = trxEntry.Open()) output.Write(trx);
            var provenanceEntry = archive.CreateEntry("provenance.json", CompressionLevel.NoCompression);
            using var provenance = provenanceEntry.Open();
            provenance.Write(Encoding.UTF8.GetBytes("{\"schema\":\"ciai.source-artifact.v1\"}"));
        }
        return stream.ToArray();
    }
}
