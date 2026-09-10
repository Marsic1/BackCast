using System.Text;

namespace Backcast;

/// <summary>
/// Finds OBS installations (running instance first, then standard install,
/// then remembered root), installs/repairs/uninstalls the plugin into the
/// correct flat Windows layout, and reads/writes the plugin's config.json
/// (portable-aware — same path rule libobs uses).
/// </summary>
internal static class PluginInstaller
{
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

    // ---- embedded payload ----

    private static byte[] Resource(string name)
    {
        using var s = typeof(PluginInstaller).Assembly.GetManifestResourceStream(name);
        if (s == null)
            throw new InvalidOperationException($"embedded resource '{name}' missing — build the plugin first");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // ---- install / uninstall ----

    /// <summary>Extracts the embedded plugin DLL + locale into the OBS install.</summary>
    public static void Install(ObsInstall obs)
    {
        string dllDir = Path.Combine(obs.RootPath, "obs-plugins", "64bit");
        string localeDir = Path.Combine(obs.DataDir, "locale");
        Directory.CreateDirectory(dllDir);
        Directory.CreateDirectory(localeDir);

        // OBS holds the DLL while running — replacing needs a restart anyway,
        // but the copy itself fails hard if OBS is running; surface that clearly.
        if (obs.IsInstalled && IsFileLocked(obs.PluginDll))
            throw new IOException("OBS is running and holds the old plugin — close OBS and try again.");

        File.WriteAllBytes(obs.PluginDll, Resource("Backcast.assets.backcast-projector.dll"));
        File.WriteAllBytes(Path.Combine(localeDir, "en-US.ini"),
            Resource("Backcast.assets.en-US.ini"));
        Log.Write($"plugin installed into {obs.RootPath} (portable={obs.IsPortable})");
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
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(obs.ConfigPath));
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
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
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(obs.ConfigPath));
                foreach (var kv in doc.RootElement.EnumerateObject())
                    values[kv.Name] = kv.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? kv.Value.GetString()
                        : kv.Value.GetRawText();
            }
        }
        catch { /* corrupt config: start fresh */ }
        values[key] = value;
        File.WriteAllText(obs.ConfigPath,
            System.Text.Json.JsonSerializer.Serialize(values, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
