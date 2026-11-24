using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.Foundry.GitHubHost;

/// <summary>
/// Maps the authenticated CiAi artifact-ready receiver onto an ASP.NET Core host.
/// The host remains responsible for trusted TLS termination, proxy configuration,
/// network rate limiting, durable journal paths, and credential/key provisioning.
/// </summary>
public static class CiAiArtifactReadyEndpointRouteBuilderExtensions
{
    public const string DefaultRoute = "/api/v1/integrations/ciai/artifact-ready";
    private const int MaximumBodyBytes = 64 * 1024;

    /// <summary>
    /// Maps a bounded HTTPS-only POST endpoint. Register one configured
    /// <see cref="CiAiArtifactReadyIngress"/> in dependency injection before mapping.
    /// </summary>
    public static RouteHandlerBuilder MapCiAiArtifactReadyIngress(
        this IEndpointRouteBuilder endpoints,
        string pattern = DefaultRoute)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return endpoints.MapPost(pattern, HandleAsync)
            .WithName("ExecutionCiAiArtifactReadyIngress")
            .WithTags("Execution", "CiAi");
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        CiAiArtifactReadyIngress ingress,
        CancellationToken cancellationToken)
    {
        if (!context.Request.IsHttps)
            return Results.Problem(
                title: "HTTPS is required.",
                detail: "The CiAi artifact-ready ingress accepts requests only over HTTPS.",
                statusCode: StatusCodes.Status400BadRequest);

        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0].Trim(),
            "application/json", StringComparison.OrdinalIgnoreCase))
            return Results.Problem(
                title: "Unsupported content type.",
                detail: "The CiAi artifact-ready ingress requires application/json.",
                statusCode: StatusCodes.Status415UnsupportedMediaType);

        if (context.Request.Headers.Authorization.Count != 1)
            return Results.Problem(
                title: "Bearer authentication is required.",
                detail: "Exactly one Authorization header must be supplied.",
                statusCode: StatusCodes.Status401Unauthorized);

        if (context.Request.ContentLength is > MaximumBodyBytes)
            return Results.Problem(
                title: "Request body is too large.",
                detail: "The maximum CiAi artifact-ready notice body is 64 KiB.",
                statusCode: StatusCodes.Status413PayloadTooLarge);

        byte[] body;
        try
        {
            body = await ReadBoundedBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (CiAiIngressBodyTooLargeException)
        {
            return Results.Problem(
                title: "Request body is too large.",
                detail: "The maximum CiAi artifact-ready notice body is 64 KiB.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        try
        {
            var accepted = ingress.Accept(context.Request.Headers.Authorization.ToString(), body);
            var response = new
            {
                contract = "execution.ciai-artifact-ingress.v1",
                outcome = accepted.Outcome.ToString(),
                receiptId = accepted.Receipt.ReceiptId,
                payloadSha256 = accepted.Receipt.PayloadSha256,
                acceptedAt = accepted.Receipt.AcceptedAt
            };

            return accepted.Outcome switch
            {
                CiAiArtifactNoticeReceiptOutcome.Accepted =>
                    Results.Json(response, statusCode: StatusCodes.Status202Accepted),
                CiAiArtifactNoticeReceiptOutcome.Duplicate =>
                    Results.Json(response, statusCode: StatusCodes.Status200OK),
                CiAiArtifactNoticeReceiptOutcome.Conflict =>
                    Results.Conflict(new
                    {
                        contract = "execution.ciai-artifact-ingress.v1",
                        code = "receipt_conflict",
                        message = "The notice identity was already used with different content."
                    }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem(
                title: "CiAi ingress authorization failed.",
                detail: "The sender, token, recipient, repository, or persisted dispatch binding was not authorized.",
                statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (ArgumentException)
        {
            return Results.Problem(
                title: "Invalid artifact-ready notice.",
                detail: "The request body does not satisfy the supported CiAi notice contract.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(
                title: "Dispatch mapping is not actionable.",
                detail: "The request is not bound to an active, verified GitHub run.",
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumBodyBytes)
                throw new CiAiIngressBodyTooLargeException();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private sealed class CiAiIngressBodyTooLargeException : Exception { }
}
