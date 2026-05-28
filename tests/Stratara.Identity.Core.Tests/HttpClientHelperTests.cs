using Stratara.Identity.Core;

namespace Stratara.Identity.Core.Tests;

public class HttpClientHelperTests
{
    [Fact]
    public void Client_ReturnsTheInjectedHttpClient()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri("https://example.test/") };

        var helper = new HttpClientHelper(httpClient);

        Assert.Same(httpClient, helper.Client);
    }

    [Fact]
    public void HttpClientHelper_ImplementsIHttpClientHelper()
    {
        using var httpClient = new HttpClient();
        IHttpClientHelper helper = new HttpClientHelper(httpClient);

        Assert.Same(httpClient, helper.Client);
    }
}
