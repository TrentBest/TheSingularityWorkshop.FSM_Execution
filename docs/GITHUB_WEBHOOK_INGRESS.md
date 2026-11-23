# GitHub Webhook Ingress Boundary

**Contract:** `fsm-execution.github-webhook-ingress.v1`  
**Status:** exact-byte HMAC authentication, enrolled-repository validation, durable delivery deduplication, revision-aware inbox/coalescing, and an ASP.NET Core route adapter implemented; running-host registration, webhook registration, and task admission remain outstanding.

## Authentication

The receiver must preserve the exact request-body bytes and verify GitHub's `X-Hub-Signature-256` header before parsing JSON or trusting any payload field. `GitHubWebhookSignatureVerifier` computes HMAC-SHA256 over those exact bytes and compares the supplied digest with a fixed-time comparison.

The verifier requires a host-provisioned secret of at least 32 bytes, rejects missing or malformed signatures, rejects empty bodies and bodies larger than 1 MiB, and clears its in-memory secret when disposed. The secret is never part of the webhook body, event contract, task packet, logs, or LLM context.

## Required host sequence

1. Enforce HTTPS, request rate limits, and the 1 MiB streaming body limit at the HTTP boundary.
2. Read the raw body once without JSON reserialization.
3. Verify `X-Hub-Signature-256` using the configured GitHub webhook secret.
4. Validate `X-GitHub-Delivery`, `X-GitHub-Event`, and enrolled repository identity.
5. Only then parse through CiAi's versioned `GitHubWorkItemEvent` contract, pinned to source commit `4b4ed970699cabbd4794ec3db18d6c368a109bae`.
6. Match both the numeric repository ID and canonical owner/name against the host-owned enrollment list.
7. Persist the unique delivery in the durable inbox. Deduplicate identical delivery IDs, reject conflicting delivery-ID reuse, retain stale deliveries as superseded, and expose only the newest unambiguous revision per work item as a pending candidate.
8. A later dispatcher may create or update a bounded FSM_Execution task from that candidate; it must use the existing durable task lifecycle rather than invent a second scheduler.

The GitHub webhook HMAC protocol is separate from the RS256 bearer-token protocol used by the CiAi artifact-ready callback. Neither protocol substitutes for the other.

## HTTP route adapter

`GitHubWebhookEndpoint.MapGitHubWebhookIngress` maps `POST /api/fsm-execution/github/webhooks/v1`. It:
- Requires HTTPS as seen by ASP.NET Core; reverse-proxy deployments must configure trusted forwarded headers before this route.
- Accepts only `application/json`, validates single-value signature/delivery/event headers, and enforces the 1 MiB limit both from Content-Length and while streaming.
- Maps invalid authentication to 401, malformed requests to 400, unsupported event/action to 202 without admission, conflicting replay/revision to 409, and durable inbox I/O failure to 503.
- Returns 200 only after the durable inbox has accepted or idempotently recognized the delivery.
- Uses `AllowAnonymous` at the ASP.NET Core authorization layer because the endpoint authenticates using GitHub's HMAC signature; this does not bypass the signature verifier.

The extension is not yet registered in a running ServerApp/host. The host must explicitly call `MapGitHubWebhookIngress`, configure the secret and repository enrollment from trusted configuration, and apply trusted proxy/TLS and rate-limit middleware. No webhook has been registered with GitHub by this code change.

## Durable inbox and revision handling

`JsonlGitHubWebhookDeliveryInbox` is a local, single-process append-only JSONL journal. Each unique authenticated delivery records its delivery ID, SHA-256 of the exact raw payload, normalized CiAi event, work-item identity, sequence, disposition, and receipt time. Writes use write-through and an explicit disk flush before the receipt is returned.

- Repeating a delivery ID with identical payload bytes returns an idempotent duplicate receipt without another journal entry.
- Reusing a delivery ID with different payload bytes fails closed.
- A strictly newer `updated_at` becomes the current pending candidate.
- An older event is journaled as superseded and cannot replace the current candidate.
- A different PR head/base revision with the same timestamp is ambiguous and rejected rather than guessed.
- Repeating the same normalized revision under a different delivery ID is recorded but does not create another pending candidate.
- Startup replay checks schema, sequence, identities, disposition transitions, and final-record completeness. A torn final line fails closed.
- The journal has a 256 MiB bound and 2 MiB per-record bound. It is a local single-process durability primitive, not distributed storage or cross-process locking.

The inbox does not acknowledge a GitHub HTTP request itself. The hosting adapter must return success only after this durable acceptance completes. Operational retention, compaction, backup, and multi-instance coordination must be designed before production scale.

## Non-authority

A valid HMAC proves that the payload was signed by a holder of the configured webhook secret. It does not enroll a repository, authorize cross-repository reads, validate the issue/PR text, approve a task, or authorize repository writes. Issue bodies, comments, titles, patches, and linked content remain untrusted input. Repository enrollment and action policy remain host-owned; task lifecycle and wake-up remain FSM_Execution-owned; CiAi remains the authoritative classifier and composite gate evaluator.

## Not yet implemented

This contract does not claim a running/live HTTP endpoint, GitHub webhook registration/subscription, bounded LLM packet dispatch, or automatic repository actions. The route adapter exists but must be mapped by a host. The current inbox exposes latest pending candidates only; a host-owned dispatcher still must bind each candidate to enrollment policy, authorized linked context, exact revision, and the existing FSM_Execution durable task lifecycle.
