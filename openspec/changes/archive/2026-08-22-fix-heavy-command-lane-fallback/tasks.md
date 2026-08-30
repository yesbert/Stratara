# Tasks

- [x] Trace what `MediatorCommandWorker` on the interactive lane does with a heavy command that
      reaches it — reject, or run. Record the answer in this change before choosing a fix.
- [x] Persist the lane alongside the outbox entry, so republication does not depend on resolving the
      command type (`src/Stratara.Outbox.RabbitMQ/Outbox/CommandOutboxDispatcher.cs`).
- [x] Test: a stored heavy command whose type is not in the allowlist republishes to the heavy topic.
- [x] Test: a stored ordinary command still republishes to the shared topic.
