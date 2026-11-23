using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace WallIServer.Endpoints;

/// <summary>
/// <c>GET /api/server-info</c>: the server's private LAN IPv4 addresses and listening port, so the setup page
/// can suggest the panel's update URL. Public (LAN addresses only).
/// </summary>
public static class ServerInfoEndpoint
{
    public const int DefaultPort = 5080;

    public static IEndpointRouteBuilder MapServerInfoEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/server-info", (IServer server, IConfiguration config) => Results.Ok(new
            {
                addresses = LanAddresses(NetworkInterface.GetAllNetworkInterfaces()),
                port = ListeningPort(server, config),
            }))
            .WithName("GetServerInfo")
            .WithTags("Server")
            .WithSummary("LAN IPv4 addresses and listening port of this server (public).");
        return app;
    }

    internal static IReadOnlyList<string> LanAddresses(IEnumerable<NetworkInterface> nics)
    {
        var ips = new List<string>();
        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            foreach (var u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var text = u.Address.ToString();
                if (text.StartsWith("127.", StringComparison.Ordinal) || text.StartsWith("169.254.", StringComparison.Ordinal))
                    continue;
                if (!ips.Contains(text)) ips.Add(text);
            }
        }
        return SelectLanAddresses(ips);
    }

    /// <summary>
    /// Only private (RFC 1918) addresses, in ordinal order. This endpoint is public, so public, VPN or
    /// CGNAT addresses are never reported — the panel only ever needs a home-network address.
    /// </summary>
    internal static IReadOnlyList<string> SelectLanAddresses(IEnumerable<string> ips) =>
        ips.Where(IsPrivate).Distinct(StringComparer.Ordinal).OrderBy(ip => ip, StringComparer.Ordinal).ToList();

    private static bool IsPrivate(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = addr.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>First bound http:// port, else the configured Kestrel endpoint's port, else 5080.</summary>
    internal static int ListeningPort(IServer server, IConfiguration config)
    {
        var bound = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        foreach (var address in bound)
        {
            if (TryPort(address, out var port)) return port;
        }
        return TryPort(config["Kestrel:Endpoints:Http:Url"], out var configured) ? configured : DefaultPort;
    }

    private static bool TryPort(string? url, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
        // Kestrel accepts wildcard hosts (0.0.0.0, *, +) that Uri can't parse; normalise them.
        var normalised = url.Replace("://*", "://localhost").Replace("://+", "://localhost");
        if (!Uri.TryCreate(normalised, UriKind.Absolute, out var uri) || uri.IsDefaultPort && !normalised.Contains(":80", StringComparison.Ordinal))
            return false;
        port = uri.Port;
        return port is > 0 and <= 65535;
    }
}
