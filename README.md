# Logistics Challenge — .NET 8 Modular Monolith

This repository is a deliberately focused implementation skeleton for the supplied logistics/replanning challenge.

## Design

- .NET 8
- Modular Monolith
- DDD tactical modeling
- Booking, Voyage and Replanning modules
- Persistent Replanning Process
- SQL Server as consistency boundary
- Atomic conditional capacity reservation
- Database uniqueness constraints for business invariants
- HTTP Idempotency-Key
- Outbox / Inbox tables
- Optimistic concurrency on process state
- At-least-once messaging model
- Durable retry / ManualReview states

## Important boundaries

### Booking

Owns:

- Booking lifecycle
- Current active VoyageAssignment
- Invariant: max one active assignment

### Voyage / Capacity

Owns:

- Voyage operational state
- Total capacity
- Reserved capacity
- Capacity reservations
- Invariant: ReservedCapacity <= TotalCapacity

### Replanning

Owns:

- Durable process state
- Retry / timeout metadata
- Recovery
- Coordination between Booking and Voyage

Replanning does NOT become a giant aggregate containing Booking and Voyage.

## Critical SQL correctness

The capacity path uses:

```sql
UPDATE Voyages
SET ReservedCapacity = ReservedCapacity + @Units
WHERE Id = @VoyageId
  AND State = 'Available'
  AND ReservedCapacity + @Units <= TotalCapacity;
```

The update and CapacityReservation insert are in one SQL transaction.

This is intentionally preferred over:

```text
SELECT capacity
    -> check in application
    -> UPDATE
```

because that pattern is race-prone.

## Run

Prerequisites:

- .NET 8 SDK
- Docker

Start SQL Server:

```bash
docker compose up -d
```

Create database/schema using `database/schema.sql`.

Then:

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/Logistics.Api
```

Swagger:

```text
http://localhost:5000/swagger
```

## API

Minimum API surface:

```text
POST /api/voyages
POST /api/bookings
POST /api/voyages/{voyageId}/disruptions
POST /api/bookings/{bookingId}/replanning
GET  /api/replanning/{processId}
```

Example voyage creation:

```json
{
  "voyageId": "V100",
  "totalCapacity": 100
}
```

Example booking creation:

```json
{
  "bookingId": "B001",
  "requestedTeu": 10
}
```

`VoyageId` and `BookingId` are business identifiers stored as `VARCHAR(10)`. `ProcessId` remains a GUID because it identifies the durable replanning process rather than a business entity.


### Start

```http
POST /api/voyages/{voyageId}/disruptions
POST /api/bookings/{bookingId}/replanning
Idempotency-Key: <unique-key>
Content-Type: application/json
```

```json
{
  "disruptionId": "00000000-0000-0000-0000-000000000001",
  "reason": "VoyageCancelled",
  "requestedAlternativeVoyageId": null
}
```

### Retry

```http
POST /api/replanning/{processId}/retry
```

### Status

```http
GET /api/replanning/{processId}
```

## Submission status

This repository is the final audit pass. The challenge-required vertical slice is implemented with durable process state, database-enforced concurrency, HTTP idempotency, Inbox/Outbox semantics, failure injection, duplicate/stale event handling, and automated concurrency/recovery tests.

The repository also contains the required architecture package under `docs/`: architecture document, Event Storming summary, scale/operability note, exactly three ADRs, four reproducible Mermaid diagrams, and the AI-assisted engineering report. Two architecture fitness tests live under `tests/Logistics.ArchitectureTests`.

### Challenge coverage matrix

| Requirement | Evidence |
|---|---|
| Atomic capacity correctness | `database/schema.sql`, `ReserveCapacity` |
| Genuine concurrent reservation | `SqlServerConcurrencyTests` |
| Durable crash recovery | `CrashRecoveryTests` + failure injector |
| Duplicate disruption | `DisruptionConsumerTests` + Inbox |
| Outbox failure / at-least-once | `OutboxFailureTests` |
| Architecture boundaries | `Logistics.ArchitectureTests` |
| Database migration baseline | `database/migrations/001_initial.sql` |
| Architecture docs | `docs/architecture.md` |
| Event Storming | `docs/event-storming-summary.md` |
| Three ADRs | `docs/adr/` |
| Required diagrams | `docs/diagrams/` |
| Scale note | `docs/scale-and-operability.md` |
| AI report | `docs/ai-assisted-engineering.md` |

## Test commands

```bash
docker compose up -d
# apply database/migrations/001_initial.sql to the SQL Server database
dotnet restore
dotnet build
dotnet test

# targeted tests
dotnet test --filter FullyQualifiedName~SqlServerConcurrencyTests
dotnet test --filter FullyQualifiedName~CrashRecoveryTests
dotnet test --filter FullyQualifiedName~DisruptionConsumerTests
dotnet test --filter FullyQualifiedName~OutboxFailureTests
dotnet test --filter FullyQualifiedName~BoundaryTests
```

## Failure injection

The three mandatory failure scenarios are reproducible through test fixtures and seams:

1. Crash immediately after new capacity reservation.
2. Broker publication failure.
3. Duplicate `VoyageDisrupted` delivery.

The expected outcome of each scenario is documented in `docs/architecture.md` and the corresponding test names.

## Interview defense

### Why modular monolith?

Multiple consistency boundaries do not automatically require multiple deployment boundaries. The modular monolith keeps transactions and failure handling explicit while leaving extraction paths open.

### Why not distributed transactions?

The workflow spans multiple aggregates/consistency boundaries. Local ACID transactions plus a durable process manager, idempotency, outbox/inbox and recovery are simpler and more explicit.

### Why not distributed locking?

Distributed locks can be useful coordination mechanisms but should not be the final correctness mechanism for capacity. The database conditional update and constraints enforce the invariant.

### Why Outbox?

Business state and publication intent must commit atomically. Broker availability must not determine whether the business transaction succeeds.

### Why Inbox?

Messaging is at-least-once. Duplicate delivery must not create a second business effect.


## Identifier convention

Business identifiers for Voyages and Bookings are human-readable string keys stored as `VARCHAR(10)`:

- Voyage: `V100`, `V200`, ...
- Booking: `B001`, `B002`, ...

The replanning process identifier remains a `GUID`, since it is a technical process/correlation identifier rather than a business key.

## Required API surface

```http
POST /api/voyages/{voyageId}/disruptions
POST /api/bookings/{bookingId}/replanning
GET  /api/replanning/{processId}
```

Example business identifiers: `V100` and `B001`.

### Message flow

`VoyageDisrupted` is written to the transactional Outbox, the Outbox publisher dispatches it to the registered `VoyageDisruptedConsumer`, the consumer writes Inbox + durable replanning processes transactionally, and the ReplanningWorker advances those processes.

Replanning captures the booking's current voyage at process creation and selects an available alternative different from that current/disrupted voyage.


### Runtime message flow

The runnable candidate has a concrete consumer connection; it is not only the runnable in-process transport adapter:

```text
POST /api/voyages/{voyageId}/disruptions
        |
        +--> Voyage.Disrupt() + Outbox row (same DB transaction)
                                |
                                v
                       OutboxPublisherWorker
                                |
                                v
                    InProcessMessagePublisher
                                |
                                v
                    VoyageDisruptedConsumer
                         |              |
                         |              +--> ProcessedMessages (Inbox)
                         v
                  ReplanningProcesses
                         |
                         v
                  ReplanningWorker
                         |
                         v
               reserve -> reassign -> release
```

`ReplanningProcess.CurrentVoyageId` is captured when the process is created. Alternative selection explicitly excludes that current/disrupted voyage. The API also supports `RequestedAlternativeVoyageId` when a specific replacement is requested.
