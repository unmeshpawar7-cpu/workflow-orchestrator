# ADR 0003: Saga-style compensation in reverse topological order

- Status: Accepted
- Date: 2026-10-08

## Context

Workflow steps call independent services; there is no distributed transaction. When a step fails permanently, the
effects of earlier steps (stock reserved, card charged) must be undone explicitly.

## Decision

- Each step may declare a **compensation handler** (any registered handler type, with its own config that may reference
  the step's own output via `{{steps.<self>.output...}}`).
- When a step exhausts its retries (or fails non-retryably) the run goes `Running → Failed`; waiting steps are
  `Skipped`. If any **succeeded** step has a compensation handler, the run moves to `Compensating`.
- Compensations run **one at a time in reverse topological order** (the order fixed when the definition was created).
  Strict sequencing keeps the undo order intuitive (refund before releasing stock) and the state easy to reason about;
  parallel compensation was rejected as premature.
- Compensation waits while forward attempts of sibling branches are still `Running`; if such an attempt succeeds late,
  that step is compensated too. Steps without a compensation handler are left `Succeeded`.
- Compensations reuse the step's retry policy and timeout. If one fails permanently the step becomes
  `CompensationFailed` and the run `DeadLettered`; the remaining compensations are **not** attempted, because
  continuing past an unknown state can make things worse.
- Operators choose the recovery direction: **requeue** (backward — retry failed compensations, then continue) or
  **retry** (forward — re-run failed steps and everything downstream).

## Consequences

- Clear, testable semantics (`WorkflowRun.Reconcile` + transition tables, covered by unit and engine tests).
- Compensations must be idempotent and tolerate partially applied forward effects (at-least-once delivery).
- Cancellation does not compensate (a deliberate "stop", not "undo"); compensate-on-cancel is a possible extension.
- Long sagas hold no locks; intermediate states are visible to other systems (the usual saga isolation trade-off).
