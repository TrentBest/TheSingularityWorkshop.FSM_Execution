# GitHub Webhook Ingress Boundary

**Contract:** `fsm-execution.github-webhook-ingress.v1`  
**Status:** signature-verification core implemented; HTTP route, enrolled-repository policy, durable delivery inbox, coalescing, and task admission remain outstanding.

## Authentication

The receiver must preserve the exact request-body bytes and verify GitHub's `X-Hub-Signature-256` header before parsing JSON or trusting any payload field. `GitHubWebhookSignatureVerifier` computes HMAC-SHA256 over those exact bytes and compares the supplied digest with a fixed-time comparison.

The verifier requires a host-provisioned secret of at least 32 bytes, rejects missing or malformed signatures, rejects empty bodies and bodies larger than 1 MiB, and clears its in-memory secret when disposed. The secret is never part of the webhook body, event contract, task packet, logs, or LLM context.

## Required host sequence

1. Enforce HTTPS, request rate limits, and the 1 MiB streaming body limit at the HTTP boundary.
2. Read the raw body once without JSON reserialization.
3. Verify `X-Hub-Signature-256` using the configured GitHub webhook secret.
4. Validate `X-GitHub-Delivery`, `X-GitHub-Event`, and enrolled repository identity.
5. Only then parse through CiAi's versioned `GitHubWorkItemEvent` contract.
6. Persist/deduplicate the delivery and coalesce newer revisions before creating or updating a bounded FSM_Execution task.

The GitHub webhook HMAC protocol is separate from the RS256 bearer-token protocol used by the CiAi artifact-ready callback. Neither protocol substitutes for the other.

## Non-authority

A valid HMAC proves that the payload was signed by a holder of the configured webhook secret. It does not enroll a repository, authorize cross-repository reads, validate the issue/PR text, approve a task, or authorize repository writes. Issue bodies, comments, titles, patches, and linked content remain untrusted input. Repository enrollment and action policy remain host-owned; task lifecycle and wake-up remain FSM_Execution-owned; CiAi remains the authoritative classifier and composite gate evaluator.

## Not yet implemented

This contract does not claim a live endpoint, durable webhook inbox, replay/delivery deduplication, revision coalescing, GitHub event subscription, LLM packet dispatch, or automatic repository actions.
