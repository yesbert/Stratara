# Environment — what Orleans 10.3.1 offers the proof of concept

Checked 2026-09-13 against nuget.org (flat container index and nuspec of each package) and the
`dotnet/orleans` source tree at tag `v10.3.1`.

## Packages and target frameworks

| Package id | Latest stable | Target frameworks |
|---|---|---|
| `Microsoft.Orleans.Server` | 10.3.1 | net8.0, net10.0 |
| `Microsoft.Orleans.Sdk` | 10.3.1 | net8.0, net10.0 |
| `Microsoft.Orleans.Reminders` | 10.3.1 | net8.0, net10.0 |
| `Microsoft.Orleans.Reminders.AdoNet` | 10.3.1 | net8.0, net10.0 |
| `Microsoft.Orleans.Reminders.Redis` | 10.3.1 | net8.0, net10.0 |
| `Microsoft.Orleans.Clustering.AdoNet` | 10.3.1 | net8.0, net10.0 |
| `Microsoft.Orleans.Clustering.Redis` | 10.3.1 | net8.0, net10.0 |
| `Microsoft.Orleans.GrainDirectory.Redis` | 10.3.1 | net8.0, net10.0 |
| `Microsoft.Orleans.GrainDirectory.AdoNet` | **none** — prerelease only, latest `10.3.1-alpha.1` | — |
| `Microsoft.Orleans.TestingHost` | 10.3.1 | net8.0, net10.0 |

**Orleans 10.3.1 targets `net10.0`.** Confirmed.

## Grain directory

**No stable ADO.NET grain directory exists.** The project is in the source tree
(`src/AdoNet/Orleans.GrainDirectory.AdoNet/`, with `PostgreSQL-GrainDirectory.sql`), but its project file
forces the version suffix `alpha.1`, so every published build is a prerelease. A stable Stratara package
cannot depend on it without taking a prerelease dependency (NU5104 on a stable pack).

Consequence for D7: **Redis is the storage-backed grain directory** of the proof of concept. The ADO.NET
directory is not measured; it is named in the recommendation as "not available as stable in 10.3.1".

## Reminders

- `ReminderOptions.MinimumReminderPeriod` defaults to **1 minute**
  (`src/Orleans.Reminders/Constants/ReminderOptionsDefaults.cs`, `MinimumReminderPeriodMinutes = 1`).
  A lower value is accepted, logged as a warning ("unsuitable for production use"), and only a negative
  value is rejected.
- `RefreshReminderListPeriod` defaults to 5 minutes, `ReminderLoadingWindow` to 10 minutes,
  `InitializationTimeout` to 5 minutes.
- Documented behaviour (Microsoft Learn, *Timers and reminders*): a reminder tick that is due while the
  cluster is down is **missed**, not replayed — only the next tick fires. Reminder delivery follows the
  grain's normal interleaving rules.

Consequences for D6:

1. Sub-minute wake-ups use a grain timer for speed, with a reminder as the safety net that re-arms it.
2. A reminder is not a scheduled job: a timeout that must fire *once at a time* is a reminder whose
   handler compares "now" with the due time it recorded, and acts if it is past — the missed-tick rule
   makes this necessary, not optional.
3. The hard-kill test (5.2) must allow for one refresh period before a restarted silo delivers reminders;
   its pre-registered wait covers `RefreshReminderListPeriod`, or the test lowers it and says so.

## ADO.NET schema scripts

The ADO.NET packages ship **no SQL scripts** — `Microsoft.Orleans.Reminders.AdoNet` and
`Microsoft.Orleans.Clustering.AdoNet` 10.3.1 contain only the assembly, its XML documentation and the
README. The PostgreSQL scripts are in the source tree at `v10.3.1`:

- `src/AdoNet/Shared/PostgreSQL-Main.sql`
- `src/AdoNet/Orleans.Clustering.AdoNet/PostgreSQL-Clustering.sql`
- `src/AdoNet/Orleans.Reminders.AdoNet/PostgreSQL-Reminders.sql`

Consequence: the integration tests apply a copy of these three scripts, taken from tag `v10.3.1`, to their
PostgreSQL container, and a shipped package would have to own the same copy with its migration story.
That is a cost of the ADO.NET providers that the recommendation records.

## Pins chosen for the proof of concept

`Directory.Packages.props`, one Orleans version for all Orleans packages: **10.3.1**.
`Testcontainers.PostgreSql` **4.14.0**, the version the other Testcontainers modules in the repository use
(4.15.0 exists; moving all modules together is a dependency update, not part of this change).
