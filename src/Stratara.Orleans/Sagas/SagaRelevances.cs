using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.Sagas;

/// <summary>
/// The events each saga the host registers applies, by its consumer, as its reader decides them, which decide whether a
/// commit wakes the saga's readers: the types a stateless saga declares a handler for, or any resolvable type for a
/// stateful process, which decides from the event itself. Worked out once, the first time they are asked for, by
/// building the sagas in a scope of their own. Where they cannot be built there — a saga that needs a session to be
/// constructed — none of them has a relevance, and every commit wakes every saga, as before; the answer is kept either
/// way, so a commit never pays for building the sagas.
/// </summary>
internal sealed class SagaRelevances(IServiceScopeFactory scopeFactory, SagaRegistrations registrations)
{
    private readonly Lock _gate = new();
    private Dictionary<string, EventRelevance>? _byConsumer;

    /// <summary>The events the saga that reads under <paramref name="consumer"/> applies, or <see langword="null"/> where no saga reads under it.</summary>
    public EventRelevance? RelevanceOf(string consumer) => (Volatile.Read(ref _byConsumer) ?? Read()).GetValueOrDefault(consumer);

    private Dictionary<string, EventRelevance> Read()
    {
        lock (_gate)
        {
            if (_byConsumer is { } known)
            {
                return known;
            }

            var byConsumer = Build();
            Volatile.Write(ref _byConsumer, byConsumer);
            return byConsumer;
        }
    }

    private Dictionary<string, EventRelevance> Build()
    {
        var byConsumer = new Dictionary<string, EventRelevance>(StringComparer.Ordinal);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ISagaHandler>();
            var sagas = scope.ServiceProvider.GetServices<ISaga>().ToList();
            foreach (var consumer in registrations.Consumers)
            {
                var type = registrations.TypeOf(consumer[SagaReaderGrain.ConsumerPrefix.Length..]);
                if (sagas.FirstOrDefault(saga => saga.GetType() == type) is { } saga)
                {
                    byConsumer[consumer] = saga is ISagaProcess
                        ? EventRelevance.AnyResolvableWith(handler.GetRelevantEventTypes(saga))
                        : EventRelevance.ForTypes(handler.GetRelevantEventTypes(saga));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            byConsumer.Clear();
        }

        return byConsumer;
    }
}
