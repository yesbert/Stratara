## Context

See proposal.md, *Why*. `TimerOwnerGrain` is `[Reentrant]`, keyed by owner, and its only state is the reminder table.
`IRemindable.ReceiveReminder` does the following:

1. It adds the reminder name to `_firing`, which is how a second tick finds the first one still running.
2. It starts `FireAsync`, tracks the task in `_ticks` and awaits it. That await holds the reminder call open for the
   length of the handler.
3. When a cancellation comes because the silo stops, it logs `117_008` and rethrows.

`FireAsync` checks the owner, the due time and that the reminder still exists, and then runs the handler. It logs
`117_129` for committed-but-not-published, and then unregisters the reminder unless it was renewed. A handler that
throws anything else propagates out of `ReceiveReminder`. The reminder service logs that as a failed tick and keeps
the reminder, which fires again a retry period later.

The aggregate grain and the command runner grain already run long work through a `[OneWay]` call to themselves
(`AggregateGrain.cs`, `RunAcceptedAsync`). Such a call has no response, so it has no response timeout. Because it is
a request, it keeps the activation busy, and idle collection leaves it alone.

## Goals / Non-Goals

**Goals:**

- The reminder call returns at once, and the handler runs in a request that has no response timeout.
- Every timer guarantee in `orleans-execution` holds unchanged.
- A failing handler is logged by the framework.

**Non-Goals:**

- Changing the retry semantics, because a failed handler still fires again after the retry period.
- A tick during deactivation. A timer owner that is waiting for a running handler while it deactivates may still
  leave that tick undelivered, as readers and works may.

## Decisions

### D1 — The tick sends a one-way self-call and returns

`ITimerOwnerGrain` gains a `[OneWay]` method, `FireDueAsync(string reminderName)`, with its own alias.
`ReceiveReminder` sends it through `this.AsReference<ITimerOwnerGrain>()` and returns a completed task. A tick whose
handler already runs sends nothing, which saves a message per retry period during a long handler (review finding).
The authoritative guard stays in `FireDueAsync` (D2). `.Ignore()` observes a send that fails. Unlike `AggregateGrain`,
nothing needs cleaning up: a failed send means the activation is on its way out, and the reminder is still
registered, so the next tick or the next activation fires it.

*Alternative rejected:* run `FireAsync` without awaiting it, inside `ReceiveReminder`. Once the reminder call
returns, the activation has no request in flight, so idle collection could deactivate it mid-handler. The deactivation
budget would then cancel the handler. The self-call is a request and holds the activation.

*Alternative rejected:* `DelayDeactivation`. It is a time bound, and it can make neither a handler of unknown
length safe nor a silo stop prompt.

### D2 — The de-duplication moves into the self-call

`FireDueAsync` does what `ReceiveReminder` did after its send. It adds to `_firing` and returns if the name is
already there. It tracks the task in `_ticks`, runs `FireAsync`, and removes the name from `_firing` and
`_renewed` in `finally`. A tick that arrives while the handler runs therefore sends a second self-call, and that call
does nothing.

If `ReceiveReminder` kept the guard and the self-call were lost, the name would stay in `_firing` and the timer
would never fire again in that activation. With the guard in the self-call, nothing is held that a lost call could
leave behind.

### D3 — How a self-call ends

| How it ends | What is logged | The reminder |
|---|---|---|
| Cancelled while the silo stops | `117_008`, as today | stays, fires on the next silo |
| `CommittedEventsNotPublishedException` | `117_129`, as today | unregistered |
| Any other exception — of the handler, or of the owner check, port resolution, reminder lookup or unregistration around it | new `117_137` `TimerFiringFailed`, Error, owner, purpose, exception | stays, fires again a retry period later |

The self-call does not rethrow. A one-way call has no caller to tell, and a rethrow would only add a runtime line
next to the framework's own. The level is Error, because a failing timer is what the reminder service's error line
used to stand for. The event is named for the firing, not the handler, because the catch also sees failures around
the handler (review finding). The purpose is taken by `ReminderName.PurposeOf`, which never throws, so a reminder name
that cannot be decoded is still logged.

*Evidence:* the spec scenario *A timer's handler fails*, tested with a handler that throws once and then returns.

### D4 — Rolling deployments

The new method is only ever called by an activation on itself, so the message normally stays on the silo that sent
it. It is addressed by grain id, though. If the activation deactivates between the send and the delivery, the
runtime forwards the call to the next activation, which can be on a silo of the earlier version. There the alias is
unknown, the runtime logs the failed invocation and that tick is lost. The reminder is still registered, so the timer
fires on its next tick (review finding). An activation on a silo of the earlier version still awaits its handler in
the reminder call, as before.

## Risks / Trade-offs

- **[The tick returns before the owner check]** An owner that was removed is now found by the self-call rather than
  by the reminder call. → The order inside `FireAsync` is unchanged. The owner is checked first and the reminder
  unregistered, so the only difference is which request does it.
- **[A one-way self-call reaches a new activation after the old one deactivated]** → `FireAsync` checks that the
  reminder still exists before running the handler, and the new activation has its own `_firing`. A timer whose
  reminder was unregistered by the old activation therefore does not fire twice. The reminder table remains the
  single truth, as the class documentation says.
- **[`OnDeactivateAsync` waits for `_ticks`]** → Unchanged. A self-call in flight is both a request and a tracked
  tick, so a stopping silo waits for it within the budget and then cancels it.

## Migration Plan

None. Hosts upgrade by moving to the release. The guide's reminder profile changes with it.
