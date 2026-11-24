using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using TheSingularityWorkshop.CiAi;

namespace TheSingularityWorkshop.FSM_Execution.GitHubHost;

/// <summary>Host policy for the bounded repository paths and validation budget of a work item.</summary>
public sealed record GitHubWorkItemTaskScope(IReadOnlyList<string> AllowedPaths, int MaximumAttempts);

public interface IGitHubWorkItemTaskScopePolicy
{
    GitHubWorkItemTaskScope Resolve(string repository, string itemKind);
}

/// <summary>
/// GitHub REST implementation of the exact-revision snapshot resolver. Authentication
/// is supplied by the host-configured HttpClient handler; this class never reads or
/// stores a token. The API item updated_at must still match the authenticated event.
/// </summary>
public sealed class GitHubRestWorkItemTaskSnapshotResolver : IGitHubWorkItemTaskSnapshotResolver
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly IGitHubWorkItemTaskScopePolicy _scopePolicy;

    public GitHubRestWorkItemTaskSnapshotResolver(
        HttpClient httpClient,
        IGitHubWorkItemTaskScopePolicy scopePolicy)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _scopePolicy = scopePolicy ?? throw new ArgumentNullException(nameof(scopePolicy));
        if (_httpClient.BaseAddress is not { Scheme: "https", Host: "api.github.com", IsDefaultPort: true })
            throw new ArgumentException("GitHub REST HttpClient must use the HTTPS api.github.com base address.", nameof(httpClient));
    }

    public async ValueTask<GitHubWorkItemTaskSnapshot> ResolveAsync(
        GitHubWorkItemEvent workItemEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItemEvent);
        var parts = workItemEvent.Repository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("GitHub event repository identity is not canonical.");
        var repositoryPath = $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}";

        using var repository = await GetJsonAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var repositoryId = RequiredInt64(repository.RootElement, "id");
        var fullName = RequiredString(repository.RootElement, "full_name");
        var defaultBranch = RequiredString(repository.RootElement, "default_branch");
        if (repositoryId != workItemEvent.RepositoryId ||
            !string.Equals(fullName, workItemEvent.Repository, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("GitHub REST repository identity does not match the authenticated webhook enrollment.");
        if (defaultBranch.Length > 255 || defaultBranch.Any(char.IsControl))
            throw new InvalidDataException("GitHub repository default branch is invalid.");

        string itemUrl;
        DateTimeOffset updatedAt;
        string itemNodeId;
        int number;
        string snapshotSha;

        if (workItemEvent.ItemKind == "issue")
        {
            using var issue = await GetJsonAsync(
                $"{repositoryPath}/issues/{workItemEvent.Number}", cancellationToken).ConfigureAwait(false);
            VerifyItemIdentity(issue.RootElement, workItemEvent);
            updatedAt = RequiredTimestamp(issue.RootElement, "updated_at");
            itemNodeId = RequiredString(issue.RootElement, "node_id");
            number = checked((int)RequiredInt64(issue.RootElement, "number"));
            itemUrl = RequiredString(issue.RootElement, "html_url");

            if (updatedAt != workItemEvent.UpdatedAt)
                throw new InvalidOperationException("GitHub Issue has changed since this webhook revision; refusing to snapshot newer content.");

            using var commit = await GetJsonAsync(
                $"{repositoryPath}/commits/{Uri.EscapeDataString(defaultBranch)}", cancellationToken).ConfigureAwait(false);
            snapshotSha = RequiredString(commit.RootElement, "sha");
            RequireSha(snapshotSha, "default-branch commit");
        }
        else if (workItemEvent.ItemKind == "pull_request")
        {
            using var pull = await GetJsonAsync(
                $"{repositoryPath}/pulls/{workItemEvent.Number}", cancellationToken).ConfigureAwait(false);
            VerifyItemIdentity(pull.RootElement, workItemEvent);
            updatedAt = RequiredTimestamp(pull.RootElement, "updated_at");
            itemNodeId = RequiredString(pull.RootElement, "node_id");
            number = checked((int)RequiredInt64(pull.RootElement, "number"));
            itemUrl = RequiredString(pull.RootElement, "html_url");

            var head = RequiredObject(pull.RootElement, "head");
            var baseElement = RequiredObject(pull.RootElement, "base");
            var headSha = RequiredString(head, "sha");
            var headRef = RequiredString(head, "ref");
            var baseSha = RequiredString(baseElement, "sha");
            var baseRef = RequiredString(baseElement, "ref");
            var baseRepository = RequiredObject(baseElement, "repo");
            var baseRepositoryId = RequiredInt64(baseRepository, "id");
            var baseRepositoryName = RequiredString(baseRepository, "full_name");

            RequireSha(headSha, "pull-request head");
            RequireSha(baseSha, "pull-request base");
            if (updatedAt != workItemEvent.UpdatedAt ||
                !string.Equals(headSha, workItemEvent.HeadSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(baseSha, workItemEvent.BaseSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(headRef, workItemEvent.HeadRef, StringComparison.Ordinal) ||
                !string.Equals(baseRef, workItemEvent.BaseRef, StringComparison.Ordinal) ||
                baseRepositoryId != workItemEvent.RepositoryId ||
                !string.Equals(baseRepositoryName, workItemEvent.Repository, StringComparison.Ordinal))
                throw new InvalidOperationException("GitHub Pull Request no longer matches the exact authenticated event revision.");
            snapshotSha = headSha;
        }
        else
            throw new NotSupportedException($"GitHub item kind '{workItemEvent.ItemKind}' is not enabled for task admission.");

        if (!string.Equals(itemNodeId, workItemEvent.ItemNodeId, StringComparison.Ordinal) ||
            number != workItemEvent.Number ||
            !string.Equals(itemUrl, workItemEvent.HtmlUrl.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidDataException("GitHub REST item identity does not match the authenticated webhook.");

        var scope = _scopePolicy.Resolve(workItemEvent.Repository, workItemEvent.ItemKind)
            ?? throw new UnauthorizedAccessException("No host-owned task scope is configured for this repository and item kind.");
        return new GitHubWorkItemTaskSnapshot(
            workItemEvent.Repository,
            workItemEvent.ItemKind,
            itemNodeId,
            number,
            itemUrl,
            updatedAt,
            snapshotSha,
            scope.AllowedPaths,
            scope.MaximumAttempts);
    }

    private async ValueTask<JsonDocument> GetJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("TheSingularityWorkshop-FSM-Execution");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub REST request '{relativePath}' returned {(int)response.StatusCode} ({response.StatusCode}).",
                null, response.StatusCode);
        if (response.Content.Headers.ContentLength is long contentLength &&
            (contentLength <= 0 || contentLength > MaximumResponseBytes))
            throw new InvalidDataException("GitHub REST response is empty or exceeds the configured 4 MiB bound.");
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await responseStream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("GitHub REST response exceeds the configured 4 MiB bound.");
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0)
            throw new InvalidDataException("GitHub REST response is empty or exceeds the configured 4 MiB bound.");
        try
        {
            return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("GitHub REST returned malformed JSON.", exception);
        }
    }

    private static void VerifyItemIdentity(JsonElement item, GitHubWorkItemEvent source)
    {
        if (RequiredInt64(item, "id") != source.ItemId ||
            !string.Equals(RequiredString(item, "node_id"), source.ItemNodeId, StringComparison.Ordinal) ||
            RequiredInt64(item, "number") != source.Number)
            throw new InvalidDataException("GitHub REST item identity does not match the authenticated webhook.");
    }

    private static JsonElement RequiredObject(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"GitHub REST response is missing object '{name}'.");
        return value;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"GitHub REST response is missing string '{name}'.");
        return value.GetString()!;
    }

    private static long RequiredInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt64(out var result) || result <= 0)
            throw new InvalidDataException($"GitHub REST response is missing positive integer '{name}'.");
        return result;
    }

    private static DateTimeOffset RequiredTimestamp(JsonElement element, string name)
    {
        var raw = RequiredString(element, name);
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            throw new InvalidDataException($"GitHub REST response has invalid timestamp '{name}'.");
        return value;
    }

    private static void RequireSha(string value, string description)
    {
        if (value.Length != 40 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException($"GitHub REST {description} SHA is not a full 40-character hexadecimal commit.");
    }
}
