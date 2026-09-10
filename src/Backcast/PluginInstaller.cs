using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Backcast;

/// <summary>
/// Finds OBS installations (running instance first, then standard install,
/// then remembered root), installs/repairs/uninstalls the plugin into the
/// correct flat Windows layout, and reads/writes the plugin's config.json
/// (portable-aware — same path rule libobs uses).
///
/// The plugin payload is NOT embedded: it is downloaded from the latest
/// GitHub release, so the exe can never install a stale plugin. The
/// installed version is read from the DLL's version resource (stamped by
/// the plugin build from its buildspec version).
/// </summary>
internal static class PluginInstaller
{
    private const string Repo = "Marsic1/BackCast";
    private const string PluginZipName = "backcast-plugin-windows-x64.zip";

    // standard zip layout packaged by plugin/package-plugin.ps1
    private const string ZipDllSuffix = "bin/64bit/backcast-projector.dll";
    private const string ZipLocaleSuffix = "data/locale/en-US.ini";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Backcast/{typeof(PluginInstaller).Assembly.GetName().Version}");
        c.Timeout = TimeSpan.FromSeconds(60);
        return c;
    }

    internal sealed record ObsInstall(string RootPath, bool IsPortable)
    {
        public string PluginDll => Path.Combine(RootPath, "obs-plugins", "64bit", "backcast-projector.dll");
        public string DataDir => Path.Combine(RootPath, "data", "obs-plugins", "backcast-projector");
        public bool IsInstalled => File.Exists(PluginDll);

        /// <summary>Plugin config dir: portable → root\config, else %APPDATA%.</summary>
        public string ConfigDir => Path.Combine(IsPortable
                ? Path.Combine(RootPath, "config", "obs-studio")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio"),
            "plugin_config", "backcast-projector");
        public string ConfigPath => Path.Combine(ConfigDir, "config.json");

        public string Title => (IsPortable ? "Portable" : "Standard") + " — " + RootPath;
    }

    private static string ExeRoot(string exePath) => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(exePath))!);

    public static bool LooksLikeObsRoot(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try { return File.Exists(Path.Combine(dir, "bin", "64bit", "obs64.exe")); }
        catch { return false; }
    }

    public static bool IsPortable(string root)
    {
        try
        {
            return File.Exists(Path.Combine(root, "obs_portable_mode.txt"))
                || File.Exists(Path.Combine(root, "portable_mode.txt"));
        }
        catch { return false; }
    }

    /// <summary>All OBS installs we can find, most-likely first.</summary>
    public static List<ObsInstall> FindObs()
    {
        var roots = new List<string>();

        // 1. a running OBS is authoritative
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("obs64"))
            {
                try
                {
                    string? exe = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exe))
                        roots.Add(ExeRoot(exe));
                }
                catch { /* access denied on some modules */ }
            }
        }
        catch { }

        // 2. standard install location
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio"));

        return roots.Where(LooksLikeObsRoot)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(r => new ObsInstall(r, IsPortable(r)))
                    .ToList();
    }

    // ---- versions ----

    /// <summary>Version of the installed plugin DLL (from its version resource).</summary>
    public static Version? GetInstalledVersion(ObsInstall obs)
    {
        try
        {
            if (!obs.IsInstalled) return null;
            return Version.TryParse(
                System.Diagnostics.FileVersionInfo.GetVersionInfo(obs.PluginDll).FileVersion, out var v)
                ? v : null;
        }
        catch { return null; }
    }

    /// <summary>Latest published plugin version from GitHub releases (null offline).</summary>
    public static async Task<Version?> FetchLatestVersionAsync()
    {
        try
        {
            using var json = await Http.GetStreamAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = await JsonDocument.ParseAsync(json);
            string? tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(tag)) return null;
            return Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? v : null;
        }
        catch { return null; }
    }

    // ---- download + install ----

    /// <summary>Downloads the plugin zip from the latest GitHub release and
    /// extracts DLL + locale. Throws with a plain message on network/format trouble.</summary>
    private static async Task<(byte[] Dll, byte[] Locale)> DownloadLatestAsync()
    {
        byte[] zip;
        try
        {
            zip = await Http.GetByteArrayAsync(
                $"https://github.com/{Repo}/releases/latest/download/{PluginZipName}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new IOException(
                $"the latest plugin zip ({PluginZipName}) isn't on github.com/{Repo} releases yet — try again later.", ex);
        }
        catch (Exception ex)
        {
            throw new IOException($"couldn't download the plugin from github.com/{Repo} — check your internet connection.", ex);
        }

        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip));
            // match by suffix — Compress-Archive and spec-compliant zips differ in separators
            static ZipArchiveEntry? Find(ZipArchive a, string suffix) =>
                a.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            var dll = Find(archive, ZipDllSuffix)
                ?? throw new InvalidDataException("the downloaded zip has no plugin DLL");
            var locale = Find(archive, ZipLocaleSuffix)
                ?? throw new InvalidDataException("the downloaded zip has no locale file");

            static async Task<byte[]> ReadAllAsync(ZipArchiveEntry e)
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                return ms.ToArray();
            }
            return (await ReadAllAsync(dll), await ReadAllAsync(locale));
        }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("the downloaded zip"))
        {
            throw new IOException(ex.Message);
        }
        catch (InvalidDataException)
        {
            throw new IOException("the downloaded file is not a valid plugin zip — try again.");
        }
    }

    /// <summary>Downloads the latest plugin from GitHub and installs it into
    /// the OBS install. Never touches config.json, so the user's audio device
    /// and window title survive updates and repairs.</summary>
    public static async Task InstallAsync(ObsInstall obs)
    {
        // OBS holds the DLL while running — replacing needs a restart anyway,
        // but the copy itself fails hard if OBS is running; surface that clearly.
        if (obs.IsInstalled && IsFileLocked(obs.PluginDll))
            throw new IOException("OBS is running and holds the old plugin — close OBS and try again.");

        var (dll, locale) = await DownloadLatestAsync();

        string dllDir = Path.Combine(obs.RootPath, "obs-plugins", "64bit");
        string localeDir = Path.Combine(obs.DataDir, "locale");
        Directory.CreateDirectory(dllDir);
        Directory.CreateDirectory(localeDir);
        File.WriteAllBytes(obs.PluginDll, dll);
        File.WriteAllBytes(Path.Combine(localeDir, "en-US.ini"), locale);
        Log.Write($"plugin installed into {obs.RootPath} (portable={obs.IsPortable}, downloaded from GitHub)");
    }

    public static void Uninstall(ObsInstall obs)
    {
        if (IsFileLocked(obs.PluginDll))
            throw new IOException("OBS is running and holds the plugin — close OBS and try again.");
        try { File.Delete(obs.PluginDll); } catch (IOException) { throw; }
        try { Directory.Delete(obs.DataDir, true); } catch (IOException) when (!Directory.Exists(obs.DataDir)) { }
        Log.Write($"plugin uninstalled from {obs.RootPath}");
    }

    private static bool IsFileLocked(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var f = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
    }

    // ---- plugin config.json ----

    /// <summary>Reads a string field from the plugin's config.json (null if missing).</summary>
    public static string? GetPluginConfig(ObsInstall obs, string key)
    {
        try
        {
            if (!File.Exists(obs.ConfigPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(obs.ConfigPath));
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>Merges one string field into the plugin's config.json.</summary>
    public static void SetPluginConfig(ObsInstall obs, string key, string value)
    {
        Directory.CreateDirectory(obs.ConfigDir);
        var values = new Dictionary<string, object?>();
        try
        {
            if (File.Exists(obs.ConfigPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(obs.ConfigPath));
                foreach (var kv in doc.RootElement.EnumerateObject())
                    values[kv.Name] = kv.Value.ValueKind == JsonValueKind.String
                        ? kv.Value.GetString()
                        : kv.Value.GetRawText();
            }
        }
        catch { /* corrupt config: start fresh */ }
        values[key] = value;
        File.WriteAllText(obs.ConfigPath,
            JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
    }
}
