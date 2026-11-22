using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.Foundry;
using TheSingularityWorkshop.Foundry.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class CiAiArtifactReadyIngressTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 17, 30, 0, TimeSpan.Zero);
    private const string Issuer = "https://identity.example.test";
    private const string Audience = "foundry-ci-evidence-ingress";
    private const string Subject = "ciai-production";
    private const string KeyId = "ciai-ingress-2026-01";
    private const string ProviderRepository = "TrentBest/TheSingularityWorkshop.CiAi";

    [Fact]
    public void ValidJwtAndRecipientBindingPersistExactNoticeAndDeduplicateRetry()
    {
        var path = NewPath();
        using var key = RSA.Create(2048);
        try
        {
            var ledger = new JsonlCiAiArtifactNoticeReceiptLedger(path);
            var ingress = CreateIngress(key, ledger);
            var body = Notice(ProviderRepository);
            var token = Token(key, new[] { ProviderRepository }, Now, Audience);
            var first = ingress.Accept("Bearer " + token, body);
            var retry = ingress.Accept("Bearer " + token, body);

            Assert.Equal(CiAiArtifactNoticeReceiptOutcome.Accepted, first.Outcome);
            Assert.Equal(CiAiArtifactNoticeReceiptOutcome.Duplicate, retry.Outcome);
            Assert.Equal(first.Receipt.ReceiptId, retry.Receipt.ReceiptId);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant(),
                first.Receipt.PayloadSha256);
            Assert.Single(new JsonlCiAiArtifactNoticeReceiptLedger(path).GetAll());
        }
        finally { Delete(path); }
    }

    [Fact]
    public void InvalidSignatureExpiredTokenAndWrongAudienceAreRejected()
    {
        var path = NewPath();
        using var key = RSA.Create(2048);
        using var attacker = RSA.Create(2048);
        try
        {
            var ingress = CreateIngress(key, new JsonlCiAiArtifactNoticeReceiptLedger(path));
            var body = Notice(ProviderRepository);
            Assert.Throws<UnauthorizedAccessException>(() =>
                ingress.Accept("Bearer " + Token(attacker, new[] { ProviderRepository }, Now, Audience), body));
            Assert.Throws<UnauthorizedAccessException>(() =>
                ingress.Accept("Bearer " + Token(key, new[] { ProviderRepository }, Now.AddMinutes(-10), Audience), body));
            Assert.Throws<UnauthorizedAccessException>(() =>
                ingress.Accept("Bearer " + Token(key, new[] { ProviderRepository }, Now, "wrong-audience"), body));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void RepositoryAndRecipientAreBoundBeforeDurableAcceptance()
    {
        var path = NewPath();
        using var key = RSA.Create(2048);
        try
        {
            var ingress = CreateIngress(key, new JsonlCiAiArtifactNoticeReceiptLedger(path));
            Assert.Throws<UnauthorizedAccessException>(() =>
                ingress.Accept("Bearer " + Token(key, new[] { "attacker/Repo" }, Now, Audience), Notice(ProviderRepository)));

            var wrongRecipient = Encoding.UTF8.GetBytes(NoticeJson("wrong-recipient"));
            Assert.Throws<UnauthorizedAccessException>(() =>
                ingress.Accept("Bearer " + Token(key, new[] { ProviderRepository }, Now, Audience), wrongRecipient));
            Assert.Empty(new JsonlCiAiArtifactNoticeReceiptLedger(path).GetAll());
        }
        finally { Delete(path); }
    }

    private static CiAiArtifactReadyIngress CreateIngress(RSA key, JsonlCiAiArtifactNoticeReceiptLedger ledger)
    {
        var publicKey = RSA.Create();
        publicKey.ImportParameters(key.ExportParameters(includePrivateParameters: false));
        return new CiAiArtifactReadyIngress(
            new CiAiIngressAuthenticationPolicy(Issuer, Audience,
                "foundry.ci-evidence.deliver", "foundry-production", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30)),
            new[] { new CiAiIngressSender(Subject, KeyId, publicKey,
                new HashSet<string>(StringComparer.Ordinal) { ProviderRepository }) },
            ledger, () => Now);
    }

    private static byte[] Notice(string providerRepository) => Encoding.UTF8.GetBytes(NoticeJson("foundry-production", providerRepository));

    private static string NoticeJson(string recipient, string providerRepository = ProviderRepository) =>
        JsonSerializer.Serialize(new
        {
            contractVersion = "ciai.artifact-ready.v1",
            noticeId = "notice-001",
            recipientIdentity = recipient,
            notifiedAt = Now,
            manifest = new
            {
                requestId = "request-001",
                operationId = "operation-001",
                idempotencyKey = "artifact-key-001",
                providerRepository,
                clientIdentity = recipient,
                artifactId = "artifact-001"
            }
        });

    private static string Token(RSA key, string[] repositories, DateTimeOffset issuedAt, string audience)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid = KeyId });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = Issuer,
            aud = audience,
            sub = Subject,
            scope = "foundry.ci-evidence.deliver",
            repositories,
            iat = issuedAt.ToUnixTimeSeconds(),
            nbf = issuedAt.ToUnixTimeSeconds(),
            exp = issuedAt.AddMinutes(4).ToUnixTimeSeconds(),
            jti = Guid.NewGuid().ToString("N")
        });
        var signingInput = Encode(header) + "." + Encode(payload);
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Encode(signature);
    }

    private static string NewPath() => Path.Combine(Path.GetTempPath(),
        "foundry-ciai-ingress-" + Guid.NewGuid().ToString("N") + ".jsonl");

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
