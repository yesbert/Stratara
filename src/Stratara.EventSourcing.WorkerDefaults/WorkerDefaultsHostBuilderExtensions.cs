using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Worker-defaults composites that wire the Stratara event-sourced stack into a host with a single call.
/// Each method chains together the per-concern DI extensions (mediator, write store, event sourcing, outbox,
/// projections, sagas, identity, session, messaging, resilience) appropriate for one host shape.
/// </summary>
public static class WorkerDefaultsHostBuilderExtensions
{
    /// <summary>
    /// Registers the API-host stack: common framework services (messaging, identity, session, security,
    /// mapping, resilience) + mediator + write store + outbox dispatcher. For hosts that dispatch commands
    /// via the outbox but do not process them.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <example>
    /// Compose an API host that enqueues commands via the outbox without running handlers in-process:
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddBackendServices();
    /// builder.Services
    ///     .AddQueryHandlersFromAssemblyContaining&lt;IAppMarker&gt;();
    ///
    /// var app = builder.Build();
    /// app.MapPost("/orders", (PlaceOrder cmd, ICommandOutboxDispatcher d, CancellationToken ct) =&gt;
    ///     d.EnqueueCommandAsync(cmd, ct));
    /// app.Run();
    /// </code>
    /// </example>
    public static IHostApplicationBuilder AddBackendServices(this IHostApplicationBuilder builder)
    {
        builder.AddCommonFrameworkServices();
        builder.Services
            .AddMediator()
            .AddWriteStore(builder.Configuration)
            .AddOutboxDispatcher();

        return builder;
    }

    /// <summary>
    /// Registers the command-worker stack: common framework services + mediator + mediator-worker (hosted
    /// service that consumes the command topic into the in-process mediator) + write store + event sourcing
    /// + outbox dispatcher.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <example>
    /// Compose a worker host that processes commands enqueued by API hosts:
    /// <code>
    /// var builder = Host.CreateApplicationBuilder(args);
    /// builder.AddCommandWorkerServices();
    /// builder.Services.AddCommandHandlersFromAssemblyContaining&lt;IAppMarker&gt;();
    /// builder.Build().Run();
    /// </code>
    /// </example>
    public static IHostApplicationBuilder AddCommandWorkerServices(this IHostApplicationBuilder builder)
    {
        builder.AddCommonFrameworkServices();
        builder.Services
            .AddMediator()
            .AddMediatorWorker()
            .AddWriteStore(builder.Configuration)
            .AddEventSourcing()
            .AddOutboxDispatcher();
        return builder;
    }

    /// <summary>
    /// Registers the event-projection-worker stack: common framework services + write store + projection
    /// replay state + projection worker (hosted service).
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <example>
    /// Compose a projection-worker host that fans event bundles out to read-model projections:
    /// <code>
    /// var builder = Host.CreateApplicationBuilder(args);
    /// builder.AddEventProjectionWorkerServices();
    /// builder.Services.AddProjectionsFromAssemblyContaining&lt;IAppMarker&gt;();
    /// builder.Build().Run();
    /// </code>
    /// </example>
    public static IHostApplicationBuilder AddEventProjectionWorkerServices(this IHostApplicationBuilder builder)
    {
        builder.AddCommonFrameworkServices();
        builder.Services
            .AddWriteStore(builder.Configuration)
            .AddProjectionReplayState()
            .AddProjectionWorker(builder.Configuration);
        return builder;
    }

    /// <summary>
    /// Registers the saga-worker stack: common framework services + write store + event sourcing + outbox
    /// dispatcher + saga worker (hosted service that reacts to event bundles).
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <example>
    /// Compose a saga-worker host that orchestrates long-running workflows triggered by domain events:
    /// <code>
    /// var builder = Host.CreateApplicationBuilder(args);
    /// builder.AddSagaWorkerServices();
    /// builder.Services.AddSagasFromAssemblyContaining&lt;IAppMarker&gt;();
    /// builder.Build().Run();
    /// </code>
    /// </example>
    public static IHostApplicationBuilder AddSagaWorkerServices(this IHostApplicationBuilder builder)
    {
        builder.AddCommonFrameworkServices();
        builder.Services
            .AddWriteStore(builder.Configuration)
            .AddEventSourcing()
            .AddOutboxDispatcher()
            .AddSagaWorker(builder.Configuration);
        return builder;
    }

    /// <summary>
    /// Registers the event-stream-hash-worker stack: common framework services + write store +
    /// event-stream-hashing worker.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <example>
    /// Compose a worker host that continuously hashes event-streams to detect tampering:
    /// <code>
    /// var builder = Host.CreateApplicationBuilder(args);
    /// builder.AddEventStreamHashWorkerServices();
    /// builder.Build().Run();
    /// </code>
    /// </example>
    public static IHostApplicationBuilder AddEventStreamHashWorkerServices(this IHostApplicationBuilder builder)
    {
        builder.AddCommonFrameworkServices();
        builder.Services
            .AddWriteStore(builder.Configuration)
            .AddEventStreamHashWorker();

        return builder;
    }

    /// <summary>
    /// Registers the outbox-worker stack: common framework services + write store + outbox dispatcher +
    /// outbox-retry worker. Use in a dedicated host that owns outbox-row retries; other hosts get the
    /// dispatcher via <see cref="AddBackendServices"/> / <see cref="AddCommandWorkerServices"/>.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <example>
    /// Compose a dedicated outbox-retry host:
    /// <code>
    /// var builder = Host.CreateApplicationBuilder(args);
    /// builder.AddOutboxWorkerServices();
    /// builder.Build().Run();
    /// </code>
    /// </example>
    public static IHostApplicationBuilder AddOutboxWorkerServices(this IHostApplicationBuilder builder)
    {
        builder.AddCommonFrameworkServices();
        builder.Services
            .AddWriteStore(builder.Configuration)
            .AddOutboxDispatcher()
            .AddOutboxWorker(builder.Configuration);
        return builder;
    }

    /// <summary>
    /// Wires the cross-cutting Stratara framework services that every worker host depends on —
    /// messaging, identity, session context, security, event-mapping, and resilience pipelines.
    /// </summary>
    /// <remarks>
    /// Called internally by every <c>Add*WorkerServices</c> composite extension. Consumer hosts can
    /// also call it directly when they compose their own worker layout outside the documented
    /// composites.
    /// </remarks>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <example>
    /// Bootstrap a custom worker shape that the documented <c>Add*WorkerServices</c> composites
    /// don't cover. Add only the framework primitives the host actually needs on top:
    /// <code>
    /// var builder = Host.CreateApplicationBuilder(args);
    /// builder.AddCommonFrameworkServices();
    /// builder.Services
    ///     .AddWriteStore(builder.Configuration)
    ///     .AddEventSourcing();
    /// builder.Build().Run();
    /// </code>
    /// </example>
    public static IHostApplicationBuilder AddCommonFrameworkServices(this IHostApplicationBuilder builder)
    {
        builder.AddMessaging();
        builder.Services
            .AddIdentity()
            .AddSessionContext()
            .AddSecurity()
            .AddMapping()
            .AddResiliencePipelines();
        return builder;
    }
}
