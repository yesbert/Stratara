using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Singleton;

namespace Stratara.Orleans.Singleton;

/// <summary>
/// The singleton works a composition registered, each with the name it was registered under where one was given —
/// so the silo can publish a named work without constructing it, and check the name once the work is constructed —
/// and with the settings its later registration gave, which apply to that work alone.
/// </summary>
internal sealed class SingletonWorkRegistrations
{
    private readonly Dictionary<Type, string?> _works = [];
    private readonly Dictionary<Type, Action<SingletonWorkOptions>?> _settings = [];

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

    /// <summary>
    /// Records a work; a second registration without a name keeps the name of the first, and its settings replace
    /// those of the first — the work runs with the settings of its later registration, once.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The work is already registered under another name, or another work is registered under this one.
    /// </exception>
    public void Add(Type workType, string? name, Action<SingletonWorkOptions>? configure)
    {
        if (name is not null && _works.FirstOrDefault(other => other.Key != workType && string.Equals(other.Value, name, StringComparison.Ordinal)).Key is { } taken)
        {
            throw new InvalidOperationException(
                $"The singleton work {taken} is already registered under the name '{name}', and {workType} asks for it too. One name is one work — it names the grain that runs it — so only the first would ever run.");
        }

        if (_works.TryGetValue(workType, out var registered) && registered is not null
            && name is not null && !string.Equals(name, registered, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The singleton work {workType} is registered under the name '{registered}' and again under '{name}'. A work runs under one name, its ISingletonWork.Name; register it once with that name.");
        }

        _works[workType] = registered ?? name;
        _settings[workType] = configure;
    }

    /// <summary>The settings callback of <paramref name="workType"/>'s later registration, or <see langword="null"/> when it gave none.</summary>
    public Action<SingletonWorkOptions>? ConfigureOf(Type workType) => _settings.GetValueOrDefault(workType);

    /// <summary>
    /// The settings <paramref name="workType"/> runs with: a copy of the host's settings for singleton work as a whole
    /// with the work's own callback applied on top, so a work's settings never reach another work.
    /// </summary>
    /// <param name="workType">The registered work.</param>
    /// <param name="siloWide">The host's settings for every singleton work.</param>
    public SingletonWorkOptions SettingsOf(Type workType, SingletonWorkOptions siloWide)
    {
        var settings = new SingletonWorkOptions { KeepAlivePeriod = siloWide.KeepAlivePeriod };
        ConfigureOf(workType)?.Invoke(settings);
        return settings;
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
