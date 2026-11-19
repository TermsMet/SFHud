using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SFHud.Services;

public sealed record ServerInfo(string Name, string Map, string Game, int Players, int MaxPlayers, int Bots, int PingMs);

/// <summary>
/// Source-engine A2S_INFO query (works for Garry's Mod, TF2, CS2 and most Source servers).
/// Only contacts addresses the player added themselves.
/// </summary>
public static class ServerQuery
{
    private static readonly byte[] InfoRequest =
        new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x54 }
            .Concat(Encoding.ASCII.GetBytes("Source Engine Query\0"))
            .ToArray();

    public static bool TryParse(string address, out string host, out int port)
    {
        host = "";
        port = 27015;
        address = address.Trim();
        if (address.StartsWith("steam://connect/", StringComparison.OrdinalIgnoreCase))
            address = address["steam://connect/".Length..].TrimEnd('/');
        if (address.Length == 0) return false;

        int colon = address.LastIndexOf(':');
        if (colon > 0 && address.IndexOf(':') == colon)
        {
            if (!int.TryParse(address[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
            host = address[..colon];
        }
        else
        {
            host = address;
        }
        return host.Length > 0;
    }

    public static async Task<ServerInfo?> QueryAsync(string address, int timeoutMs = 1500)
    {
        if (!TryParse(address, out string host, out int port)) return null;
        try
        {
            IPAddress? ip = IPAddress.TryParse(host, out var parsed)
                ? parsed
                : (await Dns.GetHostAddressesAsync(host)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip is null) return null;

            using var udp = new UdpClient(ip.AddressFamily);
            udp.Connect(ip, port);

            var sw = Stopwatch.StartNew();
            await udp.SendAsync(InfoRequest, InfoRequest.Length);
            byte[]? resp = await ReceiveAsync(udp, timeoutMs);

            // Newer servers answer with a challenge (0x41) that has to be echoed back.
            if (resp is { Length: >= 9 } && resp[4] == 0x41)
            {
                byte[] withChallenge = InfoRequest.Concat(resp[5..9]).ToArray();
                sw.Restart();
                await udp.SendAsync(withChallenge, withChallenge.Length);
                resp = await ReceiveAsync(udp, timeoutMs);
            }
            int ping = (int)sw.ElapsedMilliseconds;

            if (resp is null || resp.Length < 6 || resp[4] != 0x49) return null;
            return Parse(resp, ping);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReceiveAsync(UdpClient udp, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { return (await udp.ReceiveAsync(cts.Token)).Buffer; }
        catch (OperationCanceledException) { return null; }
        catch (SocketException) { return null; }
    }

    private static ServerInfo Parse(byte[] b, int ping)
    {
        int i = 6; // header (4) + type (1) + protocol (1)
        string name = ReadString(b, ref i);
        string map = ReadString(b, ref i);
        ReadString(b, ref i); // folder
        string game = ReadString(b, ref i);
        i += 2; // steam app id (short)
        int players = i < b.Length ? b[i++] : 0;
        int max = i < b.Length ? b[i++] : 0;
        int bots = i < b.Length ? b[i] : 0;
        return new ServerInfo(name, map, game, players, max, bots, ping);
    }

    private static string ReadString(byte[] b, ref int i)
    {
        int start = i;
        while (i < b.Length && b[i] != 0) i++;
        string s = Encoding.UTF8.GetString(b, start, i - start);
        i++; // skip null
        return s;
    }

    /// <summary>Opens Steam and joins the server (the game launches if it isn't running).</summary>
    public static void Connect(string address)
    {
        if (!TryParse(address, out string host, out int port)) return;
        Process.Start(new ProcessStartInfo($"steam://connect/{host}:{port}") { UseShellExecute = true });
    }

    public static void LaunchSteamGame(int appId) =>
        Process.Start(new ProcessStartInfo($"steam://rungameid/{appId}") { UseShellExecute = true });
}

/// <summary>Live row for a favorite server.</summary>
public sealed class ServerEntry : Observable
{
    private string _title;
    private string _status = "NOT QUERIED";
    private string _map = "—";
    private string _players = "—";
    private string _ping = "";
    private bool _online;
    private bool _busy;

    public ServerEntry(ServerFavorite fav)
    {
        Favorite = fav;
        _title = string.IsNullOrWhiteSpace(fav.Name) ? fav.Address : fav.Name;
    }

    public ServerFavorite Favorite { get; }
    public string Address => Favorite.Address;
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Map { get => _map; private set => Set(ref _map, value); }
    public string Players { get => _players; private set => Set(ref _players, value); }
    public string Ping { get => _ping; private set => Set(ref _ping, value); }
    public bool Online { get => _online; private set => Set(ref _online, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    public async Task RefreshAsync()
    {
        if (Busy) return;
        Busy = true;
        Status = "QUERYING…";
        var info = await ServerQuery.QueryAsync(Address);
        if (info is null)
        {
            Online = false;
            Status = "NO RESPONSE";
            Map = "—";
            Players = "—";
            Ping = "";
        }
        else
        {
            Online = true;
            Status = "ONLINE";
            if (string.IsNullOrWhiteSpace(Favorite.Name)) Title = info.Name;
            Map = info.Map;
            Players = $"{info.Players}/{info.MaxPlayers}";
            Ping = $"{info.PingMs} ms";
        }
        Busy = false;
    }
}
