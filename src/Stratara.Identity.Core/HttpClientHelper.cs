namespace Stratara.Identity.Core;

/// <summary>
/// Default <see cref="IHttpClientHelper"/> implementation that simply exposes the injected <see cref="HttpClient"/>.
/// Registered via <c>AddHttpClient&lt;IHttpClientHelper, HttpClientHelper&gt;</c> so DI applies a named <see cref="HttpClient"/>
/// (with its own handler chain + base address) to this typed client.
/// </summary>
public sealed class HttpClientHelper(HttpClient httpClient) : IHttpClientHelper
{
    /// <inheritdoc/>
    public HttpClient Client { get; } = httpClient;
}

/// <summary>
/// Abstraction over a configured <see cref="HttpClient"/>, so identity-related services can take a single
/// dependency that resolves to the right named/typed client (with auth handler, base address, resilience pipeline).
/// </summary>
public interface IHttpClientHelper
{
    /// <summary>The configured <see cref="HttpClient"/> to use for identity-endpoint calls.</summary>
    HttpClient Client { get; }
}
