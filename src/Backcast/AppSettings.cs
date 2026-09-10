using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backcast;

/// <summary>
/// Slim settings for the v0.3 plugin architecture: the exe is an
/// installer/config UI for the OBS plugin, so nearly everything (endpoint,
/// hotkeys, window bounds) lives in the plugin's own config next to OBS.
/// </summary>
internal sealed class AppSettings
{
    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Backcast");

    public bool FirstRun { get; set; } = true;

    /// <summary>Remembered OBS root (the folder containing bin\64bit\obs64.exe).</summary>
    public string ObsRoot { get; set; } = "";

    // ---- persistence ----

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Backcast", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { /* unreadable settings fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Write($"settings save failed: {ex.Message}");
        }
    }
}
