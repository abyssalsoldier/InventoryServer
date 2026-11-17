using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InventoryServer;

public enum AccessMode
{
    /// <summary>Plain HTTP on the company network (IP or internal DNS name).</summary>
    Internal,
    /// <summary>Public domain served by a Cloudflare Tunnel; the app listens on 127.0.0.1 only.</summary>
    CloudflareTunnel,
    /// <summary>Public domain served directly over HTTPS with a .pfx certificate.</summary>
    OwnCertificate,
}

/// <summary>One company's server: its own domain, port, data folder, branding and users.</summary>
public class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string CompanyName { get; set; } = "";
    public string LogoFile { get; set; } = "";
    public string DataDir { get; set; } = "";
    public AccessMode Mode { get; set; } = AccessMode.CloudflareTunnel;
    /// <summary>Host name or IP that ends up in QR codes.</summary>
    public string Domain { get; set; } = "";
    /// <summary>HTTP port. In tunnel mode this is where cloudflared should send traffic (127.0.0.1).</summary>
    public int LocalPort { get; set; } = 8081;
    public int HttpsPort { get; set; } = 443;
    public string CertPath { get; set; } = "";
    public string CertPasswordProtected { get; set; } = "";
    public string TunnelTokenProtected { get; set; } = "";
    public bool StartOnLaunch { get; set; }

    [JsonIgnore] public string DisplayName => CompanyName.Length > 0 ? CompanyName : Domain;
    [JsonIgnore] public string DbPath => Path.Combine(DataDir, "inventory.db");
    [JsonIgnore] public string LogoPath => LogoFile.Length == 0 ? "" : Path.Combine(DataDir, LogoFile);
    [JsonIgnore] public bool BehindProxy => Mode == AccessMode.CloudflareTunnel;
    [JsonIgnore] public bool IsSecure => Mode != AccessMode.Internal;

    /// <summary>Base URL baked into QR codes, e.g. https://info-inventory.com</summary>
    [JsonIgnore]
    public string PublicUrl => Mode switch
    {
        AccessMode.CloudflareTunnel => $"https://{Domain}",
        AccessMode.OwnCertificate => HttpsPort == 443 ? $"https://{Domain}" : $"https://{Domain}:{HttpsPort}",
        _ => LocalPort == 80 ? $"http://{Domain}" : $"http://{Domain}:{LocalPort}",
    };

    /// <summary>The interface Kestrel listens on.</summary>
    public IPAddress BindAddress()
    {
        if (Mode == AccessMode.CloudflareTunnel) return IPAddress.Loopback;
        var addr = Domain.Trim();
        if (addr.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        if (IPAddress.TryParse(addr, out var ip)) return ip;
        try
        {
            var local = NetUtil.LocalAddresses();
            var match = Dns.GetHostAddresses(addr)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && local.Contains(a.ToString()));
            if (match != null) return match;
        }
        catch { }
        return IPAddress.Any;
    }

    public string? TunnelToken => TunnelTokenProtected.Length == 0 ? null : Secret.Unprotect(TunnelTokenProtected);
    public string? CertPassword => CertPasswordProtected.Length == 0 ? null : Secret.Unprotect(CertPasswordProtected);

    public ServerProfile Clone() => JsonSerializer.Deserialize<ServerProfile>(JsonSerializer.Serialize(this))!;
}

public static class NetUtil
{
    public static List<string> LocalAddresses()
    {
        var list = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(ua.Address.ToString());
        }
        return list;
    }

    public static bool PortIsFree(int port, IPAddress? address = null)
    {
        try
        {
            var l = new TcpListener(address ?? IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Encrypts small secrets (tunnel token, certificate password) for the current Windows user.</summary>
public static class Secret
{
    public static string Protect(string plain) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string protectedValue)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }
}

/// <summary>Loads and saves the list of server profiles (servers.json).</summary>
public static class ProfileStore
{
    /// <summary>App folder; set INVENTORY_DATA_DIR to use a different location (e.g. for testing).</summary>
    public static string Root { get; } = Environment.GetEnvironmentVariable("INVENTORY_DATA_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InventoryServer");

    static string FilePath => Path.Combine(Root, "servers.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<ServerProfile> Load()
    {
        Directory.CreateDirectory(Root);
        if (File.Exists(FilePath))
        {
            try { return JsonSerializer.Deserialize<List<ServerProfile>>(File.ReadAllText(FilePath)) ?? new(); }
            catch { return new(); }
        }
        var migrated = MigrateV1();
        if (migrated != null)
        {
            var list = new List<ServerProfile> { migrated };
            Save(list);
            return list;
        }
        return new();
    }

    public static void Save(IEnumerable<ServerProfile> profiles)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(profiles, Json));
    }

    /// <summary>Turns the single-server v1 setup (settings.json + inventory.db in the root) into the first profile, leaving the database in place.</summary>
    static ServerProfile? MigrateV1()
    {
        var old = Path.Combine(Root, "settings.json");
        if (!File.Exists(old) && !File.Exists(Path.Combine(Root, "inventory.db"))) return null;
        var p = new ServerProfile { Mode = AccessMode.Internal, DataDir = Root, Domain = "localhost", LocalPort = 8080 };
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(old));
            var r = doc.RootElement;
            if (r.TryGetProperty("Address", out var a) && a.GetString() is { Length: > 0 } addr) p.Domain = addr;
            if (r.TryGetProperty("Port", out var port) && port.TryGetInt32(out var n)) p.LocalPort = n;
            if (r.TryGetProperty("StartOnLaunch", out var s)) p.StartOnLaunch = s.ValueKind == JsonValueKind.True;
        }
        catch { }
        return p;
    }

    public static string DefaultDataDir(string company, string id)
    {
        var slug = new string(company.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "server";
        return Path.Combine(Root, "servers", $"{slug}-{id}");
    }

    public static int NextFreePort(IEnumerable<ServerProfile> existing, int start = 8081)
    {
        var used = existing.Select(p => p.LocalPort).ToHashSet();
        for (var port = start; port < 65000; port++)
            if (!used.Contains(port) && NetUtil.PortIsFree(port)) return port;
        return start;
    }
}
