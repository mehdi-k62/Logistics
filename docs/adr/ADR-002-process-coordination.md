# ADR-002 — Process Coordination: Camunda/BPMN vs Custom Process Manager

## Context
The challenge requires durable multi-step coordination, retries, recovery and manual intervention, but intentionally scopes to one capability.

## Decision
Use a small custom durable Process Manager in the modular monolith. Persist state, retries and leases in SQL Server.

## Alternatives
Camunda/BPMN; generic workflow engine; in-memory saga.

## Consequences
Less infrastructure and a smaller explainable surface. Recovery semantics are explicit in code and database state.

## Risks
As workflows multiply, custom orchestration can become costly and harder to visualize.

## Revisit Conditions
Revisit when workflow count/complexity, human tasks, timers or cross-team workflow governance justify a workflow platform.
