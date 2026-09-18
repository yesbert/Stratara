using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Singleton;

namespace Stratara.Orleans.Singleton;

/// <summary>
/// The singleton works a composition registered, each with the name it was registered under where one was given —
/// so the silo can publish a named work without constructing it, and check the name once the work is constructed.
/// </summary>
internal sealed class SingletonWorkRegistrations
{
    private readonly Dictionary<Type, string?> _works = [];

    /// <summary>The registered works in registration order, with their registered names.</summary>
    public IReadOnlyList<(Type WorkType, string? Name)> Works => [.. _works.Select(work => (work.Key, work.Value))];

    /// <summary>The registrations of <paramref name="services"/>, added as a singleton the first time.</summary>
    public static SingletonWorkRegistrations Of(IServiceCollection services)
    {
        if (services.FirstOrDefault(d => d.ServiceType == typeof(SingletonWorkRegistrations))?.ImplementationInstance is SingletonWorkRegistrations existing)
        {
            return existing;
        }

        var fresh = new SingletonWorkRegistrations();
        services.AddSingleton(fresh);
        return fresh;
    }

    /// <summary>Records a work; a second registration without a name keeps the name of the first.</summary>
    /// <exception cref="InvalidOperationException">
    /// The work is already registered under another name, or another work is registered under this one.
    /// </exception>
    public void Add(Type workType, string? name)
    {
        if (name is not null && _works.FirstOrDefault(other => other.Key != workType && string.Equals(other.Value, name, StringComparison.Ordinal)).Key is { } taken)
        {
            throw new InvalidOperationException(
                $"The singleton work {taken} is already registered under the name '{name}', and {workType} asks for it too. One name is one work — it names the grain that runs it — so only the first would ever run.");
        }

        if (!_works.TryGetValue(workType, out var registered) || registered is null)
        {
            _works[workType] = name;
            return;
        }

        if (name is not null && !string.Equals(name, registered, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The singleton work {workType} is registered under the name '{registered}' and again under '{name}'. A work runs under one name, its ISingletonWork.Name; register it once with that name.");
        }
    }

    /// <summary>The name <paramref name="workType"/> was registered under, or <see langword="null"/> when none was given.</summary>
    public string? NameOf(Type workType) => _works.GetValueOrDefault(workType);

    /// <summary>
    /// Fails when a constructed work's <see cref="ISingletonWork.Name"/> is not the name it was registered under — the
    /// name the silo published and the placement looks for.
    /// </summary>
    /// <exception cref="InvalidOperationException">The names differ.</exception>
    public void EnsureNamed(ISingletonWork work)
    {
        if (NameOf(work.GetType()) is { } registered && !string.Equals(registered, work.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The singleton work {work.GetType()} was registered under the name '{registered}', but its Name is '{work.Name}'. The silo published '{registered}', so the work would be placed where nothing runs it; register it with the name its Name returns.");
        }
    }
}
