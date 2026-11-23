using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WallIServer.Endpoints;

namespace WallIServer.Tests;

public class ServerInfoTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task ServerInfo_IsPublic_AndReportsDefaultPort()
    {
        using var client = _app.CreateClient(); // no X-Api-Key
        var res = await client.GetAsync("/api/server-info");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        // TestServer binds no real address, so the configured Kestrel:Endpoints:Http:Url port is used.
        Assert.Equal(5080, json.GetProperty("port").GetInt32());

        var addresses = json.GetProperty("addresses");
        Assert.Equal(JsonValueKind.Array, addresses.ValueKind);
        foreach (var a in addresses.EnumerateArray())
        {
            var text = a.GetString()!;
            Assert.True(IPAddress.TryParse(text, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork, text);
            Assert.False(text.StartsWith("127.", StringComparison.Ordinal), text);
        }
    }

    [Fact]
    public void OnlyPrivateLanAddressesAreReported()
    {
        // Public, CGNAT/VPN (100.64/10) and other non-RFC1918 addresses must never leak from this public endpoint.
        var lan = ServerInfoEndpoint.SelectLanAddresses(
            ["8.8.4.4", "192.168.1.112", "10.0.0.5", "100.64.1.2", "172.16.0.9", "172.32.0.1", "not-an-ip"]);
        Assert.Equal(["10.0.0.5", "172.16.0.9", "192.168.1.112"], lan);
    }
}
