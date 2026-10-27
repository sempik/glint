using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Glint.Core;

namespace Glint.Services.Indicators;

/// <summary>
/// VPN badge while a tunnel adapter is up (WireGuard, OpenVPN, SSTP/PPP, Wintun, TAP and the like).
/// Windows raises NetworkAddressChanged whenever an adapter comes or goes, so nothing polls.
/// With the opt-in lookup it asks a public "what is my IP" service once per change, over HTTPS.
/// </summary>
public sealed class VpnMonitor : IndicatorMonitor, IReconfigurable
{
    const string Id = "vpn";

    static readonly string[] VpnWords =
    {
        "wireguard", "openvpn", "tap-windows", "tap-win", "wintun", "sstp", "vpn", "tailscale", "zerotier",
        "amnezia", "nordlynx", "proton", "outline", "hamachi", "cloudflare warp", "ikev2", "l2tp", "pptp", "softether",
    };
    // System tunnels that are always there and say nothing about a VPN.
    static readonly string[] NotVpn = { "teredo", "isatap", "6to4", "ip-https", "loopback", "pseudo" };
    // Virtual LANs and split proxies (game LANs, per-app tunnels) stay up all day without carrying your
    // traffic; they count only when they hold the default route.
    static readonly string[] Overlay = { "radmin", "hamachi", "zerotier", "tailscale", "sing-tun", "tun2socks", "virtualbox", "vmware", "hyper-v" };

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    static readonly HttpClient Http = CreateClient();

    readonly object _gate = new();
    Timer? _debounce;
    string? _adapters;
    bool _lookup;
    string[] _ignored = Array.Empty<string>();
    int _generation;
    string? _ipInfo;

    public VpnMonitor(IndicatorHub hub) : base(hub) { }

    static string[] IgnoredOf(IndicatorSettings s) => s.VpnIgnoredAdapters.ToArray();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Glint (+https://github.com/sempik/glint)");
        return client;
    }

    public bool NeedsRestart(IndicatorSettings s) =>
        _lookup != s.VpnIpLookup || !_ignored.SequenceEqual(IgnoredOf(s), StringComparer.OrdinalIgnoreCase);

    protected override void Start()
    {
        var s = AppServices.Settings.Current.Indicators;
        _lookup = s.VpnIpLookup;
        _ignored = IgnoredOf(s);
        _adapters = null;
        _ipInfo = null;
        _debounce = new Timer(_ => Scan(initial: false));
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        ThreadPool.QueueUserWorkItem(_ => Scan(initial: true));
    }

    protected override void Stop()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _debounce?.Dispose();
        _debounce = null;
        Interlocked.Increment(ref _generation);
        Hub.Remove(Id);
    }

    // Connecting a VPN fires a burst of address changes; settle before looking.
    void OnNetworkChanged(object? sender, EventArgs e) => _debounce?.Change(TimeSpan.FromSeconds(1.5), Timeout.InfiniteTimeSpan);

    void Scan(bool initial)
    {
        if (!Running) return;
        var names = FindVpnAdapters(_ignored);
        var key = names.Count == 0 ? null : string.Join(", ", names);
        bool changed;
        lock (_gate)
        {
            changed = key != _adapters;
            _adapters = key;
            if (changed) _ipInfo = null;
        }

        if (key is null)
        {
            Hub.Remove(Id);
            if (changed && !initial) AppServices.Notifier.Flash("VPN отключён", null, Glyphs.Shield, TimeSpan.FromSeconds(1.8));
            return;
        }

        Publish();
        if (!changed) return;
        if (!_lookup)
        {
            if (!initial) AppServices.Notifier.Flash("VPN подключён", key, Glyphs.Shield, TimeSpan.FromSeconds(2.2));
            return;
        }
        // The route may still be settling right after connect; ask a moment later.
        var generation = Interlocked.Increment(ref _generation);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
            var info = await LookupAsync().ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation) || !Running) return;
            lock (_gate) _ipInfo = info;
            Publish();
            if (!initial) AppServices.Notifier.Flash("VPN подключён", info is null ? key : $"{key} · {info}", Glyphs.Shield, TimeSpan.FromSeconds(2.6));
        });
    }

    void Publish()
    {
        string? adapters, info;
        lock (_gate)
        {
            adapters = _adapters;
            info = _ipInfo;
        }
        if (adapters is null) return;
        var tooltip = $"VPN: {adapters}";
        if (info is not null) tooltip += $"\n{info}";
        else if (_lookup) tooltip += "\nУзнаю внешний IP…";
        Hub.Set(new Indicator(Id, Glyphs.Shield, "Accent2Brush", tooltip)
        {
            Label = info is null ? "VPN" : info.Split(' ').LastOrDefault(),
            Order = 10,
        }, this);
    }

    /// <summary>Names of tunnel adapters that are up and carry an address, minus those the user told to ignore.</summary>
    internal static List<string> FindVpnAdapters(IReadOnlyCollection<string> ignored)
    {
        var result = new List<string>();
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return result;
        }
        var defaultIndex = DefaultRouteInterface();
        foreach (var nic in all)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            // A split tunnel for one app can hold the default route without carrying your traffic.
            if (ignored.Any(n => string.Equals(n, nic.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(n, nic.Description, StringComparison.OrdinalIgnoreCase))) continue;
            var text = $"{nic.Name} {nic.Description}".ToLowerInvariant();
            if (NotVpn.Any(text.Contains)) continue;
            var overlay = Overlay.Any(text.Contains);
            var physical = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet;
            var looksVpn = !overlay && (VpnWords.Any(text.Contains) || nic.NetworkInterfaceType == NetworkInterfaceType.Ppp);
            // Internet traffic goes through a virtual adapter: a full-tunnel VPN, whatever it calls itself.
            var carriesTraffic = defaultIndex is int index && Index(nic) == index && (overlay || !physical || VpnWords.Any(text.Contains));
            if (!(looksVpn || carriesTraffic) || !HasRoutableAddress(nic)) continue;
            result.Add(nic.Name);
        }
        return result;
    }

    /// <summary>IPv4 index of the adapter Windows would use to reach the internet.</summary>
    static int? DefaultRouteInterface()
    {
        // 1.1.1.1 in network byte order; only the route table is consulted, nothing is sent.
        const uint PublicAddress = 0x01010101;
        return GetBestInterface(PublicAddress, out var index) == 0 ? (int)index : null;
    }

    static int? Index(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GetIPv4Properties()?.Index;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    static bool HasRoutableAddress(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().UnicastAddresses.Any(a =>
                a.Address.AddressFamily == AddressFamily.InterNetwork
                || (a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    /// <summary>"203.0.113.5 · Германия DE" from ipwho.is, falling back to country.is; null on failure.</summary>
    static async Task<string?> LookupAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/?fields=ip,success,country,country_code&lang=ru").ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                var ip = root.GetProperty("ip").GetString();
                var country = root.TryGetProperty("country", out var c) ? c.GetString() : null;
                var code = root.TryGetProperty("country_code", out var cc) ? cc.GetString() : null;
                return $"{ip} · {country} {code}".Trim();
            }
        }
        catch (Exception)
        {
            // Try the second service.
        }
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.country.is/").ConfigureAwait(false));
            var ip = doc.RootElement.GetProperty("ip").GetString();
            var code = doc.RootElement.TryGetProperty("country", out var c) ? c.GetString() : null;
            return $"{ip} · {code}".Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
