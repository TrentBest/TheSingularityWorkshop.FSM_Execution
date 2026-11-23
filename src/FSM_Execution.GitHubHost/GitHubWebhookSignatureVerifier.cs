using System.Security.Cryptography;

namespace TheSingularityWorkshop.FSM_Execution.GitHubHost;

/// <summary>
/// Verifies GitHub's X-Hub-Signature-256 header against the exact, unmodified
/// request-body bytes. Repository enrollment and event parsing happen only
/// after this verifier succeeds.
/// </summary>
public sealed class GitHubWebhookSignatureVerifier : IDisposable
{
    public const int MaximumBodyBytes = 1024 * 1024;
    private const string HeaderPrefix = "sha256=";
    private const int DigestBytes = 32;
    private readonly byte[] _secret;
    private bool _disposed;

    public GitHubWebhookSignatureVerifier(ReadOnlySpan<byte> secret)
    {
        if (secret.Length < DigestBytes)
            throw new ArgumentException("GitHub webhook secrets must contain at least 32 bytes.", nameof(secret));

        _secret = secret.ToArray();
    }

    /// <summary>Returns false for malformed headers, invalid signatures, or out-of-policy bodies.</summary>
    public bool Verify(ReadOnlySpan<byte> exactRequestBody, string? signatureHeader)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (exactRequestBody.IsEmpty || exactRequestBody.Length > MaximumBodyBytes ||
            signatureHeader is null ||
            signatureHeader.Length != HeaderPrefix.Length + DigestBytes * 2 ||
            !signatureHeader.StartsWith(HeaderPrefix, StringComparison.Ordinal))
            return false;

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(signatureHeader.AsSpan(HeaderPrefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        if (supplied.Length != DigestBytes)
            return false;

        var expected = HMACSHA256.HashData(_secret, exactRequestBody);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    /// <summary>Rejects the request unless the exact raw body has a valid GitHub HMAC.</summary>
    public void Authenticate(ReadOnlySpan<byte> exactRequestBody, string? signatureHeader)
    {
        if (!Verify(exactRequestBody, signatureHeader))
            throw new UnauthorizedAccessException("GitHub webhook signature is missing, malformed, invalid, or outside the body-size policy.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        CryptographicOperations.ZeroMemory(_secret);
        _disposed = true;
    }
}
