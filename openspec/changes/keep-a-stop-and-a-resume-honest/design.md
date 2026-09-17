# Design

## D0 — Which records the routing check applies to

**Decision.** Only records the execution model wrote itself. A store may hold both kinds during a rolling
adoption — the bus outbox stores a command when a publish fails — and such a row carries an id of its own
and no routing beside the envelope, so holding it to the envelope would keep every one of them for an
operator. The port says which kind a record is; the resume asks.

## D1 — What the signature answers for, and what it does not

**Decision.** The resume takes the heavy claim and the record's identity from the signed envelope and
refuses a row that disagrees. The aggregate id stays unsigned.

**Why not sign the aggregate id too.** The canonical form is shared with the bus envelope, and every
signature ever written was taken over the current one: adding a field would refuse every record and
every message signed before the upgrade. The aggregate id is also the one claim whose misuse the store
already answers for — it decides which activation accepts the command, while what the command writes
comes from its payload, which the digest covers, and the append is held to the stream's version.

**Why treat a disagreement as an integrity failure rather than an error.** It is the same kind of
finding as a signature that does not verify — the record says two different things — so it belongs in
the same two modes: kept under strict, resumed with a log under permissive.

## D2 — Giving up the queue with the stop

**Decision.** `RunAcceptedAsync` abandons what is still queued when it leaves the loop on the stop, and
`Accept` gives up a command that arrives afterwards.

**Why not leave it to `OnDeactivateAsync`.** That is where it was, and it cannot work: the runtime
waits for the activation's requests before it deactivates, and those requests are exactly the queued
commands' calls. The wait ends only when the runtime gives up on the deactivation, so the callers wait
for a timeout instead of an answer and the silo's stop waits with them.

## D3 — The claim's stamp stays as it is

**Decision.** The stamp keeps its millisecond truncation, and what that costs is written down where the
claim is implemented.

**Why not a discriminator below the millisecond.** It was tried and taken out: the truncation exists so
that the read-back compares what *every* provider stores, and a stamp with microseconds in it matches no
row at all on a provider that keeps milliseconds — every pass would raise the attempt count and hand over
nothing, until every recorded command was kept for an operator having never run.

**Why not a claim token column.** A column is a migration every consumer has to run, for a case whose
only cost is a second attempt counted: the grain that receives the hand-over holds one activation per
aggregate, per intent or per pool and refuses one it already holds, so the command still runs once.

## D4 — What is not tested here

The timer race — a registration landing between a tick's renewal check and its unregister — is closed
by taking the grain's gate around both. It has no test of its own: the window is inside one activation
and cannot be staged from outside without a seam that would only exist for the test. The existing
re-registration tests cover the path that a handler and a caller take.
