using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;

namespace Stratara.Orleans.Hosting;

/// <summary>
/// Has a grain call whose token is cancelled wait for the callee's answer instead of failing at once. Orleans otherwise
/// completes such a call with a cancellation the moment the token fires, whatever the callee does — so a saga step that
/// committed as the timer tick or saga reader calling it stopped would look cancelled, and would run again. The
/// cancellation still reaches the callee: a callee that stops before it commits answers with the cancellation, one that
/// has committed answers with its outcome, and one that does not answer ends the call with a timeout at the response
/// timeout. The setting is the host's for every grain call that carries a token — the host's own included; calls without
/// one are unaffected. A host that sets it back to <see langword="false"/> after these registrations gives that up.
/// </summary>
internal static class FrameworkCallCancellation
{
    public static void Register(IServiceCollection services)
    {
        services.Configure<SiloMessagingOptions>(options => options.WaitForCancellationAcknowledgement = true);
        services.Configure<ClientMessagingOptions>(options => options.WaitForCancellationAcknowledgement = true);
    }
}
