using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>One registered CiAi sender and its trusted RSA signing key.</summary>
public sealed record CiAiIngressSender(string Subject, string KeyId, RSA PublicKey, IReadOnlySet<string> Repositories)
{
    public CiAiIngressSender Validate()
    {
        if (string.IsNullOrWhiteSpace(Subject) || Subject.Length > 128 || Subject.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(KeyId) || KeyId.Length > 128 || KeyId.Any(char.IsControl))
            throw new ArgumentException("CiAi sender subject and key ID must be bounded identities.");
        ArgumentNullException.ThrowIfNull(PublicKey);
        ArgumentNullException.ThrowIfNull(Repositories);
        if (Repositories.Count == 0 || Repositories.Any(value =>
            string.IsNullOrWhiteSpace(value) || value.Length > 201 || value.Any(char.IsControl) ||
            value.Split('/') is not { Length: 2 }))
            throw new ArgumentException("A sender must have an explicit owner/name repository allowlist.");
        return this;
    }
}

/// <summary>Fixed host policy for the short-lived CiAi artifact-ingress JWT.</summary>
public sealed record CiAiIngressAuthenticationPolicy(
    string Issuer,
    string Audience,
    string RequiredScope,
    string RecipientIdentity,
    TimeSpan MaximumLifetime,
    TimeSpan ClockSkew)
{
    public CiAiIngressAuthenticationPolicy Validate()
    {
        static void Required(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
                throw new ArgumentException("Ingress authentication policy values must be bounded.", name);
        }
        Required(Issuer, nameof(Issuer));
        Required(Audience, nameof(Audience));
        Required(RequiredScope, nameof(RequiredScope));
        Required(RecipientIdentity, nameof(RecipientIdentity));
        if (MaximumLifetime <= TimeSpan.Zero || MaximumLifetime > TimeSpan.FromMinutes(5) ||
            ClockSkew < TimeSpan.Zero || ClockSkew > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(MaximumLifetime),
                "Ingress JWT lifetime must be at most five minutes and clock skew at most one minute.");
        return this;
    }
}

public sealed record CiAiAuthenticatedIngressResult(
    CiAiArtifactNoticeReceiptOutcome Outcome,
    CiAiArtifactNoticeReceipt Receipt);

/// <summary>
/// Transport-neutral authenticated ingress boundary. An HTTPS endpoint must enforce TLS,
/// bounded request reads, and rate limits, then pass the exact Authorization bearer token
/// and raw request bytes here. This class validates RS256 JWT identity/authorization and
/// recipient binding before the durable notice ledger accepts the exact payload.
/// </summary>
public sealed class CiAiArtifactReadyIngress
{
    private const int MaximumTokenCharacters = 16 * 1024;
    private readonly CiAiIngressAuthenticationPolicy _policy;
    private readonly IReadOnlyDictionary<string, CiAiIngressSender> _sendersByKey;
    private readonly JsonlCiAiArtifactNoticeReceiptLedger _receipts;
    private readonly Func<DateTimeOffset> _clock;

    public CiAiArtifactReadyIngress(
        CiAiIngressAuthenticationPolicy policy,
        IEnumerable<CiAiIngressSender> senders,
        JsonlCiAiArtifactNoticeReceiptLedger receipts,
        Func<DateTimeOffset>? clock = null)
    {
        _policy = (policy ?? throw new ArgumentNullException(nameof(policy))).Validate();
        ArgumentNullException.ThrowIfNull(senders);
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        var map = new Dictionary<string, CiAiIngressSender>(StringComparer.Ordinal);
        foreach (var sender in senders)
        {
            sender.Validate();
            if (!map.TryAdd(sender.KeyId, sender))
                throw new ArgumentException($"Duplicate CiAi ingress key ID '{sender.KeyId}'.", nameof(senders));
        }
        if (map.Count == 0)
            throw new ArgumentException("At least one registered CiAi sender is required.", nameof(senders));
        _sendersByKey = map;
    }

    public CiAiAuthenticatedIngressResult Accept(
        string authorizationHeader, ReadOnlySpan<byte> exactRequestBody)
    {
        var now = _clock();
        if (now == default) throw new InvalidOperationException("Ingress clock returned an invalid timestamp.");
        var sender = Authenticate(authorizationHeader, now, out var allowedRepositories);
        if (exactRequestBody.IsEmpty || exactRequestBody.Length > 64 * 1024)
            throw new ArgumentException("CiAi artifact-ready notice must be between 1 byte and 64 KiB.", nameof(exactRequestBody));

        string noticeId;
        string idempotencyKey;
        string repository;
        string recipient;
        try
        {
            using var document = JsonDocument.Parse(exactRequestBody, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryString(root, "noticeId", out noticeId) ||
                !TryString(root, "recipientIdentity", out recipient) ||
                !root.TryGetProperty("manifest", out var manifest) || manifest.ValueKind != JsonValueKind.Object ||
                !TryString(manifest, "idempotencyKey", out idempotencyKey) ||
                !TryString(manifest, "repository", out repository))
                throw new UnauthorizedAccessException("Artifact-ready notice is missing its required recipient or manifest identity.");
            if (!string.Equals(recipient, _policy.RecipientIdentity, StringComparison.Ordinal) ||
                !manifest.TryGetProperty("clientIdentity", out var client) ||
                client.ValueKind != JsonValueKind.String ||
                !string.Equals(client.GetString(), _policy.RecipientIdentity, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Notice recipient and manifest client identity must match the configured Foundry receiver.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("CiAi artifact-ready notice must be valid JSON.", nameof(exactRequestBody), exception);
        }

        if (!allowedRepositories.Contains(repository))
            throw new UnauthorizedAccessException("Authenticated CiAi sender is not authorized for the notice repository.");

        var notice = CiAiArtifactReadyNotice.FromUtf8Bytes(
            sender.Subject, noticeId, idempotencyKey, exactRequestBody);
        var accepted = _receipts.Accept(notice, now);
        return new CiAiAuthenticatedIngressResult(accepted.Outcome, accepted.Receipt);
    }

    private CiAiIngressSender Authenticate(string authorizationHeader, DateTimeOffset now,
        out IReadOnlySet<string> allowedRepositories)
    {
        const string prefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("A CiAi ingress bearer token is required.");
        var token = authorizationHeader[prefix.Length..].Trim();
        if (token.Length is 0 or > MaximumTokenCharacters)
            throw new UnauthorizedAccessException("CiAi ingress bearer token length is invalid.");
        var segments = token.Split('.');
        if (segments.Length != 3)
            throw new UnauthorizedAccessException("CiAi ingress token is not a compact JWT.");

        try
        {
            using var header = JsonDocument.Parse(Decode(segments[0]));
            var root = header.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryString(root, "alg", out var algorithm) || algorithm != "RS256" ||
                !TryString(root, "kid", out var keyId))
                throw new UnauthorizedAccessException("Only registered RS256 ingress JWT keys are accepted.");
            if (!_sendersByKey.TryGetValue(keyId, out var sender))
                throw new UnauthorizedAccessException("CiAi ingress JWT key ID is not registered.");

            var signed = Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]);
            var signature = Decode(segments[2]);
            if (!sender.PublicKey.VerifyData(signed, signature, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1))
                throw new UnauthorizedAccessException("CiAi ingress JWT signature is invalid.");

            using var payload = JsonDocument.Parse(Decode(segments[1]));
            var claims = payload.RootElement;
            if (!TryString(claims, "iss", out var issuer) || issuer != _policy.Issuer ||
                !HasAudience(claims, _policy.Audience) ||
                !TryString(claims, "sub", out var subject) || subject != sender.Subject ||
                !TryString(claims, "jti", out var tokenId) || string.IsNullOrWhiteSpace(tokenId) ||
                tokenId.Length > 128 || tokenId.Any(char.IsControl) ||
                !TryNumber(claims, "iat", out var issuedAt) ||
                !TryNumber(claims, "nbf", out var notBefore) ||
                !TryNumber(claims, "exp", out var expiresAt) ||
                !TryString(claims, "scope", out var scope) ||
                !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(_policy.RequiredScope, StringComparer.Ordinal))
                throw new UnauthorizedAccessException("CiAi ingress JWT required claims are missing or invalid.");

            var issued = DateTimeOffset.FromUnixTimeSeconds(issuedAt);
            var active = DateTimeOffset.FromUnixTimeSeconds(notBefore);
            var expiry = DateTimeOffset.FromUnixTimeSeconds(expiresAt);
            if (issued > now + _policy.ClockSkew || active > now + _policy.ClockSkew ||
                expiry <= now - _policy.ClockSkew || expiry <= issued ||
                expiry - issued > _policy.MaximumLifetime || active > expiry)
                throw new UnauthorizedAccessException("CiAi ingress JWT is not currently valid or exceeds its maximum lifetime.");

            if (!claims.TryGetProperty("repositories", out var repositories) ||
                repositories.ValueKind != JsonValueKind.Array)
                throw new UnauthorizedAccessException("CiAi ingress JWT must carry an explicit repository allowlist.");
            var tokenRepositories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in repositories.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) ||
                    !tokenRepositories.Add(item.GetString()!))
                    throw new UnauthorizedAccessException("CiAi ingress JWT repository allowlist is malformed.");
            }
            if (tokenRepositories.Count == 0 || tokenRepositories.Any(repository => !sender.Repositories.Contains(repository)))
                throw new UnauthorizedAccessException("CiAi ingress JWT exceeds the registered sender repository allowlist.");
            allowedRepositories = tokenRepositories;
            return sender;
        }
        catch (FormatException exception)
        {
            throw new UnauthorizedAccessException("CiAi ingress JWT encoding is invalid.", exception);
        }
        catch (JsonException exception)
        {
            throw new UnauthorizedAccessException("CiAi ingress JWT JSON is invalid.", exception);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new UnauthorizedAccessException("CiAi ingress JWT timestamp is outside the supported range.", exception);
        }
    }

    private static bool TryString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 && !value.Any(char.IsControl);
    }

    private static bool TryNumber(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt64(out value);
    }

    private static bool HasAudience(JsonElement claims, string expected)
    {
        if (!claims.TryGetProperty("aud", out var audience)) return false;
        if (audience.ValueKind == JsonValueKind.String)
            return string.Equals(audience.GetString(), expected, StringComparison.Ordinal);
        return audience.ValueKind == JsonValueKind.Array &&
            audience.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String &&
                string.Equals(item.GetString(), expected, StringComparison.Ordinal));
    }

    private static byte[] Decode(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('=') ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new FormatException("JWT segment is not canonical base64url.");
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return Convert.FromBase64String(base64);
    }
}
