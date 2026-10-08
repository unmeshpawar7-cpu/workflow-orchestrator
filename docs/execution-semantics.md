# Execution semantics

This document states what the engine guarantees, what it does not, and what handler authors must therefore do.

## Delivery: at-least-once

A step attempt can execute **more than once** for the same logical attempt in these cases:

- The worker crashes, is killed, or is partitioned from the database after the handler produced its side effect but
  before the result was committed. The lease expires, recovery puts the step back to `Retrying`, and another worker
  runs it again.
- A worker pauses longer than its lease (GC pause, CPU starvation) while the handler keeps running. Recovery re-queues
  the step; when the original worker comes back, its result is rejected by the fencing token — but its side effect has
  already happened.
- A run is retried by an operator (`POST /api/runs/{id}/retry`): failed steps run again by design.

The engine guarantees that **at most one result is recorded per attempt** and that a worker without a valid lease can
never record anything (fencing). It cannot make a remote side effect exactly-once.

### Handlers must be idempotent

Every attempt receives `StepExecutionContext.IdempotencyKey` = `{runId}:{stepKey}:{execute|compensate}` — stable across
retries and redeliveries. Downstream calls should be deduplicated with it; the built-in `http` handler sends it as the
`Idempotency-Key` header. Compensation handlers must also tolerate running for a step whose forward side effect only
partially happened.

## Leases

- A claim grants `Worker:LeaseDuration` (default 30 s). While the handler runs, a heartbeat extends the lease every
  `Worker:HeartbeatInterval` (default 10 s) and rotates the step's `Version` (the fencing token).
- A heartbeat that affects zero rows means the lease was lost: the attempt is aborted cooperatively and its result is
  discarded.
- The recovery sweep (each worker tick) finds leases with `LeaseExpiresAt < now`. An expired forward attempt **counts as
  a failed attempt**: it becomes `Retrying` (with backoff) while the budget allows, otherwise `Failed`. An expired
  compensation is retried or marked `CompensationFailed` the same way. If the run is no longer running, the step is
  marked `Cancelled`.
- Lease length is a trade-off: shorter leases recover crashed work faster but tolerate less pause/latency before a
  healthy attempt is duplicated. With heartbeats, long-running steps (up to the 1-hour timeout cap) do not need long leases.

## Retries, timeouts and failure classes

| Failure | Retryable | Result |
| --- | --- | --- |
| Handler throws `StepFailedException(retryable: true)` or any other exception | yes | `Retrying` until `maxAttempts`, then `Failed` |
| `StepFailedException(retryable: false)` (e.g. HTTP 4xx, invalid config) | no | `Failed` immediately |
| Template cannot be resolved (missing path / output) | no | `Failed` immediately |
| Attempt exceeds `timeoutSeconds` | yes | Token cancelled; treated as a retryable failure ("Timed out after Ns") |
| Lease expired | yes | As above; counts as an attempt |
| Worker shutting down | n/a | Step handed back (`Retrying`, due now); the interrupted attempt is **not** counted |

Delay before attempt *n + 1*: `min(initialDelay · multiplier^(n−1), maxDelay)` multiplied by a uniform jitter factor in
`[1 − jitter, 1 + jitter]`, and never more than `maxDelay`.

Handlers must honour their `CancellationToken`; a handler that ignores it keeps running after the timeout (the engine
cannot kill a thread) and its eventual result is recorded only if its lease is still valid.

## Ordering guarantees

- A step starts only after **all** of its dependencies have `Succeeded` (and their outputs are committed).
- Independent branches run concurrently, in no guaranteed order relative to each other; candidates are claimed roughly
  FIFO by `NextAttemptAt`.
- Compensation is **sequential, in reverse topological order** (the deterministic order computed when the definition
  was created; ties between independent steps are broken by declaration order). Only steps that `Succeeded` and have a
  compensation handler are compensated. Compensation waits until no forward attempt of the run is still `Running`; a
  sibling that succeeds late is compensated as well.
- Runs of the same definition are independent; `MaxConcurrentRuns` limits how many are active. With several scheduler
  instances the limit is soft (two instances may both start a run in the same tick).
- Execution events of a run are totally ordered by their identity column and are written in the same transaction as
  the state change they describe.

## Cancellation

`POST /api/runs/{id}/cancel` (Pending or Running runs) marks the run `Cancelled` and cancels every step that is not
executing. Steps that are executing are cancelled **cooperatively**: the worker notices the run status on its next
heartbeat (≤ `HeartbeatInterval`), cancels the handler's token and records the step as `Cancelled`. If the handler
finishes before noticing, its success is recorded truthfully. Cancellation does not run compensations.

## What happens on a crash

| Crash point | Outcome |
| --- | --- |
| API process dies after `202` | Run and steps are already committed; scheduler/worker pick them up. |
| API dies mid-insert | Transaction rolls back; client retries with the same `Idempotency-Key` and gets one run. |
| Worker dies after claiming, before/while executing | Lease expires → recovery → retried (counts as an attempt). |
| Worker dies after the side effect, before recording | Same as above → the side effect may repeat (idempotent handlers required). |
| Worker dies after recording, before promoting dependents | Impossible: recording and promotion are one transaction. |
| Scheduler dies mid-tick | Each schedule/run is its own transaction; the next tick (on any instance) continues. |
| Redis unavailable | Locks degrade to no-ops, idempotency falls back to the database; correctness is unaffected. |
| Database unavailable | Loops log and retry on the next tick; `/health/ready` reports unhealthy. |

## Operator recovery

- **Retry** (`POST /api/runs/{id}/retry`, Failed or DeadLettered): forward recovery. Every step that does not hold a
  successful result, plus everything downstream of it, is reset to `Pending` with a fresh retry budget; successful
  upstream steps keep their outputs.
- **Requeue** (`POST /api/dead-letters/{runId}/requeue`, DeadLettered): backward recovery. Failed compensations get a
  fresh budget and compensation continues in reverse order.
