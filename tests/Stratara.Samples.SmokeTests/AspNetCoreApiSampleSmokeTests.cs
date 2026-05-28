using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace Stratara.Samples.SmokeTests;

public sealed class AspNetCoreApiSampleSmokeTests
{
    [Fact]
    public void AspNetCoreApi_ServesEndpoints_AndRoundTripsOpenDepositBalance()
    {
        var port = PickFreeTcpPort();
        var baseAddress = new Uri($"http://localhost:{port}");
        var env = new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = baseAddress.ToString(),
        };

        var result = SampleRunner.RunUntilMarker(
            "Stratara.Sample.AspNetCoreApi",
            markerPhrase: $"Now listening on: {baseAddress.ToString().TrimEnd('/')}",
            onMarkerReached: _ => DriveApi(baseAddress),
            timeout: TimeSpan.FromSeconds(60),
            environment: env);

        Assert.Contains("Now listening on:", result.StdOut);
    }

    private static void DriveApi(Uri baseAddress)
    {
        using var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };

        var createResponse = http.PostAsJsonAsync(
            "/accounts",
            new { OwnerName = "Alice", InitialBalance = 100m }).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = createResponse.Content.ReadFromJsonAsync<CreatedAccount>().GetAwaiter().GetResult();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.Id);

        var depositResponse = http.PostAsJsonAsync(
            $"/accounts/{created.Id}/deposits",
            new { Amount = 50m }).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.NoContent, depositResponse.StatusCode);

        var balance = http.GetFromJsonAsync<BalanceView>(
            $"/accounts/{created.Id}/balance").GetAwaiter().GetResult();
        Assert.NotNull(balance);
        Assert.Equal(150m, balance!.Balance);
    }

    private static int PickFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed record CreatedAccount(Guid Id);

    private sealed record BalanceView(Guid AccountId, decimal Balance);
}
