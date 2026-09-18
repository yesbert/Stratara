using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime.Hosting;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Abstractions.Timers;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.Projections;
using Stratara.Orleans.Sagas;
using Stratara.Orleans.Singleton;
using Stratara.Orleans.Timers;
using Stratara.Testing.EntityFrameworkCore;

namespace Stratara.Testing.Orleans;

/// <summary>
/// Runs the Orleans execution model in the test's own process: one silo clustered with itself on free ports, reminders
/// and the grain directory in memory, the framework's real write stack, portable commit-order reader, checkpoint store
/// and intent store on an in-memory SQLite database, and every period the model keeps as a reminder or a poll shortened
/// to seconds. A test registers its handlers, projections, sagas, timer ports and the model's roles with the calls it
/// uses in production, dispatches, waits for the readers and asserts — without a cluster, a broker or a database
/// server.
/// </summary>
/// <remarks>
/// <para>
/// The host is one silo. What happens across silos — a kill, a role split, a takeover — is an integration test against
/// real infrastructure. Creating a host takes a few seconds; share one across the tests of a class through a fixture,
/// and reset it between them with <see cref="ResetAsync"/> where a test must not see another's state.
/// </para>
/// <para>
/// The host registers the mediator, the projection and saga runtimes, the outbox drain as singleton work, and the
/// stores; the roles are the test's to register — <c>AddStrataraAggregateGrains</c>, <c>AddStrataraProjectionGrains</c>,
/// <c>AddStrataraSagaGrains</c>, <c>AddStrataraDurableTimers</c>, <c>AddStrataraOrleansCommandDispatcher</c>. Each scope
/// the host or the silo creates has a session provider of its own, preset to <see cref="Session"/>'s context.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using var host = await ExecutionModelTestHost.CreateAsync(services => services
///     .AddAggregatesFromAssemblyContaining&lt;IAppMarker&gt;()
///     .AddCommandHandlersFromAssemblyContaining&lt;IAppMarker&gt;()
///     .AddProjectionsFromAssemblyContaining&lt;IAppMarker&gt;()
///     .AddStrataraAggregateGrains()
///     .AddStrataraProjectionGrains());
///
/// await host.DispatchAsync(new DepositCommand(Guid.NewGuid(), 100m));
/// await host.WaitForReadersAsync();
/// </code>
/// </example>
public sealed class ExecutionModelTestHost : IAsyncDisposable
{
    /// <summary>The tenant the host's default session context is scoped to.</summary>
    public static readonly Guid DefaultTenantId = EventStoreTestHost.DefaultTenantId;

    private const string EntryPoint = nameof(ExecutionModelTestHost) + "." + nameof(CreateAsync);

    private readonly IHost _host;
    private readonly SqliteConnection _keeper;

    private ExecutionModelTestHost(IHost host, SqliteConnection keeper, TestSessionContextProvider session)
    {
        _host = host;
        _keeper = keeper;
        Session = session;
    }

    /// <summary>The host's root service provider, for resolving services in a scope.</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// The session every scope of the host starts under — the host's, the silo's grains' and the store readers'. Set its
    /// context to dispatch as another tenant or user; a scope already created keeps the context it started with.
    /// </summary>
    public TestSessionContextProvider Session { get; }

    /// <summary>The durable timers. Requires <c>AddStrataraDurableTimers</c> among the test's registrations.</summary>
    /// <exception cref="InvalidOperationException">The test registered no durable timers.</exception>
    public IDurableTimers Timers => Services.GetService<IDurableTimers>()
                                    ?? throw new InvalidOperationException("No durable timers are registered on the test host. Register them in CreateAsync's configure with AddStrataraDurableTimers(), together with an ITimerOwners and an ITimerHandler.");

    /// <summary>Creates and starts a host.</summary>
    /// <param name="configure">
    /// The test's registrations — aggregates, handlers, projections, sagas, timer ports and the execution model's roles —
    /// made with the calls production uses.
    /// </param>
    /// <param name="options">The periods the host runs with, or <see langword="null"/> for the shortened defaults.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>A started host with its schema created.</returns>
    /// <exception cref="InvalidOperationException">
    /// An environment other than <c>Development</c> is stated through <c>DOTNET_ENVIRONMENT</c> or
    /// <c>ASPNETCORE_ENVIRONMENT</c>; where none is stated — an ordinary test — the host is created.
    /// </exception>
    public static async Task<ExecutionModelTestHost> CreateAsync(
        Action<IServiceCollection>? configure = null,
        Action<ExecutionModelTestHostOptions>? options = null,
        CancellationToken cancellationToken = default)
    {
        TestSupportEnvironmentGuard.EnsureDevelopmentOrUnstated(
            new ServiceCollection(), EntryPoint, TestSupportEnvironmentGuard.ExecutionModelWiring, Environment.GetEnvironmentVariable);

        var settings = new ExecutionModelTestHostOptions();
        options?.Invoke(settings);

        // Hosts created at the same moment can be handed the same free port, because the port is free until the silo
        // binds it. Such a start is tried again from the beginning — a database of its own, ports of its own.
        var attempt = 1;
        while (true)
        {
            try
            {
                return await CreateOnceAsync(settings, configure, cancellationToken);
            }
            catch (Exception failure) when (attempt < 3 && TakenPort(failure))
            {
                attempt++;
            }
        }
    }

    /// <summary>Whether the failure is a port another host bound first.</summary>
    private static bool TakenPort(Exception failure)
    {
        for (var inner = failure; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }

            if (inner is AggregateException aggregate && aggregate.InnerExceptions.Any(TakenPort))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<ExecutionModelTestHost> CreateOnceAsync(
        ExecutionModelTestHostOptions settings,
        Action<IServiceCollection>? configure,
        CancellationToken cancellationToken)
    {
        var connectionString = $"Data Source=stratara-execution-model-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync(cancellationToken);
        try
        {
            var session = new TestSessionContextProvider(TestSessionContext.ForTenant(DefaultTenantId));
            var host = Build(connectionString, settings, session, configure);
            try
            {
                var testHost = new ExecutionModelTestHost(host, keeper, session);
                await CreateSchemaAsync(host.Services, settings.PartitionCount, cancellationToken);
                if (settings.BeforeStart is { } beforeStart)
                {
                    await beforeStart(testHost);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(settings.StartTimeout);
                await host.StartAsync(timeout.Token);
                return testHost;
            }
            catch
            {
                // What the host built holds ports, threads, timers and database connections until it is stopped, and
                // the noise it goes on making would bury the failure the test is about to see.
                await StopQuietlyAsync(host);
                throw;
            }
        }
        catch
        {
            await keeper.DisposeAsync();
            throw;
        }
    }

    /// <summary>Dispatches <paramref name="command"/> through the mediator in a scope of its own, under <see cref="Session"/>.</summary>
    /// <typeparam name="TCommand">The command type.</typeparam>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancels the dispatch.</param>
    /// <returns>A task that completes when the command's handler has run — in its aggregate's activation where the test registered the aggregate grains.</returns>
    public async Task DispatchAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : class, Stratara.Abstractions.Mediator.IRequest
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().HandleAsync(command, cancellationToken);
    }

    /// <summary>Seeds every registered store reader's checkpoints at the store's head, through the execution model's seeding.</summary>
    /// <param name="cancellationToken">Cancels the seeding.</param>
    /// <returns>How many checkpoints were seeded and how many existed.</returns>
    public async Task<StoreReaderSeedingReport> SeedAtHeadAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IStoreReaderSeeding>().SeedAtHeadAsync(cancellationToken);
    }

    /// <summary>
    /// Forgets what the execution model keeps on this host: its timers and the grain directory's entries go, and every
    /// registered store reader is put at the store's head — the host keeps running, and a reader returned to the
    /// beginning would read the store again into read models this does not empty.
    /// </summary>
    /// <remarks>
    /// This is the host's reset, not a rehearsal of the deployment's: a deployment resets while nothing runs and its
    /// readers start again from nothing. Between two tests of one host, "nothing from before" means the head.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the reset.</param>
    /// <returns>What was removed, and how many checkpoints were moved.</returns>
    /// <exception cref="InvalidOperationException">A store reader could not be started again and reads nothing.</exception>
    public async Task<ExecutionModelResetReport> ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IExecutionModelReset>().ResetAsync(cancellationToken);
    }

    /// <summary>
    /// Returns once every registered store reader has applied the store up to its head in every partition — the moment a
    /// test asserts on a read model or on what a saga did.
    /// </summary>
    /// <param name="timeout">How long to wait; ten seconds when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the readers have caught up.</returns>
    /// <exception cref="TimeoutException">A reader has not caught up in time; the message names it, its partition and both positions.</exception>
    public async Task WaitForReadersAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var lagging = await FirstLaggingAsync(cancellationToken);
            if (lagging is null)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"The store reader '{lagging.Value.Consumer}' has applied partition {lagging.Value.Partition} up to position {lagging.Value.Checkpoint}, and the store's head there is {lagging.Value.Head}. A projection or saga that throws stops its partition; the host's log says which entry.");
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.StopAsync();
        }
        finally
        {
            _host.Dispose();
            await _keeper.DisposeAsync();
        }
    }

    private async Task<(string Consumer, int Partition, long Checkpoint, long Head)?> FirstLaggingAsync(CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var consumers = scope.ServiceProvider.GetServices<INudgeTarget>().SelectMany(target => target.ConsumerNames).Distinct(StringComparer.Ordinal).ToList();
        if (consumers.Count == 0)
        {
            return null;
        }

        var reader = scope.ServiceProvider.GetRequiredService<ICommittedPositionReader>();
        var checkpoints = scope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>();
        var partitions = scope.ServiceProvider.GetRequiredService<IOptions<CommitOrderOptions>>().Value.PartitionCount;
        for (var partition = 0; partition < partitions; partition++)
        {
            var head = await reader.HeadAsync(partition, cancellationToken);
            foreach (var consumer in consumers)
            {
                var checkpoint = await checkpoints.GetAsync(consumer, partition, reader.Name, cancellationToken);
                if (checkpoint < head)
                {
                    return (consumer, partition, checkpoint, head);
                }
            }
        }

        return null;
    }

    private static IHost Build(string connectionString, ExecutionModelTestHostOptions settings, TestSessionContextProvider session, Action<IServiceCollection>? configure)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(ExecutionModelTestHost).Assembly.GetName().Name,
        });

        var directory = new InMemoryGrainDirectory();
        var clusterId = $"stratara-test-{Guid.NewGuid():N}";
        var siloPort = FreePort();
        var gatewayPort = FreePort();
        builder.UseOrleans(silo =>
        {
            silo.UseLocalhostClustering(siloPort, gatewayPort, serviceId: clusterId, clusterId: clusterId);
            silo.UseInMemoryReminderService();
            silo.Configure<ReminderOptions>(reminders => reminders.MinimumReminderPeriod = settings.MinimumReminderPeriod);
            silo.AddStrataraOrleans((s, name) => s.AddGrainDirectory(name, (_, _) => directory));
        });

        var services = builder.Services;
        services.AddLogging();
        services.AddScoped<ISessionContextProvider>(_ => new TestSessionContextProvider(session.Current));
        var commitOrder = Options.Create(new CommitOrderOptions { PartitionCount = settings.PartitionCount });
        services.AddStrataraTestingEventStore<StrataraTestWriteDbContext>(
            connectionString,
            DefaultTenantId,
            context => context.AddInterceptors(new PartitionCounterInterceptor(commitOrder)));
        services.AddDbContextFactory<StrataraTestReadDbContext>((_, context) => context.UseSqlite(connectionString), ServiceLifetime.Scoped);

        services.AddMediator();
        services.AddResiliencePipelines();
        services.AddProjectionReplayState();
        services.AddProjectionHandling(builder.Configuration);
        services.AddSagaHandling(builder.Configuration);
        services.AddStrataraPortableCounterReader<StrataraTestWriteDbContext>();
        services.AddStrataraProjectionCheckpoints<StrataraTestReadDbContext>();
        services.AddStrataraIntentStore<StrataraTestWriteDbContext>();
        services.AddStrataraSingletonWork<OutboxDrainWork>(OutboxDrainWork.WorkName);
        services.AddSingleton(directory);
        services.AddScoped<IExecutionModelReset, InMemoryExecutionModelReset>();
        Shorten(services, settings);

        configure?.Invoke(services);
        return builder.Build();
    }

    /// <summary>
    /// Applies the host's periods where a setting still holds the framework's default, so a value the test set through a
    /// registration wins. The partition count is the host's in every case, because the counter interceptor is built with it.
    /// </summary>
    private static void Shorten(IServiceCollection services, ExecutionModelTestHostOptions settings)
    {
        var projection = new ProjectionGrainOptions();
        services.PostConfigure<ProjectionGrainOptions>(o =>
        {
            o.PollInterval = Keep(o.PollInterval, projection.PollInterval, settings.PollInterval);
            o.KeepAlivePeriod = Keep(o.KeepAlivePeriod, projection.KeepAlivePeriod, settings.ReminderPeriod);
        });
        var saga = new SagaGrainOptions();
        services.PostConfigure<SagaGrainOptions>(o =>
        {
            o.PollInterval = Keep(o.PollInterval, saga.PollInterval, settings.PollInterval);
            o.KeepAlivePeriod = Keep(o.KeepAlivePeriod, saga.KeepAlivePeriod, settings.ReminderPeriod);
        });
        var timers = new DurableTimerOptions();
        services.PostConfigure<DurableTimerOptions>(o => o.RetryPeriod = Keep(o.RetryPeriod, timers.RetryPeriod, settings.ReminderPeriod));
        var singleton = new SingletonWorkOptions();
        services.PostConfigure<SingletonWorkOptions>(o => o.KeepAlivePeriod = Keep(o.KeepAlivePeriod, singleton.KeepAlivePeriod, settings.ReminderPeriod));
        var dispatch = new OrleansDispatchOptions();
        services.PostConfigure<OrleansDispatchOptions>(o => o.IntentGrace = Keep(o.IntentGrace, dispatch.IntentGrace, settings.IntentGrace));
        var drain = new OutboxDrainOptions();
        services.PostConfigure<OutboxDrainOptions>(o => o.PollingInterval = Keep(o.PollingInterval, drain.PollingInterval, settings.DrainPollingInterval));
        services.PostConfigure<CommitOrderOptions>(o => o.PartitionCount = settings.PartitionCount);
    }

    private static TimeSpan Keep(TimeSpan current, TimeSpan frameworkDefault, TimeSpan hostValue) =>
        current == frameworkDefault ? hostValue : current;

    private static async Task CreateSchemaAsync(IServiceProvider services, int partitionCount, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<StrataraTestWriteDbContext>>().CreateDbContextAsync(cancellationToken))
        {
            await write.Database.EnsureCreatedAsync(cancellationToken);
            write.Set<PartitionPosition>().AddRange(Enumerable.Range(0, partitionCount).Select(partition => new PartitionPosition { Partition = partition }));
            await write.SaveChangesAsync(cancellationToken);
        }

        await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<StrataraTestReadDbContext>>().CreateDbContextAsync(cancellationToken);
        await read.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(cancellationToken);
    }

    private static async Task StopQuietlyAsync(IHost host)
    {
        try
        {
            await host.StopAsync();
        }
        catch (Exception stopping)
        {
            // Whatever the stop ends with, including its own timeout: the failure the caller is about to see is the
            // one that made the start fail, not this.
            _ = stopping;
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>
    /// A port the operating system is not using. Two hosts created at the same moment can be handed the same one —
    /// the listener is closed before the silo binds — so a start that fails on the address is tried again with
    /// another port.
    /// </summary>
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
