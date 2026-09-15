using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Session;
using Stratara.Abstractions.Timers;
using Stratara.Contracts.Session;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Sagas;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Sagas.Abstractions;
using Stratara.Abstractions.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;

namespace Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

/// <summary>
/// A saga host with one stateful process whose timeout a kill must not lose. Commands:
/// <c>start streamId</c> creates the counter that starts the process; <c>expired streamId timeoutMs</c>
/// waits for the process's own stream to say it expired; <c>expirations streamId</c> counts the expiries it
/// records; <c>timers streamId</c> counts the process's timers; <c>hold-registrations</c> makes every later
/// timer registration wait once it is stored, so a kill lands between a step's timers and its append.
/// </summary>
public sealed class SagaScenario : IPocScenario
{
    public async Task<IHost> BuildAsync(PocHostSettings settings)
    {
        await PocSilo.EnsureSchemaAsync(settings.OrleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = settings.StoreConnectionString,
            ["ConnectionStrings:rabbitmq"] = settings.RabbitConnectionString,
        });
        builder.UseOrleans(silo => settings.ConfigureSilo(silo));
        builder.AddSagaServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(settings.ReadStoreConnectionString)
            .AddAggregatesFromAssemblyContaining<SagaScenario>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<TimeoutProcessState>()
            .AddTrustedType<ProcessStarted>()
            .AddTrustedType<ProcessExpired>()
            .AddSingleton(TimeProvider.System)
            .AddScoped<ISaga, TimeoutSaga>()
            .Configure<CommitOrderOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
            .AddStrataraSagaGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(2);
                if (settings.Profile == PocSiloProfile.Test)
                {
                    // Below the production minimum reminder period, which the test profile lowers.
                    options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
                }
            })
            .Configure<Stratara.Orleans.Timers.DurableTimerOptions>(options => options.RetryPeriod = settings.Profile == PocSiloProfile.Test ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1));
        HoldingTimers.Decorate(builder.Services);

        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
            }

            await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await read.Database.EnsureCreatedAsync();
        }

        return host;
    }

    public async Task<string> HandleAsync(IServiceProvider services, string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "start":
            {
                var streamId = Guid.Parse(parts[1]);
                await using var scope = services.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
                var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
                await events.CreateAsync<Counter>(streamId, new CounterCreated(streamId));
                await events.SaveChangesAsync();
                return "ok";
            }
            case "expired":
            {
                var streamId = Guid.Parse(parts[1]);
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(int.Parse(parts[2], CultureInfo.InvariantCulture));
                var stateStream = Stratara.Orleans.Sagas.SagaProcessKey.StateStreamOf(nameof(TimeoutSaga), streamId);
                while (true)
                {
                    await using var scope = services.CreateAsyncScope();
                    var state = await scope.ServiceProvider.GetRequiredService<IEventSource>().ExistsAsync(stateStream)
                        ? await scope.ServiceProvider.GetRequiredService<IAggregationService>().AggregateAsync<TimeoutProcessState>(stateStream)
                        : null;
                    if (state is { Expired: true })
                    {
                        return "true";
                    }

                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        return state is null ? "no-process" : "false";
                    }

                    await Task.Delay(250);
                }
            }
            case "expirations":
            {
                var stateStream = Stratara.Orleans.Sagas.SagaProcessKey.StateStreamOf(nameof(TimeoutSaga), Guid.Parse(parts[1]));
                await using var scope = services.CreateAsyncScope();
                var state = await scope.ServiceProvider.GetRequiredService<IEventSource>().ExistsAsync(stateStream)
                    ? await scope.ServiceProvider.GetRequiredService<IAggregationService>().AggregateAsync<TimeoutProcessState>(stateStream)
                    : null;
                return (state?.Expirations ?? 0).ToString(CultureInfo.InvariantCulture);
            }
            case "timers":
            {
                var owner = Stratara.Orleans.Sagas.SagaProcessTimerHost.OwnerOf(Stratara.Orleans.Sagas.SagaProcessKey.Of(nameof(TimeoutSaga), Guid.Parse(parts[1])));
                return (await services.GetRequiredService<IDurableTimers>().ListAsync(owner)).Count.ToString(CultureInfo.InvariantCulture);
            }
            case "hold-registrations":
                services.GetRequiredService<TimerRegistrationHold>().Active = true;
                return "ok";
            default:
                return "error unknown command " + parts[0];
        }
    }
}
