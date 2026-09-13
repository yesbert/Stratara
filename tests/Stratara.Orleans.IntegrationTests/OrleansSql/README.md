# Orleans ADO.NET scripts for PostgreSQL

Verbatim copies from the `dotnet/orleans` repository at tag `v10.3.1`:

| File | Source path |
|---|---|
| `PostgreSQL-Main.sql` | `src/AdoNet/Shared/PostgreSQL-Main.sql` |
| `PostgreSQL-Clustering.sql` | `src/AdoNet/Orleans.Clustering.AdoNet/PostgreSQL-Clustering.sql` |
| `PostgreSQL-Reminders.sql` | `src/AdoNet/Orleans.Reminders.AdoNet/PostgreSQL-Reminders.sql` |

The `Microsoft.Orleans.Clustering.AdoNet` and `Microsoft.Orleans.Reminders.AdoNet` packages ship no
scripts, so anything that uses them has to carry its own copy and apply it in this order: main,
clustering, reminders. The integration tests apply them to a database of their own on the test
container. A shipped package would own the same copy and its migration story — a cost of the ADO.NET
providers the recommendation records.
