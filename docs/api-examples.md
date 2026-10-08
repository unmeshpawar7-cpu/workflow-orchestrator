# API examples

Assumes `docker compose up` (API on `http://localhost:8080`) with the demo keys from `.env.example`.

```bash
API=http://localhost:8080
ADMIN="X-Api-Key: dev-admin-key-change-me"
OPS="X-Api-Key: dev-operator-key-change-me"
READ="X-Api-Key: dev-reader-key-change-me"
```

## Register a definition

```bash
curl -s -X POST $API/api/workflows -H "$ADMIN" -H "Content-Type: application/json" \
  -d @samples/workflows/document-processing-pipeline.json
```

`201 Created`, `Location: /api/workflows/{id}`:

```json
{
  "success": true,
  "data": {
    "id": "8a1c...", "name": "document-processing", "version": 1, "maxConcurrentRuns": 50,
    "steps": [
      { "key": "ingest", "handler": "transform", "dependsOn": [], "ordinal": 0, "timeoutSeconds": 30,
        "retry": { "maxAttempts": 3, "initialDelayMs": 1000, "backoffMultiplier": 2.0, "maxDelayMs": 60000, "jitter": 0.2 } }
    ]
  },
  "message": null
}
```

Posting a definition with the same name again creates `version: 2`; existing runs keep using version 1.

## Validation error (cycle)

```bash
curl -s -X POST $API/api/workflows -H "$ADMIN" -H "Content-Type: application/json" -d '{
  "name": "broken",
  "steps": [
    { "key": "a", "handler": "log", "dependsOn": ["c"] },
    { "key": "b", "handler": "log", "dependsOn": ["a"] },
    { "key": "c", "handler": "log", "dependsOn": ["b"] }
  ]}'
```

`400 Bad Request`:

```json
{
  "success": false,
  "message": "Workflow definition is invalid.",
  "errors": [ { "field": "steps.b", "message": "Cycle detected: b -> c -> a -> b." } ],
  "traceId": "5d1f0c..."
}
```

## Start a run (idempotent)

```bash
curl -s -i -X POST $API/api/workflows/{definitionId}/runs -H "$OPS" -H "Content-Type: application/json" \
  -H "Idempotency-Key: doc-2026-0042" -H "X-Correlation-Id: upload-77" \
  -d @samples/runs/document-processing.json
```

- First call: `202 Accepted`, `Location: /api/runs/{runId}`, body = the run (status `Pending`).
- Same key and same payload (key order/whitespace do not matter): `200 OK`, same run,
  `"message": "Idempotent replay: returning the run created earlier with this key."`
- Same key, different payload: `409 Conflict`.
- Delayed start: add `"scheduledFor": "2026-10-08T18:00:00Z"`.

## Inspect a run and its history

```bash
curl -s $API/api/runs/{runId} -H "$READ"
curl -s "$API/api/runs/{runId}/history?page=1&pageSize=50" -H "$READ"
```

History items:

```json
{ "id": 41, "stepKey": "check-ocr-service", "eventType": "StepRetryScheduled",
  "message": "Step 'check-ocr-service' attempt 1 failed; retry at 2026-10-08T10:15:03.1200000Z. StepFailedException: http: GET http://localhost:8080/health/live returned 503.",
  "data": { "attempt": 1, "retryAt": "2026-10-08T10:15:03.12Z", "durationMs": 14 }, "occurredAt": "2026-10-08T10:15:02.1Z" }
```

## List and filter

```bash
curl -s "$API/api/runs?status=Running&page=1&pageSize=20" -H "$READ"
curl -s "$API/api/runs?definitionId={definitionId}" -H "$READ"
```

```json
{ "success": true, "data": { "items": [ ... ], "page": 1, "pageSize": 20, "totalCount": 3, "totalPages": 1 }, "message": null }
```

## Cancel, retry, dead letters

```bash
curl -s -X POST $API/api/runs/{runId}/cancel -H "$OPS"        # 202, or 409 if not Pending/Running
curl -s -X POST $API/api/runs/{runId}/retry  -H "$OPS"        # 202 for Failed/DeadLettered runs
curl -s "$API/api/dead-letters" -H "$READ"
curl -s -X POST $API/api/dead-letters/{runId}/requeue -H "$OPS" # 202 -> Compensating
```

## Schedules

```bash
curl -s -X POST $API/api/workflows/{definitionId}/schedules -H "$OPS" -H "Content-Type: application/json" \
  -d @samples/schedules/chaos-every-5-minutes.json                    # 201
curl -s -X PATCH $API/api/schedules/{scheduleId} -H "$OPS" -H "Content-Type: application/json" \
  -d '{ "enabled": false }'                                            # 200
curl -s "$API/api/schedules" -H "$READ"
```

## Stats and health

```bash
curl -s $API/api/stats -H "$READ"
curl -s $API/health/ready
```

```json
{ "success": true, "data": {
    "runsByStatus": { "Pending": 0, "Running": 1, "Succeeded": 12, "Failed": 0, "Compensating": 0, "Compensated": 2, "DeadLettered": 0, "Cancelled": 1 },
    "stepsByStatus": { "Succeeded": 61, "Retrying": 1, "...": 0 },
    "averageStepDurationMs": 87.4, "stepsThatRetried": 5, "totalRetries": 7,
    "definitionCount": 3, "enabledSchedules": 1, "generatedAt": "2026-10-08T10:20:00Z" },
  "message": null }
```

(Numbers are illustrative.)

## Auth failures

```bash
curl -s $API/api/workflows                      # 401 {"success":false,"message":"A valid X-Api-Key header is required.",...}
curl -s -X POST $API/api/workflows -H "$READ"   # 403 {"success":false,"message":"The API key does not grant the role required for this operation.",...}
```
