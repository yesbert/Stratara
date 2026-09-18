using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Stratara.Security;

/// <summary>
/// Reads <see cref="StrataraBlobEncryptionOptions"/> from the <c>Stratara:BlobEncryption</c> section of
/// the configuration the container holds, and binds nothing where it holds none. Registered once, at the
/// position of the first <c>AddStrataraBlobEncryption()</c> call, so a later call — <c>AddSecurity()</c>
/// and <c>AddStrataraFileKeyStore</c> make one each — cannot re-apply the section over a value the host
/// configured in code in between.
/// </summary>
internal sealed class StrataraBlobEncryptionOptionsBinding(IServiceProvider services) : IConfigureOptions<StrataraBlobEncryptionOptions>
{
    public void Configure(StrataraBlobEncryptionOptions options) =>
        services.GetService<IConfiguration>()?.GetSection(StrataraBlobEncryptionOptions.SectionName).Bind(options);
}
