using System.Reflection;
using Moq;
using Orleans.Concurrency;
using Orleans.Serialization.Invocation;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.Projections;
using Stratara.Orleans.Sagas;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The predicate the reading grains and the singleton work grain let interleave with lets the reminder's tick through
/// and nothing of the grains' own interfaces, and each grain names a predicate the runtime can find: a public static
/// method of the grain's own class.
/// </summary>
public sealed class KeepAliveInterleavingTests
{
    public static TheoryData<Type> Grains => [typeof(ProjectionGrain), typeof(SagaReaderGrain), typeof(SingletonWorkGrain)];

    public static TheoryData<Type> GrainInterfaces => [typeof(IProjectionGrain), typeof(ISagaReaderGrain), typeof(ISingletonWorkGrain)];

    [Fact]
    public void The_reminder_tick_interleaves()
    {
        Assert.True(KeepAliveInterleaving.IsKeepAliveTick(RequestOf(typeof(IRemindable))));
    }

    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public void A_call_of_the_grain_s_own_interface_does_not_interleave_through_the_predicate(Type grainInterface)
    {
        Assert.False(KeepAliveInterleaving.IsKeepAliveTick(RequestOf(grainInterface)));
    }

    [Theory]
    [MemberData(nameof(Grains))]
    public void The_grain_names_a_public_static_predicate_of_its_own_class_that_lets_the_tick_through(Type grain)
    {
        var attribute = grain.GetCustomAttribute<MayInterleaveAttribute>(inherit: false);
        Assert.NotNull(attribute);

        var predicate = grain.GetMethod("MayInterleave", BindingFlags.Public | BindingFlags.Static, [typeof(IInvokable)]);
        Assert.NotNull(predicate);
        Assert.Equal(typeof(bool), predicate.ReturnType);
        Assert.Equal(true, predicate.Invoke(null, [RequestOf(typeof(IRemindable))]));
        Assert.Equal(false, predicate.Invoke(null, [RequestOf(typeof(IGrainWithStringKey))]));
    }

    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public void The_start_call_interleaves(Type grainInterface)
    {
        var ensureRunning = grainInterface.GetMethod("EnsureRunningAsync", Type.EmptyTypes);

        Assert.NotNull(ensureRunning);
        Assert.NotNull(ensureRunning.GetCustomAttribute<AlwaysInterleaveAttribute>());
    }

    private static IInvokable RequestOf(Type grainInterface)
    {
        var request = new Mock<IInvokable>();
        request.Setup(invokable => invokable.GetInterfaceType()).Returns(grainInterface);
        return request.Object;
    }
}
