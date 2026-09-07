using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NdiMerger.Sources;

public sealed class NdiAdapterChoice : IEquatable<NdiAdapterChoice>
{
    public static NdiAdapterChoice Automatic { get; } = new()
    {
        Id = "",
        Ipv4 = null,
        AdapterName = null,
        DisplayName = "Automatisch (alle Netzwerkkarten)",
        BindTokens = []
    };

    public required string Id { get; init; }
    public string? Ipv4 { get; init; }
    public string? AdapterName { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<string> BindTokens { get; init; } = [];
    public bool IsWifi { get; init; }
    public bool IsAutomatic => string.IsNullOrEmpty(Id);

    public bool Equals(NdiAdapterChoice? other) =>
        other is not null && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is NdiAdapterChoice other && Equals(other);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
}

/// <summary>
/// Access-Manager-Schema (adapters.allowed = IPv4s, leer = alle Karten),
/// geschrieben nach &lt;exe&gt;\NDI\ndi-config.v1.json. NDI_CONFIG_DIR + NDIRoot
/// zeigen auf diesen Ordner, bevor die NDI-DLL initialisiert wird.
/// </summary>
public static class NdiAdapterBinding
{
    public static bool LastAccessManagerWriteOk { get; private set; }
    public static string? LastAccessManagerWriteError { get; private set; }
    public static string LastAccessManagerWriteMode { get; private set; } = "";
    public static string CurrentConfigJson { get; private set; } = "";
    public static IReadOnlyList<string> CurrentAllowedIpv4s { get; private set; } = [];

    public static string AppDirectory
    {
        get
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            return string.IsNullOrEmpty(exeDir) ? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) : exeDir;
        }
    }

    /// <summary>Folder that contains ndi-config.v1.json (NDI_CONFIG_DIR).</summary>
    public static string ConfigDirectory => Path.Combine(AppDirectory, "NDI");

    public static string AppConfigPath => Path.Combine(ConfigDirectory, "ndi-config.v1.json");

    /// <summary>
    /// SpeedHQ ceiling: full-frame alone, and all zone senders together, each target this.
    /// </summary>
    public const long MaxCombinedBitsPerSecond = 10_000_000_000L;

    /// <summary>Pixel count of currently enabled zone senders (full-frame excluded).</summary>
    public static long ZoneBudgetPixels { get; private set; } = 1;

    public static void SetZoneBudgetPixels(long pixels) =>
        ZoneBudgetPixels = Math.Max(1, pixels);

    [ModuleInitializer]
    internal static void BindConfigDirBeforeNdiLoads()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            PublishProcessConfigDir();
        }
        catch
        {
            // first Apply() retries
        }
    }

    public static IReadOnlyList<NdiAdapterChoice> ListAdapters()
    {
        var nics = new List<NdiAdapterChoice>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(IsCandidate)
                     .OrderBy(n => IsWifi(n) ? 1 : 0)
                     .ThenByDescending(n => n.Speed)
                     .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            var ip = FirstUsableIpv4(nic);
            if (ip is null)
                continue;

            var speed = FormatSpeed(nic.Speed);
            var desc = ShortDescription(nic.Description);
            var wifi = IsWifi(nic);
            var kind = wifi ? "WLAN" : "Ethernet";
            var label = nic.Name.Equals(desc, StringComparison.OrdinalIgnoreCase)
                ? nic.Name
                : $"{nic.Name}  —  {desc}";
            nics.Add(new NdiAdapterChoice
            {
                Id = nic.Id,
                Ipv4 = ip,
                AdapterName = nic.Name,
                DisplayName = string.IsNullOrEmpty(speed)
                    ? $"{kind}  ·  {label}  —  {ip}"
                    : $"{kind}  ·  {label}  —  {ip}  ({speed})",
                BindTokens = [ip],
                IsWifi = wifi
            });
        }

        var auto = new NdiAdapterChoice
        {
            Id = "",
            Ipv4 = null,
            AdapterName = null,
            DisplayName = NdiAdapterChoice.Automatic.DisplayName,
            BindTokens = []
        };

        var list = new List<NdiAdapterChoice> { auto };
        list.AddRange(nics);
        return list;
    }

    public static NdiAdapterChoice Resolve(string? id, string? ipv4, IReadOnlyList<NdiAdapterChoice> adapters)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var byId = adapters.FirstOrDefault(a => a.Id == id);
            if (byId is not null)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(ipv4))
        {
            var byIp = adapters.FirstOrDefault(a => a.Ipv4 == ipv4);
            if (byIp is not null)
                return byIp;
        }

        return adapters.FirstOrDefault(a => a.IsAutomatic) ?? NdiAdapterChoice.Automatic;
    }

    public static void Apply(NdiAdapterChoice? choice)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var tokens = AllowedTokens(choice);
        CurrentAllowedIpv4s = tokens
            .Where(t => IPAddress.TryParse(t, out var addr) && IsUsableIpv4(addr))
            .ToArray();
        CurrentConfigJson = BuildAppConfigJson(CurrentAllowedIpv4s);
        LastAccessManagerWriteOk = TryWriteText(AppConfigPath, CurrentConfigJson, out var error);
        LastAccessManagerWriteError = error;
        LastAccessManagerWriteMode = LastAccessManagerWriteOk ? "app-local" : "app-local-failed";
        PublishProcessConfigDir();
    }

    /// <summary>
    /// Access Manager: IPv4s in adapters.allowed. Empty list = all NICs.
    /// </summary>
    public static IReadOnlyList<string> AllowedTokens(NdiAdapterChoice? choice)
    {
        if (choice is { IsAutomatic: false, BindTokens.Count: > 0 })
            return choice.BindTokens.Where(t => IPAddress.TryParse(t, out var addr) && IsUsableIpv4(addr)).ToArray();

        if (choice is { IsAutomatic: false, Ipv4: { Length: > 0 } ip } &&
            IPAddress.TryParse(ip, out var parsed) && IsUsableIpv4(parsed))
            return [ip];

        return [];
    }

    public static IReadOnlyList<string> AllowedIpv4s(NdiAdapterChoice? choice) =>
        AllowedTokens(choice).Where(t => IPAddress.TryParse(t, out var addr) && IsUsableIpv4(addr)).ToArray();

    /// <summary>
    /// Per-sender JSON for NDIlib_send_create_v2 (adapters.allowed + SHQ quality).
    /// Pass <paramref name="qualityPercent"/> for full-frame (own 10 Gbps) or omit for
    /// the shared zone budget.
    /// </summary>
    public static string SenderConfigJson(int? qualityPercent = null)
    {
        var ndi = new JsonObject
        {
            ["rudp"] = new JsonObject
            {
                ["send"] = new JsonObject { ["enable"] = true },
                ["recv"] = new JsonObject { ["enable"] = true }
            },
            ["tcp"] = new JsonObject
            {
                ["send"] = new JsonObject { ["enable"] = true },
                ["recv"] = new JsonObject { ["enable"] = true }
            },
            ["codec"] = SpeedHqCodecObject(qualityPercent ?? SpeedHqQualityPercent(ZoneBudgetPixels))
        };

        if (CurrentAllowedIpv4s.Count > 0)
        {
            var allowed = new JsonArray();
            foreach (var ip in CurrentAllowedIpv4s)
                allowed.Add(ip);
            ndi["adapters"] = new JsonObject { ["allowed"] = allowed };
        }

        return new JsonObject { ["ndi"] = ndi }.ToJsonString();
    }

    public static void ApplyFromSession(string? adapterId, string? adapterIp)
    {
        Apply(Resolve(adapterId, adapterIp, ListAdapters()));
    }

    public static void EnsureProcessConfigDir()
    {
        Directory.CreateDirectory(ConfigDirectory);
        if (!File.Exists(AppConfigPath))
            Apply(NdiAdapterChoice.Automatic);
        else
            PublishProcessConfigDir();
    }

    private static void PublishProcessConfigDir()
    {
        // SDK: NDI_CONFIG_DIR = folder with ndi-config.v1.json, before initialize().
        // Access Manager also resolves %NDIRoot%\NDI\ndi-config.v1.json.
        Environment.SetEnvironmentVariable("NDI_CONFIG_DIR", ConfigDirectory);
        Environment.SetEnvironmentVariable("NDIRoot", AppDirectory);
        SetEnvironmentVariable("NDI_CONFIG_DIR", ConfigDirectory);
        SetEnvironmentVariable("NDIRoot", AppDirectory);
        try { WputenvS("NDI_CONFIG_DIR", ConfigDirectory); } catch { /* ucrt optional */ }
        try { WputenvS("NDIRoot", AppDirectory); } catch { /* ucrt optional */ }
    }

    private static bool IsWifi(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            return true;
        var name = nic.Name;
        var desc = nic.Description;
        return name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("WiFi", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("WLAN", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("WiFi", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCandidate(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up)
            return false;
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel
            or NetworkInterfaceType.Ppp)
            return false;

        var name = nic.Name;
        var desc = nic.Description;
        if (name.Contains("Npcap", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            return false;

        return nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.Ethernet3Megabit
            or NetworkInterfaceType.Wireless80211
            || name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Ethernet", StringComparison.OrdinalIgnoreCase)
            || IsWifi(nic);
    }

    private static string? FirstUsableIpv4(NetworkInterface nic)
    {
        return nic.GetIPProperties().UnicastAddresses
            .Select(a => a.Address)
            .Where(IsUsableIpv4)
            .OrderBy(a => IsLinkLocal(a) ? 1 : 0)
            .Select(a => a.ToString())
            .FirstOrDefault();
    }

    private static bool IsLinkLocal(IPAddress addr)
    {
        var bytes = addr.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool IsUsableIpv4(IPAddress addr)
    {
        if (addr.AddressFamily != AddressFamily.InterNetwork)
            return false;
        return !IPAddress.IsLoopback(addr);
    }

    private static string ShortDescription(string description)
    {
        return description
            .Replace(" Family Controller", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string? FormatSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
            return null;
        if (bitsPerSecond >= 1_000_000_000)
            return $"{bitsPerSecond / 1_000_000_000.0:0.#} Gbit";
        if (bitsPerSecond >= 1_000_000)
            return $"{bitsPerSecond / 1_000_000.0:0.#} Mbit";
        return null;
    }

    private static string BuildAppConfigJson(IReadOnlyList<string> _)
    {
        var root = LoadBaseConfig();
        var ndi = root["ndi"] as JsonObject ?? new JsonObject();
        root["ndi"] = ndi;

        // Process-wide allowed must stay empty. Restricting it to Ethernet on a
        // dual-homed PC (1.x + 2.x) breaks mDNS / port 5960 and viewers never connect.
        // Video bind is per-sender via send_create_v2 (SenderConfigJson).
        ndi["adapters"] = new JsonObject { ["allowed"] = new JsonArray() };
        if (ndi["multicast"] is not JsonObject multicast)
        {
            multicast = new JsonObject();
            ndi["multicast"] = multicast;
        }
        if (multicast["recv"] is not JsonObject recv)
        {
            recv = new JsonObject();
            multicast["recv"] = recv;
        }
        recv["enable"] = true;
        ApplySpeedHqCodec(ndi);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject LoadBaseConfig()
    {
        try
        {
            if (File.Exists(AppConfigPath) && JsonNode.Parse(File.ReadAllText(AppConfigPath)) is JsonObject obj)
                return obj;
        }
        catch
        {
            // ignore unreadable app NDI config
        }

        return JsonNode.Parse(
            """
            {
              "ndi": {
                "machinename": "",
                "tcp": { "recv": { "enable": true } },
                "rudp": { "recv": { "enable": true } },
                "groups": { "send": "", "recv": "" },
                "unicast": { "recv": { "enable": true } },
                "networks": { "ips": "", "discovery": "" },
                "adapters": { "allowed": [] },
                "multicast": {
                  "send": { "ttl": 1, "enable": false, "netmask": "255.255.0.0", "netprefix": "239.255.0.0" },
                  "recv": { "enable": true, "subnets": [] }
                }
              }
            }
            """) as JsonObject ?? new JsonObject { ["ndi"] = new JsonObject() };
    }

    private static bool TryWriteText(string path, string json, out string? error)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static void ApplySpeedHqCodec(JsonObject ndi) =>
        ndi["codec"] = SpeedHqCodecObject(SpeedHqQualityPercent(ZoneBudgetPixels));

    private static JsonObject SpeedHqCodecObject(int quality) => new()
    {
        ["shq"] = new JsonObject
        {
            ["quality"] = quality,
            ["mode"] = "4:2:2"
        }
    };

    /// <summary>
    /// NDI SHQ quality is % of the 4K30 default (~125 Mbps). Scaled so <paramref name="budgetPixels"/>
    /// encode at <see cref="MaxCombinedBitsPerSecond"/>.
    /// </summary>
    public static int SpeedHqQualityPercent(long budgetPixels)
    {
        const double fourK30Mbps = 125.0;
        const int fourKw = 3840;
        const int fourKh = 2160;
        double targetMbps = MaxCombinedBitsPerSecond / 1_000_000.0;
        double pixelScale = Math.Max(1, budgetPixels) / (fourKw * (double)fourKh);
        double defaultMbps = fourK30Mbps * pixelScale;
        int quality = (int)Math.Round(targetMbps / defaultMbps * 100.0);
        return Math.Clamp(quality, 1, 2000);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetEnvironmentVariable(string lpName, string? lpValue);

    [DllImport("ucrtbase.dll", EntryPoint = "_wputenv_s", CharSet = CharSet.Unicode)]
    private static extern int WputenvS(string name, string value);
}
