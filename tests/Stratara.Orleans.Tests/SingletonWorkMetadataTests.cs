using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Singleton;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A work registered with its name is published without being constructed; one registered without it is
/// constructed to read the name, and a failure there names the work and why it was constructed. A work whose name
/// differs from its registered one is refused naming both, and so are two works that ask for one name.
/// </summary>
public sealed class SingletonWorkMetadataTests
{
    [Fact]
    public void Two_works_registered_under_one_name_are_refused_naming_both()
    {
        var services = new ServiceCollection().AddStrataraSingletonWork<UnconstructableWork>("the-one-name");

        var refused = Assert.Throws<InvalidOperationException>(() => services.AddStrataraSingletonWork<OtherWork>("the-one-name"));

        Assert.Contains(nameof(UnconstructableWork), refused.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(OtherWork), refused.Message, StringComparison.Ordinal);
        Assert.Contains("the-one-name", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_work_is_published_without_being_constructed()
    {
        var services = new ServiceCollection().AddStrataraSingletonWork<UnconstructableWork>(UnconstructableWork.WorkName);
        var metadata = new SingletonWorkPlacement.SingletonWorkSiloMetadata();
        using var provider = services.BuildServiceProvider();

        metadata.Fill(provider.GetRequiredService<IServiceScopeFactory>());

        Assert.Contains(SingletonWorkPlacement.MetadataKeyOf(UnconstructableWork.WorkName), metadata.Entries.Keys);
    }

    [Fact]
    public void An_unnamed_work_is_constructed_to_be_published()
    {
        var services = new ServiceCollection().AddStrataraSingletonWork<NamedWork>();
        var metadata = new SingletonWorkPlacement.SingletonWorkSiloMetadata();
        using var provider = services.BuildServiceProvider();

        metadata.Fill(provider.GetRequiredService<IServiceScopeFactory>());

        Assert.Contains(SingletonWorkPlacement.MetadataKeyOf(NamedWork.WorkName), metadata.Entries.Keys);
    }

    [Fact]
    public void An_unnamed_work_that_cannot_be_constructed_fails_naming_its_type_and_the_named_registration()
    {
        var services = new ServiceCollection().AddStrataraSingletonWork<UnconstructableWork>();
        var metadata = new SingletonWorkPlacement.SingletonWorkSiloMetadata();
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<InvalidOperationException>(() => metadata.Fill(provider.GetRequiredService<IServiceScopeFactory>()));

        Assert.Contains(nameof(UnconstructableWork), failure.Message, StringComparison.Ordinal);
        Assert.Contains("with its name", failure.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(failure.InnerException?.InnerException ?? failure.InnerException);
    }

    [Fact]
    public void A_work_whose_name_differs_from_the_registered_one_is_refused_naming_both()
    {
        var services = new ServiceCollection().AddStrataraSingletonWork<NamedWork>("registered-name");
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<SingletonWorkRegistrations>().EnsureNamed(new NamedWork()));

        Assert.Contains("registered-name", failure.Message, StringComparison.Ordinal);
        Assert.Contains(NamedWork.WorkName, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_work_registered_under_its_name_passes_the_check_and_a_later_unnamed_registration_keeps_the_name()
    {
        var services = new ServiceCollection()
            .AddStrataraSingletonWork<NamedWork>(NamedWork.WorkName)
            .AddStrataraSingletonWork<NamedWork>();
        using var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<SingletonWorkRegistrations>();

        registrations.EnsureNamed(new NamedWork());

        Assert.Equal(NamedWork.WorkName, registrations.NameOf(typeof(NamedWork)));
    }

    [Fact]
    public void A_work_registered_under_two_names_is_refused()
    {
        var services = new ServiceCollection().AddStrataraSingletonWork<NamedWork>(NamedWork.WorkName);

        var failure = Assert.Throws<InvalidOperationException>(() => services.AddStrataraSingletonWork<NamedWork>("another"));

        Assert.Contains("another", failure.Message, StringComparison.Ordinal);
    }

    public sealed class NamedWork : ISingletonWork
    {
        public const string WorkName = "named";

        public string Name => WorkName;

        public TimeSpan Period => TimeSpan.FromMinutes(1);

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class UnconstructableWork : ISingletonWork
    {
        public const string WorkName = "unconstructable";

        public UnconstructableWork() => throw new InvalidOperationException("Needs the running host.");

        public string Name => WorkName;

        public TimeSpan Period => TimeSpan.FromMinutes(1);

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A second work, for the case of two works asking for one name.</summary>
    public sealed class OtherWork : ISingletonWork
    {
        public string Name => "other";

        public TimeSpan Period => TimeSpan.FromMinutes(1);

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
