using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using TheSingularityWorkshop.CiAi;

namespace TheSingularityWorkshop.Execution;

public interface IGitHubActionsArtifactArchiveDownloader
{
    Task<ReadOnlyMemory<byte>> DownloadAsync(
        CiProviderArtifactObservation artifact,
        string repository,
        long runId,
        string expectedHeadSha,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Downloads one exact GitHub Actions artifact archive with a single, explicitly allowlisted
/// redirect. The GitHub bearer token is sent only to api.github.com and is never forwarded to
/// the signed storage URL. Supply HttpClients created with automatic redirects and proxies disabled;
/// production hosts should use PinnedHttpsArtifactHttpClientFactory for every registered origin.
/// </summary>
public sealed class GitHubActionsArtifactArchiveDownloader : IGitHubActionsArtifactArchiveDownloader
{
    private const string GitHubApiOrigin = "https://api.github.com";
    private const long MaximumArchiveBytes = CiTestResultArtifactArchive.DefaultMaximumArchiveBytes;
    private readonly IReadOnlyDictionary<string, HttpClient> _clientsByOrigin;
    private readonly IGitHubActionsReadCredentialProvider _credentials;

    public GitHubActionsArtifactArchiveDownloader(
        IReadOnlyDictionary<string, HttpClient> clientsByOrigin,
        IGitHubActionsReadCredentialProvider credentials)
    {
        ArgumentNullException.ThrowIfNull(clientsByOrigin);
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));

        var clients = new Dictionary<string, HttpClient>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in clientsByOrigin)
        {
            ArgumentNullException.ThrowIfNull(pair.Value);
            if (!Uri.TryCreate(pair.Key, UriKind.Absolute, out var origin) ||
                !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                origin.Port != 443 ||
                !string.IsNullOrEmpty(origin.UserInfo) ||
                !string.IsNullOrEmpty(origin.Query) ||
                !string.IsNullOrEmpty(origin.Fragment) ||
                origin.AbsolutePath != "/" ||
                System.Net.IPAddress.TryParse(origin.Host, out _))
                throw new ArgumentException("Artifact clients must be keyed by HTTPS DNS origins on port 443.", nameof(clientsByOrigin));

            var key = OriginKey(origin);
            if (!clients.TryAdd(key, pair.Value))
                throw new ArgumentException("Artifact client origin is registered more than once.", nameof(clientsByOrigin));
        }

        if (!clients.ContainsKey(GitHubApiOrigin))
            throw new ArgumentException("A pinned https://api.github.com client is required.", nameof(clientsByOrigin));
        _clientsByOrigin = clients;
    }

    public async Task<ReadOnlyMemory<byte>> DownloadAsync(
        CiProviderArtifactObservation artifact,
        string repository,
        long runId,
        string expectedHeadSha,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!RepositoryIdentity.IsValid(repository))
            throw new ArgumentException("Repository must use canonical owner/name identity.", nameof(repository));
        if (runId <= 0)
            throw new ArgumentOutOfRangeException(nameof(runId));
        if (expectedHeadSha is not { Length: 40 } || !expectedHeadSha.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected provider head SHA must be a full commit SHA.", nameof(expectedHeadSha));
        if (now == default)
            throw new ArgumentException("A current time is required for artifact expiry validation.", nameof(now));

        if (!string.Equals(artifact.Repository, repository, StringComparison.OrdinalIgnoreCase) ||
            artifact.RunId != runId ||
            !string.Equals(artifact.HeadSha, expectedHeadSha, StringComparison.OrdinalIgnoreCase) ||
            artifact.IsExpired || artifact.ExpiresAt <= now ||
            artifact.ArchiveSizeBytes is <= 0 or > MaximumArchiveBytes)
            throw new UnauthorizedAccessException("Provider artifact metadata does not match the exact requested run or is expired/oversized.");

        var expectedDigest = ParseSha256Digest(artifact.ProviderArchiveDigest);
        var downloadUri = artifact.ArchiveDownloadUrl;
        var parts = repository.Split('/');
        var expectedPath =
            $"/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/actions/artifacts/{artifact.ArtifactId}/zip";
        if (!downloadUri.IsAbsoluteUri ||
            !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            downloadUri.Port != 443 ||
            !string.Equals(downloadUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(downloadUri.UserInfo) ||
            !string.IsNullOrEmpty(downloadUri.Query) ||
            !string.IsNullOrEmpty(downloadUri.Fragment) ||
            !string.Equals(downloadUri.AbsolutePath, expectedPath, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Provider archive URL is not the exact GitHub artifact endpoint for the registered repository and artifact ID.");

        var token = await _credentials.GetTokenAsync(repository, cancellationToken).ConfigureAwait(false);
        ValidateToken(token);

        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        apiRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        apiRequest.Headers.UserAgent.ParseAdd("TheSingularityWorkshop-Execution/1.0");
        apiRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null)
            apiRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var apiResponse = await _clientsByOrigin[GitHubApiOrigin].SendAsync(
            apiRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        byte[] archiveBytes;
        if (apiResponse.StatusCode == HttpStatusCode.OK)
        {
            archiveBytes = await ReadBoundedAsync(apiResponse.Content, cancellationToken).ConfigureAwait(false);
        }
        else if (IsRedirect(apiResponse.StatusCode))
        {
            var location = apiResponse.Headers.Location
                ?? throw new InvalidDataException("GitHub artifact download redirect omitted its Location header.");
            var redirectUri = location.IsAbsoluteUri ? location : new Uri(downloadUri, location);
            var redirectOrigin = ValidateRedirectUri(redirectUri);
            if (!_clientsByOrigin.TryGetValue(redirectOrigin, out var redirectClient))
                throw new UnauthorizedAccessException("GitHub artifact redirect origin is not explicitly allowlisted.");

            // Deliberately create a new request without Authorization or cookies.
            using var redirectRequest = new HttpRequestMessage(HttpMethod.Get, redirectUri);
            redirectRequest.Headers.UserAgent.ParseAdd("TheSingularityWorkshop-Execution/1.0");
            redirectRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
            using var redirectResponse = await redirectClient.SendAsync(
                redirectRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (redirectResponse.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException("Allowlisted GitHub artifact storage origin did not return a complete 200 response.");
            archiveBytes = await ReadBoundedAsync(redirectResponse.Content, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new HttpRequestException($"GitHub artifact download returned unexpected HTTP status {(int)apiResponse.StatusCode}.");
        }

        if (archiveBytes.LongLength != artifact.ArchiveSizeBytes)
            throw new InvalidDataException("Downloaded artifact archive size differs from GitHub's provider metadata.");

        var actualDigest = SHA256.HashData(archiveBytes);
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            throw new InvalidDataException("Downloaded artifact archive digest differs from GitHub's provider metadata.");

        return archiveBytes;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > MaximumArchiveBytes)
            throw new InvalidDataException("GitHub artifact archive exceeds the configured maximum size.");

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (destination.Length + read > MaximumArchiveBytes)
                throw new InvalidDataException("GitHub artifact archive exceeds the configured maximum size.");
            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    private static string ValidateRedirectUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            System.Net.IPAddress.TryParse(uri.Host, out _))
            throw new UnauthorizedAccessException("GitHub artifact redirect must target an HTTPS DNS origin on port 443.");

        return OriginKey(uri);
    }

    private static string OriginKey(Uri uri)
        => uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static byte[] ParseSha256Digest(string digest)
    {
        if (digest is not { Length: 71 } ||
            !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
            !digest.AsSpan(7).ToString().All(Uri.IsHexDigit))
            throw new InvalidDataException("GitHub artifact metadata does not contain a canonical SHA-256 digest.");

        return Convert.FromHexString(digest[7..]);
    }

    private static void ValidateToken(string? token)
    {
        if (token is { Length: > 8192 } ||
            token is not null &&
            (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl) || token.Any(char.IsWhiteSpace)))
            throw new UnauthorizedAccessException("GitHub read credential provider returned an invalid token.");
    }
}
