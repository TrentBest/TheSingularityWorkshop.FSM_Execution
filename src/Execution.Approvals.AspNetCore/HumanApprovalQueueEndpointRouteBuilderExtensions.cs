using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TheSingularityWorkshop.Foundry;

namespace TheSingularityWorkshop.Foundry.Approvals;

/// <summary>
/// Host-supplied authorization boundary. Implementations must authenticate the caller and
/// authorize access to the approval queue; this adapter intentionally has no permissive default.
/// </summary>
public interface IHumanApprovalQueueAuthorizer
{
    ValueTask<bool> CanReadAsync(HttpContext context, CancellationToken cancellationToken);
    ValueTask<bool> CanDecideAsync(HttpContext context, string approvalId, CancellationToken cancellationToken);
    ValueTask<string?> GetActorAsync(HttpContext context, CancellationToken cancellationToken);
}

public sealed record HumanApprovalDecisionRequest(string Decision, string Reason);

/// <summary>
/// Maps authenticated, GUI-consumable HTTP endpoints over the durable approval journal.
/// The host must provide an authorization implementation before mapping these routes.
/// </summary>
public static class HumanApprovalQueueEndpointRouteBuilderExtensions
{
    public const string DefaultRoute = "/api/v1/adp/approvals";

    public static void MapHumanApprovalQueue(this IEndpointRouteBuilder endpoints,
        JsonlHumanApprovalRequestStore store, IHumanApprovalQueueAuthorizer authorizer,
        string pattern = DefaultRoute)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        endpoints.MapGet(pattern, async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await authorizer.CanReadAsync(context, cancellationToken).ConfigureAwait(false))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var repository = context.Request.Query["repository"].FirstOrDefault();
            if (repository is { Length: > 256 })
                return Results.BadRequest(new { error = "repository_filter_too_long" });

            var now = DateTimeOffset.UtcNow;
            var items = store.GetPending(repository).Select(item => new
            {
                item.SchemaVersion,
                item.ApprovalId,
                item.TaskId,
                item.CorrelationId,
                item.Repository,
                item.CandidateSha,
                item.RequestedBy,
                item.Operation,
                item.Summary,
                item.Rationale,
                item.ArgumentsJson,
                item.ArgumentsSha256,
                item.CreatedAt,
                item.ExpiresAt,
                isExpired = item.ExpiresAt <= now,
                item.Sequence,
                status = item.Status.ToString()
            }).ToArray();

            return Results.Json(new { schemaVersion = 1, observedAt = now, count = items.Length, items });
        }).WithName("ExecutionListPendingHumanApprovals").WithTags("Execution", "ADP");

        endpoints.MapPost(pattern + "/{approvalId}/decision",
            async (HttpContext context, string approvalId, HumanApprovalDecisionRequest request,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(approvalId) || approvalId.Length > 128)
                    return Results.BadRequest(new { error = "invalid_approval_id" });
                if (!await authorizer.CanDecideAsync(context, approvalId, cancellationToken).ConfigureAwait(false))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                var actor = await authorizer.GetActorAsync(context, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(actor))
                    return Results.StatusCode(StatusCodes.Status401Unauthorized);
                if (request is null || string.IsNullOrWhiteSpace(request.Reason) ||
                    request.Reason.Length > 2048 || request.Reason.Any(char.IsControl))
                    return Results.BadRequest(new { error = "decision_reason_required" });

                var decision = request.Decision switch
                {
                    "approve" => HumanApprovalStatus.Approved,
                    "reject" => HumanApprovalStatus.Rejected,
                    _ => (HumanApprovalStatus?)null
                };
                if (!decision.HasValue)
                    return Results.BadRequest(new { error = "decision_must_be_approve_or_reject" });

                try
                {
                    var pending = store.Get(approvalId);
                    if (pending.ExpiresAt <= DateTimeOffset.UtcNow)
                        return Results.StatusCode(StatusCodes.Status410Gone);
                    var decided = store.Decide(approvalId, decision.Value, actor,
                        request.Reason, DateTimeOffset.UtcNow);
                    return Results.Json(new
                    {
                        decided.ApprovalId,
                        status = decided.Status.ToString(),
                        decided.Sequence,
                        decided.DecisionBy,
                        decided.DecisionReason,
                        decided.DecidedAt
                    });
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound(new { error = "approval_not_found" });
                }
                catch (InvalidOperationException)
                {
                    return Results.Conflict(new { error = "approval_already_decided" });
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new { error = "invalid_decision" });
                }
            }).WithName("ExecutionDecideHumanApproval").WithTags("Execution", "ADP");
    }
}
