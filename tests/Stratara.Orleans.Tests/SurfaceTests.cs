using System.Reflection;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The published surface of the two packages is what a consumer should use and nothing else. Every
/// public type is listed here, so adding one — or losing one — is a decision a reviewer sees, not a
/// side effect of a refactoring.
/// </summary>
public class SurfaceTests
{
    private const string RemindableInterface = "Orleans.IRemindable";

    private static readonly string[] RuntimeSurface =
    [
        "Microsoft.Extensions.DependencyInjection.OrleansAggregateServiceCollectionExtensions",
        "Microsoft.Extensions.DependencyInjection.OrleansProjectionServiceCollectionExtensions",
        "Microsoft.Extensions.DependencyInjection.OrleansSingletonWorkServiceCollectionExtensions",
        "Microsoft.Extensions.DependencyInjection.OrleansTimersServiceCollectionExtensions",
        "Microsoft.Extensions.DependencyInjection.StrataraOrleansSiloBuilderExtensions",
        "Stratara.Orleans.Aggregates.HeavyWorkOptions",
        "Stratara.Orleans.Aggregates.OrleansDispatchOptions",
        "Stratara.Orleans.CommitOrder.CommitOrderOptions",
        "Stratara.Orleans.CommitOrder.PartitionMap",
        "Stratara.Orleans.GrainDirectories",
        "Stratara.Orleans.Hosting.ExecutionModelResetReport",
        "Stratara.Orleans.Hosting.IExecutionModelReset",
        "Stratara.Orleans.Projections.ProjectionGrainOptions",
        "Stratara.Orleans.Sagas.SagaGrainOptions",
        "Stratara.Orleans.Singleton.OutboxDrainOptions",
        "Stratara.Orleans.Singleton.OutboxDrainWork",
        "Stratara.Orleans.Singleton.SingletonWorkOptions",
        "Stratara.Orleans.Timers.DurableTimerOptions",
    ];

    private static readonly string[] PersistenceSurface =
    [
        "Microsoft.Extensions.DependencyInjection.OrleansCheckpointServiceCollectionExtensions",
        "Microsoft.Extensions.DependencyInjection.OrleansCommitOrderServiceCollectionExtensions",
        "Microsoft.Extensions.DependencyInjection.OrleansIntentStoreServiceCollectionExtensions",
        "Microsoft.Extensions.DependencyInjection.OrleansResetServiceCollectionExtensions",
        "Stratara.Orleans.EntityFrameworkCore.CommitOrder.PartitionCounterBackfill",
        "Stratara.Orleans.EntityFrameworkCore.CommitOrder.PartitionCounterInterceptor",
        "Stratara.Orleans.EntityFrameworkCore.CommitOrder.PortableCounterReader`1",
        "Stratara.Orleans.EntityFrameworkCore.CommitOrder.PostgresTransactionIdReader`1",
        "Stratara.Orleans.EntityFrameworkCore.Projections.ProjectionCheckpointStore`1",
    ];

    [Theory]
    [InlineData("Stratara.Orleans")]
    [InlineData("Stratara.Orleans.EntityFrameworkCore")]
    public void The_published_surface_is_exactly_the_listed_types(string assemblyName)
    {
        var expected = assemblyName == "Stratara.Orleans" ? RuntimeSurface : PersistenceSurface;
        var actual = Assembly.Load(assemblyName)
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToList();

        var added = actual.Except(expected, StringComparer.Ordinal).ToList();
        var removed = expected.Except(actual, StringComparer.Ordinal).ToList();

        Assert.True(
            added.Count == 0 && removed.Count == 0,
            $"{assemblyName} publishes {string.Join(", ", added)} beyond the list and lacks {string.Join(", ", removed)}.");
    }

    [Theory]
    [InlineData("Stratara.Orleans")]
    [InlineData("Stratara.Orleans.EntityFrameworkCore")]
    public void NoPublicType_ImplementsIRemindable(string assemblyName)
    {
        var offenders = Assembly.Load(assemblyName)
            .GetExportedTypes()
            .Where(type => type.GetInterfaces().Any(contract => contract.FullName == RemindableInterface))
            .Select(type => type.FullName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Timers are reached through Stratara's own port; these public types expose {RemindableInterface}: " +
            string.Join(", ", offenders));
    }
}
