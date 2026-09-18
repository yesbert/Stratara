# Design

## D1 — What a reset between two tests has to mean

**Decision.** Stop the readers, put every registered consumer's checkpoint at the store's head, start
them again.

**Why the head and not the beginning.** The reset does not empty the read models — a test host holds
them in the same in-memory database as everything else, and a test asserts on them. A reader returned
to the beginning would apply the store again on top of what is already there. The head is what "forget
what has happened" means for a reader whose read model keeps what it applied.

**Why stopping matters.** A reader caches its position and only re-reads the checkpoint when it is
told to forget it. Stopping it does exactly that, and a reader that is stopped cannot be inside a batch
while the checkpoint moves. Without it the reset is invisible to the reader: it keeps its position, so
either nothing advances — the wait for the readers then never ends — or the next advance is refused and
it re-reads from whatever the checkpoint says.

## D2 — The pause belongs to every store reader

**Decision.** `PauseAsync`/`ResumeAsync` move from the projection's grain into the store reader every
consumer shares, and the saga's grain interface declares them too.

**Why.** A reset touches every registered consumer, and sagas are registered consumers. The pause was
a projection's because the rebuild is a projection's; the reset is not. The counting of pausers, the
wait for a running loop and the forgetting of the cached position are the same for both.

## D3 — What the host does about a port

**Decision.** A start refused with "address already in use" is tried again from the beginning: a new
database, new ports, the test's `BeforeStart` run again on it.

**Why not hold the listener until the silo binds.** Orleans binds the port itself, so the host would
have to hand the socket over, which the runtime does not offer. Retrying is what a test host can do,
and the window is small enough that two attempts have always been enough in practice.
