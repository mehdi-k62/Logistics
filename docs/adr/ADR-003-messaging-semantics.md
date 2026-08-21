# ADR-003 — Messaging Technology & Delivery Semantics

## Context
Integration events must survive application/broker failure and duplicate delivery must be safe. A real broker cluster is not required by the challenge.

## Decision
Use an abstract publisher seam with an Outbox and assume at-least-once delivery. The reference implementation uses an explicit in-process transport adapter (`InProcessMessagePublisher`) so the repository is runnable without an external broker. The adapter is the seam where RabbitMQ/Kafka can be plugged in for production deployment; the consumer contract and Inbox semantics remain unchanged.

## Alternatives
Kafka; RabbitMQ; direct synchronous calls; exactly-once claim.

## Consequences
Outbox decouples business commit from broker availability. Consumers must be idempotent. Kafka remains attractive if retention/replay and high-throughput event streams dominate.

## Risks
At-least-once means duplicates are normal. Ordering is not globally guaranteed.

## Revisit Conditions
Choose a concrete broker when deployment constraints, retention, throughput, routing and operational ownership are known.
