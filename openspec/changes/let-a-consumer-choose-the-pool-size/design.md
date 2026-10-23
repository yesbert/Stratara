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

### D2: Log once per resolved connection string

`ConfigureDbOptions` runs for every context instance. The log goes through a static
`ConcurrentDictionary<string, bool>` of the connection strings already reported, keyed by the string after the
rewrite, so a tenant-resolved string is reported once per tenant database, not once per context. The logger comes
from `ILoggerFactory` in the provider the factory passes. Where none is registered, nothing is logged.

## Risks / Trade-offs

- **[A host that wrote `Maximum Pool Size=100` relied on getting 15]** → Not plausible to rely on: nothing documented
  it. The changelog names the change.
