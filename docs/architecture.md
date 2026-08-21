# Architecture — Disrupted Voyage Recovery

## 1. Business understanding and assumptions

The capability replans confirmed bookings when their voyage becomes unavailable. A replanning operation is durable and multi-step: identify impact, evaluate eligibility, select an alternative, reserve capacity, reassign the booking, release old capacity, complete the process, and publish `BookingReplanned`.

Assumptions kept intentionally narrow: no pricing, payment, customer management, shipment execution, UI, production broker cluster, or production Kubernetes.

## 2. Event Storming discoveries

The key hotspots are the capacity invariant, the single active booking assignment invariant, process ownership, crash recovery after reservation, duplicate disruption, replacement-voyage disruption, and publication failure. These hotspots directly shaped the SQL constraints, durable process state, Inbox, Outbox and failure-injection tests.

## 3. Ubiquitous Language

- **Booking** — confirmed cargo commitment with one current active voyage assignment.
- **Voyage** — scheduled transport option with operational state and capacity.
- **Capacity Reservation** — durable claim of TEU on a voyage for a replanning process.
- **Replanning Process** — durable coordination state for one booking/disruption pair.
- **Disruption** — business fact that a voyage is no longer usable.
- **BookingReplanned** — integration event emitted after business completion.

## 4. Bounded Contexts

- **Booking** owns booking lifecycle and the single-active-assignment invariant.
- **Voyage / Capacity** owns voyage operational state and capacity allocation.
- **Replanning** owns durable process state and cross-boundary coordination.

The deployment is a modular monolith. These are logical ownership boundaries, not three aggregates in one object model and not three production microservices.

## 5. Aggregate and consistency boundaries

Booking and Voyage/Capacity are separate consistency boundaries. Replanning is a process manager, not a giant aggregate containing both. Capacity correctness is enforced by a database conditional update and constraints; application-level locking is not the correctness mechanism.

## 6. Process coordination

The custom durable process manager is preferred over Camunda for this focused capability. Each process is persisted with state, retry metadata, lease metadata and correlation identifiers. Workers claim work using optimistic concurrency and a lease.

## 7. Concurrency and isolation

The critical reservation path uses one SQL transaction containing an atomic conditional `UPDATE` followed by the reservation insert. A unique `(ProcessId, VoyageId)` index makes repeated reservation attempts safe. Booking assignments use a filtered unique index so at most one active assignment exists.

## 8. Idempotency

HTTP commands use `Idempotency-Key` plus a payload hash and a unique `(Scope, Key)` index. Message consumption uses an Inbox/processed-message table. Business process creation additionally uses unique `(DisruptionId, BookingId)`.

## 9. Outbox / Inbox and delivery semantics

Business state and the Outbox intent are committed together. Publication is explicitly **at-least-once**. In the runnable candidate, the Outbox publisher dispatches `VoyageDisrupted` through `InProcessMessagePublisher` to the registered `VoyageDisruptedConsumer`; this is a transport seam rather than a claim that an external RabbitMQ/Kafka cluster is bundled. A production transport adapter can replace it. A crash after broker publication and before the database `Published` update can result in duplicate delivery. Consumers therefore remain idempotent. The solution does not claim end-to-end exactly-once.

## 10. Event ordering

The current Booking assignment is the business source of truth when deciding whether a disruption still affects a booking. A late disruption for an old voyage therefore cannot create a new replanning process after the booking has already moved. The process state machine also rejects invalid transitions.

## 11. Failure recovery

Mandatory failure injection covers: crash immediately after new capacity reservation, broker publication failure, and duplicate `VoyageDisrupted`. Additional tests cover replacement-voyage disruption, stale events, and publisher crash after publish.

The reservation crash case intentionally leaves durable process state one step behind while the reservation is committed. Recovery recognizes the existing `(ProcessId, VoyageId)` reservation and continues without double allocation.

## 12. Observability

The API exposes `/health`. Structured logging should carry BookingId, VoyageId, ReplanningProcessId, CorrelationId and MessageId on relevant operations. Retry, concurrency conflict, outbox publication and state transition logs are the operational baseline.

## 13. Scale scenario

For 50,000 impacted bookings, do not load all bookings into one process or transaction. Query impacted IDs in pages, enqueue one durable process per booking, and process with bounded worker concurrency. Indexes on `VoyageAssignments(VoyageId, State)`, `ReplanningProcesses(State, NextAttemptAt)` and Outbox due state support this model. Capacity remains protected by the database invariant.

## 14. Security / audit boundary

The API is intentionally unauthenticated for the challenge. In a production deployment, disruption declaration, force-replanning and manual recovery would be separate operator permissions, with correlation/audit records and sensitive payload fields masked from logs.

## 15. Architecture evolution

The Voyage/Capacity boundary is the most natural extraction candidate if independent deployment becomes necessary. Its data ownership moves with the module; the current application ports become remote adapters; integration-event contracts remain stable. The modular boundaries and dependency rules reduce extraction cost without paying distributed-system cost prematurely.

## 16. Main trade-offs and limitations

The solution favors correctness and explainability over framework breadth. SQL Server is a deliberate consistency boundary. The broker is abstracted/tested rather than deploying a real Kafka/RabbitMQ cluster. The process manager is intentionally small rather than a generic workflow engine. A production system would add richer tracing, metrics, authorization, operational dashboards and migration tooling.
