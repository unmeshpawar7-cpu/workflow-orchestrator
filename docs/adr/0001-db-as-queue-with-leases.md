# ADR 0001: The database is the work queue (leases, not a message broker)

- Status: Accepted
- Date: 2026-10-08

## Context

Workflow state (runs, steps, outputs, history) must be durable and queryable. Work must be handed to workers, survive
worker crashes and be retried later. The usual alternative is a message broker (RabbitMQ, Azure Service Bus, SQS)
carrying "execute step X" messages next to a database holding the state.

With a broker, every transition is a **dual write**: update the database *and* publish/ack a message. Getting that
right needs an outbox, inbox deduplication and careful ordering, or runs end up stuck (state says Ready, no message) or
double-executed (message redelivered after the state moved on). Delayed retries need broker-specific features
(scheduled delivery, dead-letter TTL tricks).

## Decision

Step rows **are** the queue. A step is claimable when `Status IN (Ready, Retrying, Compensating)`,
`NextAttemptAt <= now` and `LeaseOwner IS NULL`. A worker claims it with a single conditional update:

```sql
UPDATE StepRuns
SET Status = 'Running', LeaseOwner = @worker, LeaseExpiresAt = @now + @lease,
    Attempt = Attempt + 1, Version = @newVersion
WHERE Id = @id AND Version = @seenVersion AND Status = @seenStatus AND LeaseOwner IS NULL
```

(issued through EF Core `ExecuteUpdateAsync`; one affected row means the claim was won). Leases are renewed by
heartbeats; expired leases are swept back to `Retrying`. Recording a result, promoting dependents and appending the
execution event happen in **one transaction**. Indexes `IX_StepRuns_Status_NextAttemptAt` and
`IX_StepRuns_LeaseExpiresAt` serve the two hot queries.

## Consequences

- No dual writes, no outbox: state and queue cannot disagree. Delayed retry is just a future `NextAttemptAt`.
- Portable: the same claim works on SQL Server and SQLite, so engine tests run the real protocol.
- Polling adds latency (bounded by `Worker:PollInterval`) and load (one indexed query per worker per tick).
- Throughput is bounded by the database: fine for orchestration (hundreds of steps per second), not for
  high-volume event streaming. If that is ever needed, the next steps are batch claims with `UPDLOCK, READPAST`, then
  a broker used only as a wake-up signal while the database stays authoritative.
- At-least-once execution remains (a crash after a side effect but before commit repeats it) — see
  [execution semantics](../execution-semantics.md).
