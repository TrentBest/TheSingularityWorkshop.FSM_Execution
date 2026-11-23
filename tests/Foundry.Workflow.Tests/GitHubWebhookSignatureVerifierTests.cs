using System.Security.Cryptography;
using System.Text;
using TheSingularityWorkshop.FSM_Execution.GitHubHost;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class GitHubWebhookSignatureVerifierTests
{
    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void ExactRawBodyAndCorrectSha256HeaderAreAccepted()
    {
        var body = Encoding.UTF8.GetBytes("{\"action\":\"opened\",\"issue\":{\"number\":17}}");
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);

        Assert.True(verifier.Verify(body, Signature(body)));
        verifier.Authenticate(body, Signature(body));
    }

    [Fact]
    public void WhitespaceOrContentChangesInvalidateSignature()
    {
        var signedBody = Encoding.UTF8.GetBytes("{\"action\":\"opened\"}");
        var changedBody = Encoding.UTF8.GetBytes("{ \"action\":\"opened\"}");
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);

        Assert.False(verifier.Verify(changedBody, Signature(signedBody)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=0000000000000000000000000000000000000000")]
    [InlineData("sha256=not-hex")]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000 ")]
    public void MissingOrMalformedSignatureIsRejected(string? signature)
    {
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);

        Assert.False(verifier.Verify(Encoding.UTF8.GetBytes("{}"), signature));
    }

    [Fact]
    public void EmptyAndOversizedBodiesAreRejected()
    {
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);

        Assert.False(verifier.Verify([], Signature([])));
        var oversized = new byte[GitHubWebhookSignatureVerifier.MaximumBodyBytes + 1];
        Assert.False(verifier.Verify(oversized, Signature(oversized)));
    }

    [Fact]
    public void InvalidSecretLengthIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new GitHubWebhookSignatureVerifier(new byte[31]));
    }

    [Fact]
    public void AuthenticateThrowsForAnInvalidSignature()
    {
        using var verifier = new GitHubWebhookSignatureVerifier(Secret);

        Assert.Throws<UnauthorizedAccessException>(() =>
            verifier.Authenticate(Encoding.UTF8.GetBytes("{}"), "sha256=" + new string('0', 64)));
    }

    [Fact]
    public void DisposeZeroesSecretAndPreventsFurtherUse()
    {
        var verifier = new GitHubWebhookSignatureVerifier(Secret);
        verifier.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            verifier.Verify(Encoding.UTF8.GetBytes("{}"), "sha256=" + new string('0', 64)));
    }

    private static string Signature(ReadOnlySpan<byte> body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Secret, body)).ToLowerInvariant();
}
