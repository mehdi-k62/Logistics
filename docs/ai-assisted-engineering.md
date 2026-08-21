# AI-Assisted Engineering Report

## Tools / context

ChatGPT was used as an implementation and review assistant. The challenge document was supplied as the primary specification. The generated solution was reviewed against the explicit requirements, non-goals, hard-fail conditions and submission-readiness checklist.

## Prompt patterns

- Ask for a focused .NET 10 implementation around the supplied invariants.
- Ask for genuine SQL Server concurrency tests rather than sequential mocks.
- Ask for failure injection at precise transaction boundaries.
- Ask for duplicate, stale and out-of-order message scenarios.
- Ask for an audit against the challenge rather than adding unrelated scope.

## Generated code / tests

AI-assisted work included the initial modular structure, domain/process skeletons, atomic reservation strategy, worker lease model, crash-recovery tests, disruption consumer tests, Outbox dispatcher tests and documentation drafts.

## Verification / review

The design was checked against the challenge's mandatory tests and hard-fail conditions. The critical distinction between Domain Events + Outbox and Event Sourcing was explicitly reviewed against the challenge's Non-Goals section. Build/test execution is performed on the candidate machine; the submission README records reproducible commands.

## Rejected AI recommendations

1. **Event Sourcing** was rejected because the challenge explicitly lists Event Sourcing as a Non-Goal.
2. **Microservices for every bounded context** was rejected because the challenge says microservices receive no extra score and unnecessary distribution reduces the architecture score.
3. **Distributed locking as the capacity solution** was rejected because database-enforced correctness is the stronger invariant boundary.

## AI-generated defect / weak assumption discovered

An early generated version treated Domain Events plus Outbox as if it were Event Sourcing. Reviewing the source against the challenge corrected the terminology and implementation direction. Another early skeleton described several items as future work; the final audit converted the README into a truthful submission-oriented description.

## Decisions owned by the candidate

The final choices of modular monolith, database correctness boundary, durable custom process manager, at-least-once semantics, current-assignment stale-event handling, and limited scope remain explicit architectural decisions to defend in review.

## Where AI helped / hurt

AI accelerated scaffolding, test scenario enumeration and documentation. It increased review effort because generated claims had to be checked against the source challenge and because incremental code generation can leave inconsistent documentation or partially implemented seams.
