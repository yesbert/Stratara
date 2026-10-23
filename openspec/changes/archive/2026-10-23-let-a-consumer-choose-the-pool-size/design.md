## Context

See `proposal.md`, *Why*. Read on `main` at `065d14b` (`v4.4.6`): `ConfigureDbOptions` is shared by
`AddNpgsqlWriteDbContextFactory`, `AddNpgsqlReadDbContextFactory` and `AddNpgsqlIdentityDbContextFactory`, and runs
for every context the factory creates. It parses the connection string with `NpgsqlConnectionStringBuilder` and
rewrites `MaxPoolSize == 100` to 15. The constant has been there since the first public release (`ac33172`).

## Decisions

### D1: Decide from the keys, not from the value

*As built; the plan named `ContainsKey`.* The host's string is read with a plain `DbConnectionStringBuilder`, which
keeps the keys as given. Each key is applied to an `NpgsqlConnectionStringBuilder` preset to a maximum pool size of
1, and to another preset to 2. A key that moves either preset names the pool size, under whatever keyword Npgsql
takes for it (`Maximum Pool Size`, `MaxPoolSize`, in any case), whatever value it gives, 1 and 2 included. Only a
string with no such key gets the default. The default stays 15. Changing it would change every host that names
nothing, and the finding asks for choice, not for another default.

*Why not `NpgsqlConnectionStringBuilder.ContainsKey`:* it answers for keywords Npgsql knows, not for keys the string
set, so it cannot tell a named size from an unnamed one. *Found while testing:* Npgsql does not take
`Max Pool Size`, although its own pool-exhaustion message names the setting that way. The guide names
`Maximum Pool Size`.

*Alternative rejected: an option on the registration.* It adds API to express what the connection string already
expresses, and a consumer would then have two places that disagree.

### D2: Log once per host and database, under the framework's category

*As built after review; the plan logged once per connection string from a static set.* Each registration adds a
singleton `PoolSizeReport`. Its set is keyed by host, port, database, user, size and origin, never by the password,
so a rotated credential neither logs again nor stays in memory. Each host keeps its own set, so a second host in the
same process, such as a test fixture or a silo beside a client, reports for itself. A database is recorded only once
the line was actually written, so a level that suppressed it at first does not suppress it for good. The category is
`Stratara.EventSourcing.EntityFrameworkCore.ConnectionPool`, not the extension's namespace,
`Microsoft.Extensions.DependencyInjection`, which a common `"Microsoft": "Warning"` filter would hide.

*Kept as built: no per-string result cache.* The review suggested caching the result per input string. That would
keep every connection string, passwords included, for the life of the host. The cost it saves is one parse and two
probe builders per context.

## Risks / Trade-offs

- **[A host that wrote `Maximum Pool Size=100` relied on getting 15]** → Not plausible to rely on: nothing documented
  it. The changelog names the change.
