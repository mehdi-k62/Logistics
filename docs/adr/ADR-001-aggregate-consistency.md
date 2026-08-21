# ADR-001 — Aggregate & Consistency Boundaries

## Context
Capacity allocation and booking assignment have independent invariants and may be touched by a durable replanning process.

## Decision
Keep Booking and Voyage/Capacity as separate consistency boundaries. Keep Replanning as a durable process manager, not a giant aggregate.

## Alternatives
One aggregate spanning booking and voyage; distributed locks; distributed transaction.

## Consequences
Local transactions remain small and database invariants are explicit. The process must tolerate intermediate states and retries.

## Risks
More orchestration and recovery logic than one transaction.

## Revisit Conditions
If business invariants require atomic cross-context updates, or extraction changes latency/failure requirements materially, revisit the boundaries.
