# ADR 0004: API keys with hashed storage for machine-to-machine access

- Status: Accepted
- Date: 2026-10-08

## Context

Callers are services (CI pipelines, back-office systems, schedulers), not people. There is no login UI and no user
directory to integrate. Options considered: OAuth 2.0 client credentials (JWT bearer from an identity provider), mTLS,
and static API keys.

## Decision

Use an `X-Api-Key` authentication handler:

- Keys are configured as **SHA-256 hex digests** with a client name and roles (`workflow.admin`, `workflow.operator`,
  `workflow.reader`); plaintext keys never appear in configuration, logs or the repository.
- The presented key is hashed and compared with every configured digest using
  `CryptographicOperations.FixedTimeEquals`, without early exit.
- The matching entry becomes a `ClaimsPrincipal` (name + role claims); role-based policies are applied per endpoint.
- 401 and 403 responses use the standard error envelope. Run creation is rate limited per key.

SHA-256 (not a slow password hash) is appropriate because keys are high-entropy random secrets, not human passwords;
brute-forcing a 128-bit+ random key from its digest is infeasible, and a fast hash keeps per-request cost negligible.

## Consequences

- Zero external dependencies; works the same locally, in CI and in containers. Keys rotate by adding the new digest,
  migrating clients, then removing the old one (several keys may share a role).
- No expiry, scopes or audience binding beyond roles; a leaked key is valid until removed from configuration.
- When an identity provider is available, OAuth 2.0 client credentials (JWT bearer) is the natural upgrade: the
  authorization policies stay unchanged because they only depend on role claims.
