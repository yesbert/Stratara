using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;

namespace Stratara.Orleans.Hosting;

/// <summary>
/// Has a grain call whose token is cancelled wait for the callee's answer instead of failing at once. Orleans otherwise
/// completes such a call with a cancellation the moment the token fires, whatever the callee does — so a saga step, a
/// command or a timer handler that committed as its caller's silo stopped would look cancelled to the caller, and the
/// caller would run it again. The cancellation still reaches the callee: a callee that stops before it commits answers
/// with the cancellation, one that has committed answers with its outcome. The setting is the host's for every grain
/// call; a host that sets it back to <see langword="false"/> after these registrations gives that up.
/// </summary>
internal static class FrameworkCallCancellation
{
    public static void Register(IServiceCollection services)
    {
        services.Configure<SiloMessagingOptions>(options => options.WaitForCancellationAcknowledgement = true);
        services.Configure<ClientMessagingOptions>(options => options.WaitForCancellationAcknowledgement = true);
    }
}
