using Orleans.Serialization.Invocation;

namespace Stratara.Orleans.Hosting;

/// <summary>
/// Which requests a grain that keeps a keep-alive reminder lets interleave with whatever it is doing: the reminder's
/// tick. The tick only makes sure the grain runs, which it answers at once, so it never waits behind a catch-up or a
/// run that outlasts the response timeout — the runtime would log a tick it could not deliver. A grain names this
/// through <see cref="global::Orleans.Concurrency.MayInterleaveAttribute"/> and a public static method of its own that
/// returns <see cref="IsKeepAliveTick"/>, because the runtime looks the method up on the grain's class.
/// </summary>
internal static class KeepAliveInterleaving
{
    /// <summary>Whether <paramref name="request"/> is the delivery of a reminder tick.</summary>
    public static bool IsKeepAliveTick(IInvokable request) => request.GetInterfaceType() == typeof(IRemindable);
}
