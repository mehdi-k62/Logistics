# Outbox failure model

The publisher intentionally implements **at-least-once delivery**.

There is an unavoidable ambiguity window:

```text
DB: Outbox = Processing
        |
        v
Broker: Publish succeeds
        |
        X process crashes / network failure
        |
DB: Published flag was never committed
```

On restart, the lease expires and another worker can publish the same message.
Therefore the design does **not** promise exactly-once publication.

Instead:

1. Outbox makes business transaction + publication intent atomic.
2. Publisher uses a short database lease so abandoned work is recoverable.
3. Broker publication is retried.
4. Consumers use Inbox/ProcessedMessages with a unique `(ConsumerName, MessageId)` key.
5. Business handlers are idempotent.

The correct invariant is therefore:

> Every committed integration event is eventually published at least once, and duplicate delivery does not create a duplicate business effect.
