# Implementation plan

## P0 — make the happy path real

1. Add module application ports:
   - IBookingGateway
   - IVoyageGateway
   - ICapacityReservationGateway
2. Implement ReplanningOrchestrator.
3. Implement atomic ReserveCapacity SQL command.
4. Implement release capacity.
5. Implement assignment transaction.
6. Persist ReplanningProcess after every step.

## P0 — correctness

1. Unique `(DisruptionId, BookingId)`.
2. Filtered unique active Booking assignment.
3. Conditional capacity UPDATE.
4. Process optimistic concurrency.
5. Idempotency-Key hash validation.
6. Outbox transaction.
7. Inbox uniqueness.

## P0 — failure/recovery

Test:

- crash after reservation
- crash after reassignment
- old-capacity release failure
- duplicate disruption
- replacement-voyage disruption
- broker unavailable
- duplicate message
- out-of-order message
- stuck process

## P1 — operability

Metrics:

- replanning.started
- replanning.completed
- replanning.failed
- replanning.manual_review
- replanning.retry
- replanning.duration
- outbox.pending
- outbox.failed
- inbox.duplicate
- capacity.conflict

Logs should include:

- CorrelationId
- ProcessId
- BookingId
- DisruptionId
- VoyageId
- IdempotencyKey

## Real SQL Server race test

Run SQL Server first and apply `database/schema.sql`, then:

```bash
dotnet test tests/Logistics.IntegrationTests/Logistics.IntegrationTests.csproj --filter FullyQualifiedName~SqlServerConcurrencyTests
```

Optional connection override:

```bash
export LOGISTICS_TEST_CONNECTION_STRING='Server=localhost,1433;Database=Logistics;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False'
```

The main race test starts three workers behind a barrier and lets all three execute the same atomic reservation operation against one voyage with exactly one unit remaining. The expected result is exactly one successful reservation, two capacity conflicts, and a final reserved capacity of one.
