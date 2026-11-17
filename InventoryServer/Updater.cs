using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InventoryServer;

/// <summary>What a release publishes next to its zip (update.json).</summary>
public record UpdateManifest(string Version, string File, string Sha256, string Signature, string Notes = "");

/// <summary>
/// Self-update from GitHub Releases.
/// Each release carries InventoryServer-win-x64.zip and update.json; update.json is signed (ECDSA P-256) with a private key
/// that only lives on the build PC, and the app refuses any update whose signature does not match <see cref="PublicKey"/>.
///
/// Installing: the new version is unpacked to a staging folder and started with --apply-update. It waits for this process to
/// exit, backs up the app folder, copies itself over it (restoring the backup on failure) and starts the updated app again.
/// Company data lives in %LocalAppData%\InventoryServer and is never touched.
/// </summary>
public static class Updater
{
    /// <summary>GitHub "owner/repo" whose releases contain updates. Set by `ReleaseTool init`.</summary>
    public const string Repo = "abyssalsoldier/InventoryServer";

    /// <summary>Base64 SubjectPublicKeyInfo of the release signing key. Set by `ReleaseTool init`.</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEEx9VROPj2Ij8QJMhNDj/lXGGI3e8bBs2xHyhXBZ/3pD7FQOyOqurWsgc6kk2Bba2U5K03JzT6XgKq7XmFFzBAA==";

    public const string ZipName = "InventoryServer-win-x64.zip";
    public const string ManifestName = "update.json";

    public static bool Configured => Repo != "OWNER/REPO" && PublicKey.Length > 0;

    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));

    /// <summary>Version as shown to people and used for release tags: "V1.2.3".</summary>
    public static string CurrentText => Display($"{Current.Major}.{Current.Minor}.{Current.Build}");

    /// <summary>"1.2.3" or "V1.2.3" → "V1.2.3".</summary>
    public static string Display(string version) => "V" + Plain(version);

    /// <summary>"V1.2.3" → "1.2.3" (the form stored in update.json and compared).</summary>
    public static string Plain(string version) => version.Trim().TrimStart('V', 'v');

    static string WorkDir => Path.Combine(ProfileStore.Root, "update");

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static HttpClient NewClient(TimeSpan timeout)
    {
        var c = new HttpClient { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"InventoryServer/{CurrentText}");
        return c;
    }

    /// <summary>The text the release tool signs: binds the version to the exact zip.</summary>
    public static byte[] SignedPayload(string version, string file, string sha256) =>
        Encoding.UTF8.GetBytes($"InventoryServer-update\n{version}\n{file}\n{sha256.ToLowerInvariant()}");

    /// <summary>Returns the newer release, or null when up to date. Throws on network or signature problems.</summary>
    public static async Task<UpdateManifest?> CheckAsync(CancellationToken ct = default)
    {
        if (!Configured) return null;
        using var http = NewClient(TimeSpan.FromSeconds(30));
        var json = await http.GetStringAsync($"https://github.com/{Repo}/releases/latest/download/{ManifestName}", ct);
        var m = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The update information could not be read.");
        if (!Version.TryParse(Plain(m.Version), out var v)) throw new InvalidDataException("The update has an invalid version number.");
        if (m.File != ZipName || !System.Text.RegularExpressions.Regex.IsMatch(m.Sha256, "^[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("The update information is malformed.");
        VerifySignature(m);
        return Normalize(v) > Current ? m : null;
    }

    static void VerifySignature(UpdateManifest m)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
        byte[] sig;
        try { sig = Convert.FromBase64String(m.Signature); }
        catch (FormatException) { throw new CryptographicException("The update signature is invalid."); }
        if (!key.VerifyData(SignedPayload(m.Version, m.File, m.Sha256), sig, HashAlgorithmName.SHA256))
            throw new CryptographicException("The update is not signed with this app's release key, so it was refused.");
    }

    /// <summary>True if this process can replace its own files (e.g. not installed under Program Files).</summary>
    public static bool CanWriteAppFolder()
    {
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Downloads and verifies the release, unpacks it, and returns the staged exe path.</summary>
    public static async Task<string> DownloadAsync(UpdateManifest m, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Cleanup();
        Directory.CreateDirectory(WorkDir);
        var zip = Path.Combine(WorkDir, $"{m.Version}.zip");
        progress?.Report($"Downloading {Display(m.Version)}…");
        using (var http = NewClient(TimeSpan.FromMinutes(10)))
        await using (var src = await http.GetStreamAsync($"https://github.com/{Repo}/releases/download/{Display(m.Version)}/{m.File}", ct))
        await using (var dst = File.Create(zip))
            await src.CopyToAsync(dst, ct);

        progress?.Report("Verifying…");
        string actual;
        await using (var f = File.OpenRead(zip))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
        if (actual != m.Sha256.ToLowerInvariant())
        {
            File.Delete(zip);
            throw new CryptographicException("The downloaded update does not match its signed checksum, so it was refused.");
        }

        progress?.Report("Unpacking…");
        var staging = Path.Combine(WorkDir, $"staging-{m.Version}");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zip, staging);
        File.Delete(zip);
        var exe = Path.Combine(staging, "InventoryServer.exe");
        if (!File.Exists(exe)) throw new InvalidDataException("The update package does not contain InventoryServer.exe.");
        return exe;
    }

    /// <summary>Starts the staged copy, which finishes the install once this process has exited.</summary>
    public static void LaunchInstaller(string stagedExe, IEnumerable<string> resumeServerIds)
    {
        var psi = new ProcessStartInfo(stagedExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(stagedExe)! };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add(AppContext.BaseDirectory.TrimEnd('\\'));
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add(string.Join(",", resumeServerIds));
        Process.Start(psi);
    }

    /// <summary>Removes leftovers of earlier updates (ignored if still in use).</summary>
    public static void Cleanup()
    {
        if (!Directory.Exists(WorkDir)) return;
        foreach (var d in Directory.GetDirectories(WorkDir, "staging-*"))
            try { Directory.Delete(d, true); } catch { }
        foreach (var f in Directory.GetFiles(WorkDir, "*.zip"))
            try { File.Delete(f); } catch { }
    }

    static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(WorkDir);
            File.AppendAllText(Path.Combine(WorkDir, "update.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// Runs in the staged copy: <c>--apply-update &lt;appDir&gt; &lt;oldPid&gt; &lt;resumeIds&gt;</c>.
    /// Replaces the app folder's files with this folder's files and restarts the app.
    /// </summary>
    public static void ApplyUpdate(string[] args)
    {
        var appDir = args[1];
        var resume = args.Length > 3 ? args[3] : "";
        var source = AppContext.BaseDirectory.TrimEnd('\\');
        Log($"Installing {CurrentText} from {source} into {appDir}");

        if (int.TryParse(args[2], out var pid))
        {
            try { using var old = Process.GetProcessById(pid); old.WaitForExit(60_000); }
            catch (ArgumentException) { /* already gone */ }
        }

        // Only the files the update contains are backed up and replaced; anything else in the app folder is left alone.
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(source, f)).ToList();
        var backup = Path.Combine(WorkDir, "previous-version");
        try
        {
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            CopyFiles(appDir, backup, files.Where(f => File.Exists(Path.Combine(appDir, f))));
            CopyFiles(source, appDir, files);
            Log("Installed.");
        }
        catch (Exception e)
        {
            Log("FAILED: " + e.Message + " – restoring the previous version.");
            try { CopyFiles(backup, appDir, Directory.GetFiles(backup, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(backup, f))); Log("Previous version restored."); }
            catch (Exception e2) { Log("Restore FAILED: " + e2.Message + $" – copy the files from {backup} back manually."); }
            MessageBox.Show($"The update could not be installed:\n{e.Message}\n\nThe previous version was restored. Details: {Path.Combine(WorkDir, "update.log")}",
                "Inventory Server update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        var psi = new ProcessStartInfo(Path.Combine(appDir, "InventoryServer.exe")) { UseShellExecute = false, WorkingDirectory = appDir };
        psi.ArgumentList.Add("--updated");
        psi.ArgumentList.Add(resume);
        Process.Start(psi);
    }

    /// <summary>Copies the given relative paths from one folder to another, retrying files that are briefly locked.</summary>
    static void CopyFiles(string from, string to, IEnumerable<string> relativePaths)
    {
        foreach (var rel in relativePaths)
        {
            var target = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            for (var attempt = 1; ; attempt++)
            {
                try { File.Copy(Path.Combine(from, rel), target, overwrite: true); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(500); }
            }
        }
    }
}
