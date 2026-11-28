using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TheSingularityWorkshop.CiAi;

namespace TheSingularityWorkshop.Execution;

/// <summary>
/// Reads one workflow definition from the GitHub Contents API at an exact commit SHA.
/// The supplied HttpClient must have automatic redirects and proxies disabled; production
/// hosts should use PinnedHttpsArtifactHttpClientFactory for the api.github.com origin.
/// </summary>
public interface IGitHubActionsWorkflowDefinitionReader
{
    Task<ReadOnlyMemory<byte>> ReadAsync(
        string repository,
        string workflowPath,
        string commitSha,
        CancellationToken cancellationToken = default);
}

public sealed class GitHubActionsWorkflowDefinitionReader : IGitHubActionsWorkflowDefinitionReader
{
    public const int MaximumWorkflowDefinitionBytes = 256 * 1024;
    private readonly HttpClient _httpClient;
    private readonly IGitHubActionsReadCredentialProvider _credentials;

    public GitHubActionsWorkflowDefinitionReader(
        HttpClient httpClient,
        IGitHubActionsReadCredentialProvider credentials)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public async Task<ReadOnlyMemory<byte>> ReadAsync(
        string repository,
        string workflowPath,
        string commitSha,
        CancellationToken cancellationToken = default)
    {
        if (!IsCanonicalRepository(repository))
            throw new ArgumentException("Repository must use canonical owner/name identity.", nameof(repository));
        if (!IsWorkflowPath(workflowPath))
            throw new ArgumentException("Workflow path must identify a file below .github/workflows.", nameof(workflowPath));
        if (commitSha is not { Length: 40 } || !commitSha.All(Uri.IsHexDigit))
            throw new ArgumentException("Workflow revision must be a full commit SHA.", nameof(commitSha));

        var token = await _credentials.GetTokenAsync(repository, cancellationToken).ConfigureAwait(false);
        if (token is { Length: > 8192 } ||
            token is not null &&
            (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl) || token.Any(char.IsWhiteSpace)))
            throw new UnauthorizedAccessException("GitHub read credential provider returned an invalid token.");

        var encodedPath = string.Join("/", workflowPath.Split('/').Select(Uri.EscapeDataString));
        var ownerAndName = repository.Split('/');
        var uri = new Uri(
            $"https://api.github.com/repos/{Uri.EscapeDataString(ownerAndName[0])}/{Uri.EscapeDataString(ownerAndName[1])}/contents/{encodedPath}?ref={commitSha}");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        request.Headers.UserAgent.ParseAdd("TheSingularityWorkshop-Execution/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"GitHub workflow definition lookup returned HTTP {(int)response.StatusCode}.");

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mediaType, "application/vnd.github+json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub returned a JSON API envelope instead of raw workflow bytes.");

        if (response.Content.Headers.ContentLength is long length &&
            length is <= 0 or > MaximumWorkflowDefinitionBytes)
            throw new InvalidDataException("Workflow definition is empty or exceeds the configured size bound.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (destination.Length + read > MaximumWorkflowDefinitionBytes)
                throw new InvalidDataException("Workflow definition exceeds the configured size bound.");
            destination.Write(buffer, 0, read);
        }

        var bytes = destination.ToArray();
        if (bytes.Length == 0)
            throw new InvalidDataException("GitHub returned an empty workflow definition.");
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Contains('\0'))
                throw new InvalidDataException("Workflow definition contains a NUL character.");
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Workflow definition is not valid UTF-8.", exception);
        }

        return bytes;
    }

    private static bool IsCanonicalRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Length > 201)
            return false;
        var parts = repository.Split('/');
        return parts.Length == 2 && parts.All(part =>
            part.Length is > 0 and <= 100 &&
            part != "." && part != ".." &&
            part.All(character => char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.'));
    }

    private static bool IsWorkflowPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 ||
            path.Any(char.IsControl) || path.Any(char.IsWhiteSpace) ||
            path.Contains('\\') || path.StartsWith('/') || path.Contains('?') || path.Contains('#'))
            return false;

        var segments = path.Split('/');
        return segments.Length >= 3 &&
            segments[0] == ".github" &&
            segments[1] == "workflows" &&
            segments.Skip(2).All(segment =>
                segment.Length > 0 && segment != "." && segment != "..") &&
            (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
    }
}
