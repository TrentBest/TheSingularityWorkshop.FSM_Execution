using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Foundry;
using TheSingularityWorkshop.Foundry.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiArtifactRetrievalServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RetrievesOnlyExactOriginArtifactAndVerifiesManifestBytes()
    {
        var payload = Encoding.UTF8.GetBytes("{\"schema\":\"ciai.workflow-evidence.v2\",\"result\":\"pending\"}");
        var fixture = Fixture(payload);
        var transport = new FakeTransport(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(payload)
        });
        var service = Service(transport);

        var artifact = await service.RetrieveAsync(fixture.Receipt, fixture.Mapping, fixture.Sender);

        Assert.Equal(fixture.Manifest.Digest, artifact.PayloadSha256);
        Assert.Equal(payload, artifact.Payload.ToArray());
        Assert.Equal("Bearer", transport.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal(new Uri("https://artifacts.example.com/evidence.json"), transport.LastRequest.RequestUri);
        var copy = artifact.Payload.ToArray();
        copy[0] ^= 0xFF;
        Assert.Equal(payload, artifact.Payload.ToArray());
    }

    [Fact]
    public async Task RejectsProviderRedirectInsteadOfFollowingIt()
    {
        var fixture = Fixture(Encoding.UTF8.GetBytes("{\"schema\":\"ciai.workflow-evidence.v2\"}"));
        var response = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://other.example.net/evidence.json") },
            Content = JsonContent(Array.Empty<byte>())
        };
        var service = Service(new FakeTransport(response));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RetrieveAsync(fixture.Receipt, fixture.Mapping, fixture.Sender));
    }

    [Fact]
    public async Task RejectsDigestMismatchAndWrongCapturedOrigin()
    {
        var payload = Encoding.UTF8.GetBytes("{\"schema\":\"ciai.workflow-evidence.v2\"}");
        var fixture = Fixture(payload);
        var changed = Encoding.UTF8.GetBytes("{\"schema\":\"ciai.workflow-evidence.v2\",\"tampered\":true}");
        var service = Service(new FakeTransport(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(changed)
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RetrieveAsync(fixture.Receipt, fixture.Mapping, fixture.Sender));

        var wrongOrigin = fixture with
        {
            Mapping = fixture.Mapping with
            {
                Mapping = fixture.Mapping.Mapping with { ArtifactOrigin = "https://other.example.net/" }
            }
        };
        var unusedTransport = new FakeTransport(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(payload)
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(unusedTransport).RetrieveAsync(wrongOrigin.Receipt, wrongOrigin.Mapping, wrongOrigin.Sender));
        Assert.Null(unusedTransport.LastRequest);
    }

    private static CiAiArtifactRetrievalService Service(FakeTransport transport) =>
        new(new ArtifactRetrievalDestinationPolicy(new[] { "artifacts.example.com" }),
            new FakeCredentials(), transport, "foundry", () => Now);

    private static FixtureData Fixture(byte[] payload)
    {
        var created = Now.AddMinutes(-2);
        var expires = Now.AddHours(1);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        const string requestId = "request-1";
        const string operationId = "operation-1";
        const string idempotencyKey = "artifact-key-1";
        const string candidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string provider = "TrentBest/TheSingularityWorkshop.CiAi";
        const string repository = "TrentBest/raWWar";
        const string candidateRef = "refs/heads/agents/candidate-1";
        const string workflowPath = ".github/workflows/validate.yml";
        const string trustedSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var uri = new Uri("https://artifacts.example.com/evidence.json");
        var manifest = new ArtifactManifest(requestId, operationId, idempotencyKey, provider,
            "workflow-evidence", "1.0", "d4d2aa0", "artifact-1", "v1", candidateSha,
            "SHA-256", digest, "application/json", payload.Length, uri, created, expires,
            "short", "foundry");
        var notice = new ArtifactReadyNotice(1, "notice-1", "foundry", manifest, created.AddSeconds(1));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(notice, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var normalized = CiAiArtifactReadyNotice.FromUtf8Bytes("ciai-sender", "notice-1", idempotencyKey, bytes);
        var ledgerPath = Path.Combine(Path.GetTempPath(), "foundry-artifact-receipt-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var receipt = new JsonlCiAiArtifactNoticeReceiptLedger(ledgerPath).Accept(normalized, Now).Receipt;
        File.Delete(ledgerPath);

        var mapping = new CiAiRequestTaskMapping(1, requestId, operationId, idempotencyKey,
            "task-1", "correlation-1", repository, candidateRef, candidateSha, workflowPath,
            "refs/heads/development", trustedSha, "Build", "Resolve", "Verify",
            "ciai-sender", "https://artifacts.example.com/", "https://ci.example.com/ack",
            "ack-profile", created, expires);
        var binding = new CiAiDispatchRunBinding(123, 1, repository, workflowPath,
            "refs/heads/development", trustedSha, candidateRef, candidateSha,
            "workflow_dispatch", created.AddSeconds(2));
        var snapshot = new CiAiRequestTaskMappingSnapshot(mapping, binding, false);
        var rsa = RSA.Create(2048);
        var sender = new CiAiIngressSender("ciai-sender", "key-1", rsa,
            new HashSet<string>(StringComparer.Ordinal) { provider });
        return new FixtureData(manifest, receipt, snapshot, sender, ledgerPath);
    }

    private static ByteArrayContent JsonContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private sealed record FixtureData(ArtifactManifest Manifest, CiAiArtifactNoticeReceipt Receipt,
        CiAiRequestTaskMappingSnapshot Mapping, CiAiIngressSender Sender, string LedgerPath);

    private sealed class FakeCredentials : ICiAiArtifactCredentialProvider
    {
        public ValueTask<AuthenticationHeaderValue> GetAuthorizationAsync(
            string providerRepository, Uri artifactUri, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AuthenticationHeaderValue("Bearer", "short-lived-provider-token"));
    }

    private sealed class FakeTransport(HttpResponseMessage response) : ICiAiArtifactHttpTransport
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = new HttpRequestMessage(request.Method, request.RequestUri);
            LastRequest.Headers.Authorization = request.Headers.Authorization;
            LastRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response.RequestMessage = LastRequest;
            return Task.FromResult(response);
        }
    }
}
