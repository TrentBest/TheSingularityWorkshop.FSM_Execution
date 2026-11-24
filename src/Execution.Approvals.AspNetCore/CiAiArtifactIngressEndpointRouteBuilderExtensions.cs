using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TheSingularityWorkshop.Execution;

namespace TheSingularityWorkshop.Execution.Approvals;

/// <summary>
/// Bounded HTTP adapter for authenticated CiAi artifact-ready notices. Authentication,
/// schema/provenance validation, and durable receipt persistence remain in the host-neutral
/// ingress coordinator; the route preserves the exact received body bytes.
/// </summary>
public static class CiAiArtifactIngressEndpointRouteBuilderExtensions
{
    public const string DefaultRoute = "/api/v1/integrations/ciai/artifact-ready";
    private const int MaximumBodyBytes = 64 * 1024;

    public static void MapCiAiArtifactIngress(this IEndpointRouteBuilder endpoints,
        string pattern = DefaultRoute)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        endpoints.MapPost(pattern, async (HttpContext context,
            CiAiArtifactNoticeIngress ingress, CancellationToken cancellationToken) =>
        {
            if (context.Request.ContentLength is > MaximumBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            byte[] body;
            try
            {
                body = await ReadBoundedBodyAsync(context.Request.Body, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            try
            {
                var result = await ingress.AcceptAsync(body, DateTimeOffset.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Outcome == CiAiArtifactNoticeReceiptOutcome.Conflict)
                    return Results.Conflict(new { error = "notice_identity_conflict" });

                var response = new
                {
                    result.Receipt.ReceiptId,
                    result.Receipt.NoticeId,
                    result.Receipt.IdempotencyKey,
                    result.Receipt.PayloadSha256,
                    result.Receipt.AcceptedAt,
                    outcome = result.Outcome.ToString(),
                    artifactIntegrityVerified = false
                };
                return result.Outcome == CiAiArtifactNoticeReceiptOutcome.Duplicate
                    ? Results.Ok(response)
                    : Results.Accepted(value: response);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "dispatch_mapping_not_found" });
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException)
            {
                return Results.BadRequest(new { error = "invalid_artifact_ready_notice" });
            }
        }).WithName("ExecutionAcceptCiAiArtifactReadyNotice").WithTags("Execution", "ADP");
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await body.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > MaximumBodyBytes)
                throw new InvalidDataException("CiAi notice body exceeds the 64 KiB ingress limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
