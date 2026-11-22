using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>Supplies short-lived, provider-scoped credentials for one immutable artifact request.</summary>
public interface ICiAiArtifactCredentialProvider
{
    ValueTask<AuthenticationHeaderValue> GetAuthorizationAsync(
        string providerRepository, Uri artifactUri, CancellationToken cancellationToken);
}

/// <summary>HTTP transport seam; production hosts should use <see cref="CiAiArtifactHttpTransport"/>.</summary>
public interface ICiAiArtifactHttpTransport
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

/// <summary>
/// Production artifact transport: no redirects, proxies, cookies, or automatic decompression;
/// DNS is resolved and every returned address must be globally routable before a socket connects.
/// </summary>
public sealed class CiAiArtifactHttpTransport : ICiAiArtifactHttpTransport, IDisposable
{
    private readonly HttpClient _client;

    public CiAiArtifactHttpTransport()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 2,
            MaxResponseHeadersLength = 32,
            ConnectCallback = ConnectToPublicAddressAsync
        };
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    public void Dispose() => _client.Dispose();

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsGloballyRoutable(address)))
            throw new HttpRequestException("Artifact host DNS resolved to an empty or non-public address set.");

        SocketException? lastFailure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                lastFailure = exception;
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("Unable to connect to the validated artifact host address set.", lastFailure);
    }

    private static bool IsGloballyRoutable(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            var a = bytes[0];
            var b = bytes[1];
            var c = bytes[2];
            if (a is 0 or 10 or 127 || a >= 224 ||
                (a == 100 && b is >= 64 and <= 127) ||
                (a == 169 && b == 254) ||
                (a == 172 && b is >= 16 and <= 31) ||
                (a == 192 && (b == 0 || b == 168)) ||
                (a == 192 && b == 88 && c == 99) ||
                (a == 198 && (b is 18 or 19 || (b == 51 && c == 100))) ||
                (a == 203 && b == 0 && c == 113))
                return false;
            return true;
        }

        if (bytes.Length != 16 || (bytes[0] & 0xE0) != 0x20)
            return false;

        // Reject IETF special-purpose, documentation, Teredo, and 6to4 ranges.
        if (bytes[0] == 0x20 && bytes[1] == 0x01 &&
            (bytes[2] == 0x00 || (bytes[2] == 0x0D && bytes[3] == 0xB8)))
            return false;
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return false;
        return true;
    }
}

/// <summary>Verified immutable artifact bytes. The payload accessor returns a defensive copy.</summary>
public sealed class CiAiVerifiedArtifact
{
    private readonly byte[] _payload;

    internal CiAiVerifiedArtifact(ArtifactManifest manifest, string receiptId, byte[] payload, string payloadSha256)
    {
        Manifest = manifest;
        ReceiptId = receiptId;
        _payload = payload.ToArray();
        PayloadSha256 = payloadSha256;
    }

    public ArtifactManifest Manifest { get; }
    public string ReceiptId { get; }
    public string PayloadSha256 { get; }
    public ReadOnlyMemory<byte> Payload => new(_payload.ToArray());
}

/// <summary>
/// Pulls a notice's immutable artifact only after re-binding it to the accepted receipt,
/// authenticated provider registration, and exact persisted GitHub dispatch mapping.
/// This verifies bytes and manifest identity; packet schema/provenance validation is a
/// separate, later gate.
/// </summary>
public sealed class CiAiArtifactRetrievalService
{
    public const long MaximumArtifactBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ArtifactRetrievalDestinationPolicy _destinationPolicy;
    private readonly ICiAiArtifactCredentialProvider _credentials;
    private readonly ICiAiArtifactHttpTransport _transport;
    private readonly string _receiverIdentity;
    private readonly Func<DateTimeOffset> _clock;

    public CiAiArtifactRetrievalService(
        ArtifactRetrievalDestinationPolicy destinationPolicy,
        ICiAiArtifactCredentialProvider credentials,
        ICiAiArtifactHttpTransport transport,
        string receiverIdentity,
        Func<DateTimeOffset>? clock = null)
    {
        _destinationPolicy = destinationPolicy ?? throw new ArgumentNullException(nameof(destinationPolicy));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (string.IsNullOrWhiteSpace(receiverIdentity) || receiverIdentity.Length > 200 ||
            receiverIdentity.Any(char.IsControl))
            throw new ArgumentException("A bounded receiver identity is required.", nameof(receiverIdentity));
        _receiverIdentity = receiverIdentity;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<CiAiVerifiedArtifact> RetrieveAsync(
        CiAiArtifactNoticeReceipt receipt,
        CiAiRequestTaskMappingSnapshot mappingSnapshot,
        CiAiIngressSender registeredSender,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(mappingSnapshot);
        ArgumentNullException.ThrowIfNull(registeredSender);
        registeredSender.Validate();

        var now = _clock();
        if (now == default) throw new InvalidOperationException("Artifact retrieval clock returned an invalid timestamp.");
        if (mappingSnapshot.Cancelled || mappingSnapshot.RunBinding is null ||
            now < mappingSnapshot.Mapping.CreatedAt || now > mappingSnapshot.Mapping.ExpiresAt)
            throw new InvalidOperationException("Artifact retrieval requires an active request bound to one exact GitHub run and attempt.");

        var mapping = mappingSnapshot.Mapping.Validate();
        mappingSnapshot.RunBinding.ValidateAgainst(mapping);
        if (!string.Equals(receipt.SenderIdentity, registeredSender.Subject, StringComparison.Ordinal) ||
            !string.Equals(mapping.ProviderIdentity, registeredSender.Subject, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Artifact receipt, sender registration, and persisted provider identity do not agree.");

        var receiptDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(receipt.PayloadUtf8))).ToLowerInvariant();
        if (!string.Equals(receiptDigest, receipt.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The durable notice receipt payload digest does not match its exact stored bytes.");

        ArtifactReadyNotice notice;
        try
        {
            notice = JsonSerializer.Deserialize<ArtifactReadyNotice>(receipt.PayloadUtf8, JsonOptions)
                ?? throw new InvalidDataException("The accepted artifact-ready notice could not be deserialized.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The accepted artifact-ready notice is not a supported typed v1 notice.", exception);
        }

        var manifest = notice.Manifest;
        if (notice.ContractVersion != ArtifactReadyNotice.SupportedContractVersion ||
            notice.NoticeId != receipt.NoticeId ||
            notice.RecipientIdentity != _receiverIdentity ||
            manifest.ClientIdentity != _receiverIdentity ||
            manifest.RequestId != mapping.RequestId ||
            manifest.OperationId != mapping.OperationId ||
            manifest.IdempotencyKey != mapping.IdempotencyKey ||
            !string.Equals(manifest.SourceSha, mapping.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            !registeredSender.Repositories.Contains(manifest.ProviderRepository) ||
            manifest.MediaType != "application/json" ||
            manifest.SizeBytes <= 0 || manifest.SizeBytes > MaximumArtifactBytes ||
            manifest.ExpiresAt <= now || manifest.CreatedAt > receipt.AcceptedAt)
            throw new UnauthorizedAccessException("Artifact manifest does not match the accepted receipt, sender allowlist, receiver, or exact dispatch mapping.");

        var uri = manifest.RetrievalUri;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort ||
            !Uri.TryCreate(mapping.ArtifactOrigin, UriKind.Absolute, out var configuredOrigin) ||
            !string.Equals(uri.GetLeftPart(UriPartial.Authority),
                configuredOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Artifact retrieval URI does not match the credential-free origin captured before dispatch.");

        var destinationErrors = _destinationPolicy.Validate(uri, now, manifest.ExpiresAt);
        if (destinationErrors.Count != 0)
            throw new UnauthorizedAccessException("Artifact retrieval destination rejected by host policy: " +
                string.Join(",", destinationErrors));

        var authorization = await _credentials.GetAuthorizationAsync(
            manifest.ProviderRepository, uri, cancellationToken).ConfigureAwait(false);
        if (authorization is null || string.IsNullOrWhiteSpace(authorization.Scheme) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            throw new UnauthorizedAccessException("A provider-scoped artifact read credential is required.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = authorization;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _transport.SendAsync(request, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            (response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri))
            throw new InvalidDataException("Artifact provider must return HTTP 200 from the exact configured URI without redirects.");
        if (response.Content.Headers.ContentType?.MediaType is not { } mediaType ||
            !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artifact provider returned an unsupported media type.");
        if (response.Content.Headers.ContentLength is > MaximumArtifactBytes)
            throw new InvalidDataException("Artifact response exceeds the 16 MiB host limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        var read = await ArtifactPayloadReader.ReadAndVerifyAsync(
            stream, manifest, MaximumArtifactBytes, deadline.Token).ConfigureAwait(false);
        if (!read.Verification.IsVerified || read.Payload is null)
            throw new InvalidDataException("Artifact bytes failed exact manifest verification: " +
                (read.Verification.FailureCode ?? "payload.unknown"));
        return new CiAiVerifiedArtifact(manifest, receipt.ReceiptId,
            read.Payload.Value.ToArray(), read.Verification.ActualDigest
                ?? throw new InvalidDataException("Verified artifact digest was unexpectedly absent."));
    }
}
