using Orleans.Hosting;
using Stratara.Orleans;
using Stratara.Orleans.Hosting;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of what the Orleans execution model needs on the silo itself.</summary>
public static class StrataraOrleansSiloBuilderExtensions
{
    /// <summary>
    /// Registers the storage-backed grain directory the execution model's single-activation grains —
    /// store readers, singleton work, timer owners, stateful processes and the heavy-work permits — are
    /// placed in, under the name the model selects, and checks at start that one is registered. Binds no
    /// configuration. The directory's backend is the host's choice: <paramref name="addDurableDirectory"/>
    /// receives the silo and the name and registers any directory under it. A silo that runs the model's
    /// grains without a directory under the name fails to start with a message naming this registration.
    /// </summary>
    /// <param name="silo">The silo builder.</param>
    /// <param name="addDurableDirectory">Registers a storage-backed grain directory under the name it is given.</param>
    /// <returns>The same silo builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="silo"/> or <paramref name="addDurableDirectory"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// var orleansDb = builder.Configuration.GetConnectionString("orleans")!;
    /// var redis = StackExchange.Redis.ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("redis")!);
    ///
    /// builder.UseOrleans(silo => silo
    ///     .UseAdoNetClustering(options => { options.Invariant = "Npgsql"; options.ConnectionString = orleansDb; })
    ///     .AddStrataraOrleans((s, name) => s.AddRedisGrainDirectory(name, options => options.ConfigurationOptions = redis)));
    /// </code>
    /// </example>
    public static ISiloBuilder AddStrataraOrleans(this ISiloBuilder silo, Action<ISiloBuilder, string> addDurableDirectory)
    {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(addDurableDirectory);

        addDurableDirectory(silo, GrainDirectories.Durable);
        DurableDirectoryCheck.Register(silo.Services);
        SiloStopSignal.Register(silo.Services);
        Stratara.Orleans.Singleton.SingletonWorkPlacement.Register(silo);
        return silo;
    }
}
