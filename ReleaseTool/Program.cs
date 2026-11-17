// Release helper for Inventory Server self-updates.
//
//   dotnet run --project ReleaseTool -- init <owner/repo>
//       Creates the release signing key (once) and writes the repo name and public key into InventoryServer\Updater.cs.
//       The PRIVATE key is saved outside the source tree; back it up somewhere safe. Without it you cannot ship updates
//       to installs that already trust it.
//
//   dotnet run --project ReleaseTool -- pack <published app folder> <output folder> <version> [notes]
//       Zips the app and writes a signed update.json next to it. release.ps1 calls this.
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var keyPath = Environment.GetEnvironmentVariable("INVENTORY_RELEASE_KEY") is { Length: > 0 } custom
    ? custom
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InventoryServer-release", "signing-key.pem");

const string ZipName = "InventoryServer-win-x64.zip";

try
{
    switch (args.FirstOrDefault())
    {
        case "init" when args.Length == 2:
            return Init(args[1]);
        case "pack" when args.Length is 4 or 5:
            return Pack(args[1], args[2], args[3], args.Length == 5 ? args[4] : "");
        default:
            Console.Error.WriteLine("Usage:\n  init <owner/repo>\n  pack <appFolder> <outFolder> <version> [notes]");
            return 2;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine("ERROR: " + e.Message);
    return 1;
}

int Init(string repo)
{
    if (!Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new ArgumentException("Repo must look like owner/name.");
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    if (File.Exists(keyPath))
    {
        key.ImportFromPem(File.ReadAllText(keyPath));
        Console.WriteLine($"Using existing signing key: {keyPath}");
    }
    else
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        Console.WriteLine($"Created signing key: {keyPath}");
        Console.WriteLine("BACK THIS FILE UP (e.g. password manager). Keep it private: anyone with it can push updates to every install.");
    }
    var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    var updater = FindUpdaterSource();
    var src = File.ReadAllText(updater);
    src = Regex.Replace(src, @"public const string Repo = "".*?"";", $"public const string Repo = \"{repo}\";");
    src = Regex.Replace(src, @"public const string PublicKey = "".*?"";", $"public const string PublicKey = \"{publicKey}\";");
    File.WriteAllText(updater, src);
    Console.WriteLine($"Updated {updater} (repo {repo}).");
    Console.WriteLine("Rebuild and install this version once by hand; from then on it updates itself.");
    return 0;
}

int Pack(string appDir, string outDir, string version, string notes)
{
    if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) throw new ArgumentException("Version must look like 1.2.3.");
    if (!File.Exists(Path.Combine(appDir, "InventoryServer.exe"))) throw new FileNotFoundException($"No InventoryServer.exe in {appDir}.");
    if (!File.Exists(keyPath)) throw new FileNotFoundException($"Signing key not found at {keyPath}. Run 'init' first (or set INVENTORY_RELEASE_KEY).");

    Directory.CreateDirectory(outDir);
    var zip = Path.Combine(outDir, ZipName);
    File.Delete(zip);
    ZipFile.CreateFromDirectory(appDir, zip, CompressionLevel.Optimal, includeBaseDirectory: false);

    string sha;
    using (var f = File.OpenRead(zip))
        sha = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();

    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(keyPath));
    // Must match InventoryServer.Updater.SignedPayload.
    var payload = Encoding.UTF8.GetBytes($"InventoryServer-update\n{version}\n{ZipName}\n{sha}");
    var signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256));

    var manifest = new { version, file = ZipName, sha256 = sha, signature, notes };
    File.WriteAllText(Path.Combine(outDir, "update.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Packed {zip} ({new FileInfo(zip).Length / 1024 / 1024} MB) and signed update.json for version {version}.");
    return 0;
}

static string FindUpdaterSource()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "InventoryServer", "Updater.cs");
        if (File.Exists(candidate)) return candidate;
    }
    throw new FileNotFoundException("Could not find InventoryServer\\Updater.cs. Run this from the solution folder.");
}
