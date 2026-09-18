using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.Sagas;

/// <summary>
/// The sagas the host registers, by the name each reads under — the type name, as the saga handler gives it — worked
/// out once from the registrations rather than by constructing every saga on every commit. A saga registered by its
/// type or as an instance is named from the registration; one registered through a factory is built once, in a scope
/// of its own, to learn its type.
/// </summary>
internal sealed class SagaRegistrations
{
    private readonly Dictionary<string, Type> _byName;

    /// <exception cref="InvalidOperationException">Two saga types carry the same name and would share one checkpoint.</exception>
    public SagaRegistrations(IEnumerable<Type> sagaTypes)
    {
        var types = sagaTypes.Distinct().ToList();
        var shared = types.GroupBy(type => type.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (shared is not null)
        {
            throw new InvalidOperationException(
                $"The sagas {string.Join(", ", shared.Select(type => type.FullName))} share the name '{shared.Key}' and would share one checkpoint; give each saga a type name of its own.");
        }

        _byName = types.ToDictionary(type => type.Name, StringComparer.Ordinal);
        Consumers = [.. types.Select(type => SagaReaderGrain.ConsumerOf(type.Name))];
    }

    /// <summary>The consumer of every registered saga, once each, in registration order.</summary>
    public IReadOnlyList<string> Consumers { get; }

    /// <summary>The type of the saga registered under <paramref name="sagaName"/>, or <see langword="null"/> where none is.</summary>
    public Type? TypeOf(string sagaName) => _byName.GetValueOrDefault(sagaName);

    /// <summary>Reads the saga registrations of <paramref name="registrations"/>, building a factory-registered saga once.</summary>
    /// <exception cref="InvalidOperationException">Two saga types carry the same name.</exception>
    public static SagaRegistrations From(IEnumerable<ServiceDescriptor> registrations, IServiceProvider services)
    {
        var descriptors = registrations.Where(d => d.ServiceType == typeof(ISaga) && !d.IsKeyedService).ToList();
        var types = new List<Type>(descriptors.Count);
        IServiceScope? scope = null;
        try
        {
            foreach (var descriptor in descriptors)
            {
                if (descriptor.ImplementationType is { } type)
                {
                    types.Add(type);
                }
                else if (descriptor.ImplementationInstance is { } instance)
                {
                    types.Add(instance.GetType());
                }
                else if (descriptor.ImplementationFactory is { } factory)
                {
                    scope ??= services.CreateScope();
                    types.Add(factory(scope.ServiceProvider).GetType());
                }
            }
        }
        finally
        {
            scope?.Dispose();
        }

        return new SagaRegistrations(types);
    }

    /// <summary>
    /// Registers the registrations once, read from the finished composition the first time they are asked for, and a
    /// check that asks for them before any other hosted service starts, the silo among them.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(SagaRegistrations)))
        {
            return;
        }

        services.AddSingleton(provider => From(services, provider));
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService, SagaRegistrationCheck>());
    }
}

/// <summary>Refuses to let a host start whose sagas cannot each read under a name of their own.</summary>
internal sealed class SagaRegistrationCheck(IServiceProvider services) : IHostedService
{
    /// <exception cref="InvalidOperationException">Two saga types carry the same name.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        services.GetRequiredService<SagaRegistrations>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
