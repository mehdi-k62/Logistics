# Scale and Operability Note

## 50,000 impacted bookings

The process should be fan-out, not one giant transaction:

1. Identify impacted booking IDs with an indexed query.
2. Page the IDs (for example 500–2,000 at a time).
3. Create durable replanning work with a unique `(DisruptionId, BookingId)` constraint.
4. Let bounded workers claim processes with leases.
5. Keep each capacity reservation / booking assignment transaction small.
6. Use retry/backoff for transient failures and manual review for non-retryable incidents.

The capacity invariant remains local to SQL Server and is safe under concurrent workers. The worker pool can scale horizontally because claims are lease/version based.

## Operational signals

Minimum useful structured fields: `BookingId`, `VoyageId`, `ReplanningProcessId`, `CorrelationId`, `MessageId`.

Useful metrics: replanning started/completed/failed, capacity conflicts, manual review count, Outbox backlog, retry count, stuck replanning count, and duration.

## Failure operations

A stuck process is detectable from `NextAttemptAt`, lease expiry and process state. Manual retry is explicit. Manual intervention must be audited in a production deployment.
