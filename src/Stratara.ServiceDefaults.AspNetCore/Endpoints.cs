using System.Diagnostics.CodeAnalysis;

namespace Microsoft.Extensions.Hosting;

[ExcludeFromCodeCoverage]
internal static class Endpoints
{
    public const string HealthEndpointPath = "/health";
    public const string AlivenessEndpointPath = "/alive";
}