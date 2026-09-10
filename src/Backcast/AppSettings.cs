using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backcast;

/// <summary>
/// Persisted to %APPDATA%\Backcast\settings.json — internal only; users set
/// everything through the wizard/settings window. The JSON never needs to be
/// opened by hand.
///
/// The user model is "transport + port/path": both the playback URL and the
/// OBS-side URL are derived, so they can never disagree.
/// </summary>
public sealed class AppSettings
{
    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Backcast");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    /// <summary>Set false after the first-run wizard completes.</summary>
    public bool FirstRun { get; set; } = true;

    /// <summary>"udp" (direct OBS → app) or "rtmp" (OBS/Aitum → MediaMTX relay → app).</summary>
    public string Transport { get; set; } = "udp";

    /// <summary>Local UDP port for the direct transport.</summary>
    public int UdpPort { get; set; } = 1234;

    /// <summary>Stream key/path for the relay transport (rtmp key = rtsp path).</summary>
    public string RtmpPath { get; set; } = "discord";

    public string DisplayName { get; set; } = Environment.UserName;

    public bool AutoStart { get; set; } = true;

    public bool AlwaysOnTop { get; set; }

    public AudioSettings Audio { get; set; } = new();

    public WindowSettings Window { get; set; } = new();

    public HotkeySettings Hotkeys { get; set; } = new();

    /// <summary>Advanced escape hatch — mpv name=value lines, empty for everyone normal.</summary>
    public List<string> ExtraMpvOptions { get; set; } = new();

    public bool UseRelay => Transport == "rtmp";

    /// <summary>The URL mpv plays.</summary>
    [JsonIgnore]
    public string PlaybackUrl => UseRelay
        ? $"rtsp://127.0.0.1:8554/{RtmpPath}"
        : $"udp://127.0.0.1:{UdpPort}?reuse=1&buffer_size=2097152";

    /// <summary>The URL the user pastes into OBS (or an Aitum custom-RTMP destination).</summary>
    [JsonIgnore]
    public string ObsUrl => UseRelay
        ? $"rtmp://127.0.0.1:1935/{RtmpPath}"
        : $"udp://127.0.0.1:{UdpPort}?pkt_size=1316";

    public sealed class AudioSettings
    {
        /// <summary>"silentEndpoint" (default) or "voiceCable".</summary>
        public string Mode { get; set; } = "silentEndpoint";

        /// <summary>Exact mpv audio-device name chosen in the wizard/settings.</summary>
        public string SelectedDevice { get; set; } = "";

        /// <summary>Legacy substring hint, kept as fallback when no device was picked.</summary>
        public string EndpointHint { get; set; } = "CABLE";
    }

    public sealed class WindowSettings
    {
        public int? X { get; set; }
        public int? Y { get; set; }
        public int W { get; set; } = 1280;
        public int H { get; set; } = 720;
        public bool Maximized { get; set; }
    }

    public sealed class HotkeySettings
    {
        public string Reload { get; set; } = "F9";
        public string ToggleTopmost { get; set; } = "F10";
    }

    // ---- persistence ----

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (settings != null) return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // corrupt/unreadable settings: fall through to defaults; the file
            // gets rewritten on the next save
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // read-only disk or roaming hiccup: keep running; settings just
            // won't persist this session
        }
    }
}
