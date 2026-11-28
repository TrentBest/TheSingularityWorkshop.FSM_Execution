using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Execution;

namespace TheSingularityWorkshop.Execution.GitHubHost;

/// <summary>Provides a short-lived GitHub token for one repository.</summary>
public interface IGitHubAccessTokenProvider
{
    ValueTask<string> GetTokenAsync(string repository, CancellationToken cancellationToken);
}

/// <summary>
/// Reads only root AgentOnboarding.md at the supplied immutable commit SHA and delegates
/// contract interpretation to CiAi's pinned AgentOnboardingValidator.
/// This verifier intentionally does not require the SHA to be the current development head:
/// callers must bind the SHA to their own task/candidate policy and this adapter verifies
/// the exact commit supplied.
/// </summary>
public sealed class GitHubAgentOnboardingAdmissionVerifier : IAgentOnboardingAdmissionVerifier
{
    private const int MaximumResponseBytes = 128 * 1024;
    private readonly HttpClient _client;
    private readonly IGitHubAccessTokenProvider _tokens;

    public GitHubAgentOnboardingAdmissionVerifier(HttpClient client, IGitHubAccessTokenProvider tokens)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        if (_client.BaseAddress is null ||
            _client.BaseAddress.Scheme != Uri.UriSchemeHttps ||
            _client.BaseAddress.Host != "api.github.com" ||
            _client.BaseAddress.Port != 443)
            throw new ArgumentException("The GitHub API client must use https://api.github.com:443.", nameof(client));
        if (_client.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("The GitHub API client must not carry ambient Authorization headers.", nameof(client));
    }

    public async ValueTask<AgentOnboardingAdmissionEvidence> VerifyAsync(
        string repository, string targetSha, CancellationToken cancellationToken = default)
    {
        ValidateRepository(repository);
        if (targetSha is not { Length: 40 } || !targetSha.All(Uri.IsHexDigit))
            throw new ArgumentException("A full 40-character hexadecimal commit SHA is required.", nameof(targetSha));

        var token = await _tokens.GetTokenAsync(repository, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(char.IsControl))
            throw new UnauthorizedAccessException("The host token provider did not return a valid GitHub token.");

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"repos/{repository}/contents/{AgentOnboardingAdmissionEvidence.RequiredPath}?ref={Uri.EscapeDataString(targetSha)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("TheSingularityWorkshop-Execution/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return Map(AgentOnboardingValidator.Evaluate(new AgentOnboardingSourceObservation(
                repository, targetSha, targetSha, AgentOnboardingValidator.RequiredPath,
                AgentOnboardingReadStatus.Missing, null)));

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub onboarding read failed with HTTP {(int)response.StatusCode}.",
                null, response.StatusCode);

        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("GitHub onboarding response exceeds the host response limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (bounded.Length + count > MaximumResponseBytes)
                throw new InvalidDataException("GitHub onboarding response exceeds the host response limit.");
            await bounded.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        using var json = JsonDocument.Parse(bounded.ToArray());
        var root = json.RootElement;
        var path = GetString(root, "path");
        var type = GetString(root, "type");
        var encoding = GetString(root, "encoding");
        if (path != AgentOnboardingValidator.RequiredPath || type != "file" ||
            !string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt32(out var size) || size < 0)
            return Map(AgentOnboardingValidator.Evaluate(new AgentOnboardingSourceObservation(
                repository, targetSha, targetSha, AgentOnboardingValidator.RequiredPath,
                AgentOnboardingReadStatus.Unreadable, null)));

        if (size > AgentOnboardingValidator.MaximumContentBytes)
            return Map(AgentOnboardingValidator.Evaluate(new AgentOnboardingSourceObservation(
                repository, targetSha, targetSha, AgentOnboardingValidator.RequiredPath,
                AgentOnboardingReadStatus.Available,
                new string('x', AgentOnboardingValidator.MaximumContentBytes + 1))));

        var encoded = GetString(root, "content");
        if (encoded is null)
            return Map(AgentOnboardingValidator.Evaluate(new AgentOnboardingSourceObservation(
                repository, targetSha, targetSha, AgentOnboardingValidator.RequiredPath,
                AgentOnboardingReadStatus.Unreadable, null)));

        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException)
        {
            return Map(AgentOnboardingValidator.Evaluate(new AgentOnboardingSourceObservation(
                repository, targetSha, targetSha, AgentOnboardingValidator.RequiredPath,
                AgentOnboardingReadStatus.Unreadable, null)));
        }

        if (bytes.Length != size)
            return Map(AgentOnboardingValidator.Evaluate(new AgentOnboardingSourceObservation(
                repository, targetSha, targetSha, AgentOnboardingValidator.RequiredPath,
                AgentOnboardingReadStatus.Unreadable, null)));

        string content;
        try { content = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { content = "\uD800"; }

        return Map(AgentOnboardingValidator.Evaluate(new AgentOnboardingSourceObservation(
            repository, targetSha, targetSha, AgentOnboardingValidator.RequiredPath,
            AgentOnboardingReadStatus.Available, content)));
    }

    private static AgentOnboardingAdmissionEvidence Map(AgentOnboardingAdmissionResult result) =>
        new(result.Repository, result.RequestedCommitSha, result.Path, result.ContractVersion,
            result.IsEligible, result.Outcome, result.FailureCode, result.Reason, result.ContentSha256);

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static void ValidateRepository(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Length > 256 ||
            repository.Split('/') is not { Length: 2 } parts ||
            parts.Any(part => string.IsNullOrWhiteSpace(part) ||
                part.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
            throw new ArgumentException("Repository must use canonical owner/name identity.", nameof(repository));
    }
}
