using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace TheSingularityWorkshop.FSM_Execution.GitHubHost;

/// <summary>
/// ASP.NET Core route adapter for authenticated GitHub work-item deliveries.
/// The hosting application must configure trusted forwarded headers, TLS, and rate limits
/// before mapping this route.
/// </summary>
public static class GitHubWebhookEndpoint
{
    public const string Route = "/api/fsm-execution/github/webhooks/v1";

    public static IEndpointRouteBuilder MapGitHubWebhookIngress(
        this IEndpointRouteBuilder endpoints,
        GitHubWebhookIngress ingress)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(ingress);
        endpoints.MapPost(Route, context => HandleAsync(context, ingress)).AllowAnonymous();
        return endpoints;
    }

    /// <summary>Request delegate separated for deterministic host-level tests.</summary>
    public static async Task HandleAsync(HttpContext context, GitHubWebhookIngress ingress)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ingress);

        if (!context.Request.IsHttps)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        if (context.Request.ContentLength is > GitHubWebhookSignatureVerifier.MaximumBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        if (!TrySingleHeader(context.Request.Headers, "X-Hub-Signature-256", out var signature) ||
            !TrySingleHeader(context.Request.Headers, "X-GitHub-Delivery", out var deliveryId) ||
            !TrySingleHeader(context.Request.Headers, "X-GitHub-Event", out var eventName))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        try
        {
            var body = await ReadBoundedBodyAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
            var receipt = ingress.Accept(body, signature, deliveryId, eventName);
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsJsonAsync(new
            {
                deliveryId = receipt.DeliveryId,
                sequence = receipt.Sequence,
                disposition = receipt.Disposition.ToString()
            }, context.RequestAborted).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            // A configured endpoint may receive an event/action outside this work-item contract.
            // Acknowledge it without admitting work so GitHub does not retry it indefinitely.
            context.Response.StatusCode = StatusCodes.Status202Accepted;
        }
        catch (UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
        catch (PayloadTooLargeException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        catch (ArgumentException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
        catch (InvalidDataException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
        }
        catch (IOException)
        {
            // Durable inbox failure must not be acknowledged as accepted.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }

    private static bool TrySingleHeader(
        IHeaderDictionary headers,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!headers.TryGetValue(name, out var values) || values.Count != 1)
            return false;
        var candidate = values[0];
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 512 ||
            candidate.Any(char.IsControl))
            return false;
        value = candidate;
        return true;
    }

    private sealed class PayloadTooLargeException : Exception { }

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > GitHubWebhookSignatureVerifier.MaximumBodyBytes)
                throw new PayloadTooLargeException();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (buffer.Length == 0)
            throw new ArgumentException("GitHub webhook request body is empty.");
        return buffer.ToArray();
    }
}
