using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Abstractions.Validation;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;
using Stratara.Orleans.IntegrationTests.Projections;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// Task 4.5: a recorded command runs through the mediator pipeline in its grain. A command that fails
/// validation is not handled, and the validation failure is what the intent records; a valid one is handled.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class IntentPipelineTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const string Database = "poc_intent_pipeline";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_recorded_command_that_fails_validation_is_not_handled()
    {
        var store = postgres.ConnectionStringFor(Database);
        using var app = await StartAsync(store, siloPort: 11199, gatewayPort: 30089);
        var invalid = Guid.NewGuid();
        var valid = Guid.NewGuid();

        await EnqueueAsync(app.Services, new RecordValidated(invalid, Valid: false));
        await EnqueueAsync(app.Services, new RecordValidated(valid, Valid: true));

        var applied = app.Services.GetRequiredService<AppliedTable>();
        Assert.True(await WaitUntilAsync(() => applied.IsAppliedAsync(valid)), "the valid command was not handled");
        Assert.True(
            await WaitUntilAsync(async () => (await LastFailureAsync(store, invalid))?.Contains(nameof(StrataraValidationException), StringComparison.Ordinal) == true),
            $"the invalid command's intent did not record the validation failure; it recorded '{await LastFailureAsync(store, invalid)}'");
        Assert.False(await applied.IsAppliedAsync(invalid));

        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(string store, int siloPort, int gatewayPort)
    {
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = store,
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddBackendServices();
        builder.Services
            .AddStrataraValidation()
            .AddScoped<IValidator<RecordValidated>, RecordValidatedValidator>()
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<RecordValidated>, RecordValidatedHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<RecordValidated>()
            .AddSingleton(new AppliedTable(store))
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher()
            .AddStrataraIntentStore<PocWriteDbContext>();

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        await AppliedTable.EnsureSchemaAsync(store);
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task EnqueueAsync(IServiceProvider services, RecordValidated command)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
        await scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(command);
    }

    private static async Task<string?> LastFailureAsync(string store, Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(store);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT last_failure FROM outbox_entry WHERE aggregate_id = @id", connection);
        command.Parameters.AddWithValue("id", aggregateId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }
}

public sealed record RecordValidated(Guid AggregateId, bool Valid) : ICommand, IAggregateScopedCommand;

public sealed class RecordValidatedValidator : IValidator<RecordValidated>
{
    public ValueTask<ValidationResult> ValidateAsync(RecordValidated instance, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(instance.Valid
            ? ValidationResult.Success
            : new ValidationResult([new ValidationFailure(nameof(RecordValidated.Valid), "the probe command is marked invalid")]));
}

public sealed class RecordValidatedHandler(AppliedTable applied) : ICommandHandler<RecordValidated>
{
    public Task HandleAsync(RecordValidated command, CancellationToken cancellationToken) => applied.MarkAsync(command.AggregateId);
}
