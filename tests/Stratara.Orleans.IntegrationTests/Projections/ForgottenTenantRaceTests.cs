using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.ForgottenTenants;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// Two deliveries of one projection forget the same tenant at once — the two deletion facts of a tenant arrive in two
/// bundles. On PostgreSQL the tenant is recorded once, and neither writer leaves an error in the log, whatever order
/// each names the tenants in.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ForgottenTenantRaceTests(PostgreSqlFixture postgres)
{
    private const string Database = "poc_forgotten_tenant_race";

    [Fact]
    public async Task A_tenant_another_writer_records_first_is_forgotten_without_an_error()
    {
        var plain = await PrepareAsync();
        var projection = $"race-{Guid.NewGuid():N}";
        var tenant = Guid.CreateVersion7();
        var logs = new LogCapture();
        var store = new ForgottenTenantStore<PocReadDbContext>(new ContextFactory(ConnectionString, logs, new OtherWriterFirst(plain, projection, tenant)));

        await store.ForgetAsync(projection, [tenant], TestContext.Current.CancellationToken);

        Assert.Equal([tenant], await ForgottenAsync(plain, projection));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Writers_forgetting_the_same_tenants_at_once_record_each_once_without_an_error()
    {
        var plain = await PrepareAsync();
        var projection = $"race-{Guid.NewGuid():N}";
        var tenants = Enumerable.Range(0, 25).Select(_ => Guid.CreateVersion7()).ToList();
        var logs = new LogCapture();
        var store = new ForgottenTenantStore<PocReadDbContext>(new ContextFactory(ConnectionString, logs));

        // Each writer names the tenants in an order of its own, as two deletion facts listing the same tenants may.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(writer =>
            store.ForgetAsync(projection, tenants.OrderBy(tenant => HashCode.Combine(writer, tenant)).ToList(), TestContext.Current.CancellationToken)));

        Assert.Equal(tenants.Order(), (await ForgottenAsync(plain, projection)).Order());
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
    }

    private string ConnectionString => postgres.ConnectionStringFor(Database);

    private async Task<ContextFactory> PrepareAsync()
    {
        await PostgresTimerHostSchema.EnsureDatabaseAsync(ConnectionString);
        var plain = new ContextFactory(ConnectionString);
        await using var context = plain.CreateDbContext();
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return plain;
    }

    private static async Task<List<Guid>> ForgottenAsync(ContextFactory factory, string projection)
    {
        await using var context = factory.CreateDbContext();
        return await context.Set<ForgottenTenant>().AsNoTracking()
            .Where(forgotten => forgotten.Projection == projection)
            .Select(forgotten => forgotten.TenantId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Records the tenant through another context just before the store's own insert runs.</summary>
    private sealed class OtherWriterFirst(ContextFactory other, string projection, Guid tenant) : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await WriteFirstAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await WriteFirstAsync(command, cancellationToken);
            return result;
        }

        private async Task WriteFirstAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (command.CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await using var context = other.CreateDbContext();
                context.Set<ForgottenTenant>().Add(new ForgottenTenant { Projection = projection, TenantId = tenant });
                await context.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private sealed class ContextFactory(string connectionString, ILoggerProvider? logs = null, params IInterceptor[] interceptors) : IDbContextFactory<PocReadDbContext>
    {
        private readonly ILoggerFactory? _loggerFactory = logs is null ? null : LoggerFactory.Create(builder => builder.AddProvider(logs));

        public PocReadDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PocReadDbContext>()
                .UseSnakeCaseNamingConvention()
                .UseNpgsql(connectionString)
                .AddInterceptors(interceptors);
            if (_loggerFactory is not null)
            {
                options.UseLoggerFactory(_loggerFactory);
            }

            return new PocReadDbContext(options.Options);
        }
    }

    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, int EventId, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(Entries);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<(LogLevel Level, int EventId, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, eventId.Id, formatter(state, exception)));
        }
    }
}
