using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace Stratara.Samples.SmokeTests;

public sealed class IdentitySampleSmokeTests
{
    [Fact]
    public void Identity_Boots_ServesOverview_AndChallengesProtectedApi()
    {
        var port = PickFreeTcpPort();
        var baseAddress = new Uri($"http://localhost:{port}");
        var env = new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = baseAddress.ToString(),
        };

        var result = SampleRunner.RunUntilMarker(
            "Stratara.Sample.Identity",
            markerPhrase: $"Now listening on: {baseAddress.ToString().TrimEnd('/')}",
            onMarkerReached: _ => DriveEndpoints(baseAddress),
            timeout: TimeSpan.FromSeconds(60),
            environment: env);

        Assert.Contains("Now listening on:", result.StdOut);
    }

    private static void DriveEndpoints(Uri baseAddress)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };

        var overviewResponse = http.GetAsync("/").GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.OK, overviewResponse.StatusCode);
        var overview = overviewResponse.Content.ReadFromJsonAsync<Overview>().GetAwaiter().GetResult();
        Assert.NotNull(overview);
        Assert.Contains("identity sample", overview!.Message, StringComparison.OrdinalIgnoreCase);

        using var bearerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        bearerRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer not-a-valid-token");
        var apiResponse = http.SendAsync(bearerRequest).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Unauthorized, apiResponse.StatusCode);

        var cookieResponse = http.GetAsync("/api/me").GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Found, cookieResponse.StatusCode);

        DriveApiKeyLane(http);
    }

    private static void DriveApiKeyLane(HttpClient http)
    {
        var issueResponse = http.PostAsync("/admin/api-keys", content: null).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);

        var issued = issueResponse.Content.ReadFromJsonAsync<IssuedKey>().GetAwaiter().GetResult();
        Assert.NotNull(issued);
        Assert.StartsWith("stk_", issued!.ApiKey, StringComparison.Ordinal);

        using var keyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        keyRequest.Headers.TryAddWithoutValidation("X-Api-Key", issued.ApiKey);
        var keyResponse = http.SendAsync(keyRequest).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.OK, keyResponse.StatusCode);

        var caller = keyResponse.Content.ReadFromJsonAsync<WhoAmI>().GetAwaiter().GetResult();
        Assert.NotNull(caller);
        Assert.Equal(issued.Tenant, caller!.Tenant);
        Assert.Equal(issued.KeyId, caller.Actor);

        using var bogusRequest = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        bogusRequest.Headers.TryAddWithoutValidation("X-Api-Key", "stk_not-a-real-key");
        var bogusResponse = http.SendAsync(bogusRequest).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Unauthorized, bogusResponse.StatusCode);
    }

    private static int PickFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record Overview(string Message, string Login, string Api);

    private sealed record IssuedKey(string ApiKey, string KeyId, string Tenant);

    private sealed record WhoAmI(string Actor, string Tenant);
}
