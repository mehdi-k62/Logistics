# Event Storming Summary

## Actors

Operator, Replanning Worker, Voyage/Capacity module, Booking module, Message Publisher, Integration Consumer.

## Commands

`DeclareVoyageDisrupted`, `StartReplanning`, `EvaluateEligibility`, `SelectAlternative`, `ReserveCapacity`, `ReassignBooking`, `ReleasePreviousCapacity`, `CompleteReplanning`, `RetryReplanning`, `ManuallyResolve`.

## Domain events

`VoyageDisrupted`, `ReplanningStarted`, `EligibilityConfirmed`, `AlternativeSelected`, `CapacityReserved`, `BookingReassigned`, `OldCapacityReleased`, `ReplanningCompleted`.

## Integration events

`BookingReplanned` is emitted only after business completion and stored transactionally in the Outbox.

## Policies / failure paths

- Duplicate disruption → Inbox + unique process key.
- Duplicate command → HTTP idempotency + unique key.
- Capacity conflict → retry/alternative/manual review.
- Crash after reservation → durable process recovery and idempotent reservation.
- Publication failure → Outbox retry.
- Duplicate publication → idempotent consumer.
- Stale disruption → current booking assignment wins.
- Replacement voyage disrupted → new replanning process for the current assignment.
- Stuck process → explicit detection and manual recovery.
