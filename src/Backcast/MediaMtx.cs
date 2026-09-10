using System.Diagnostics;
using System.IO.Compression;

namespace Backcast;

/// <summary>
/// Manages the local MediaMTX relay (RTMP in :1935 → RTSP out :8554) for the
/// "Aitum Multistream" transport. Downloaded once on request into
/// %APPDATA%\Backcast\mediamtx, started hidden when needed, stopped with the
/// app. The user never sees or touches any of this.
/// </summary>
internal static class MediaMtx
{
    private const string Repo = "bluenviron/mediamtx";

    private static Process? _proc;

    public static string Dir => Path.Combine(AppSettings.DirectoryPath, "mediamtx");
    public static string ExePath => Path.Combine(Dir, "mediamtx.exe");

    public static bool IsDownloaded => File.Exists(ExePath);

    public static bool IsRunning => _proc is { HasExited: false };

    /// <summary>
    /// Resolves the current windows_amd64 asset URL. The version-less
    /// /releases/latest/download/ URL broke when assets gained version
    /// prefixes (mediamtx_v1.21.0_windows_amd64.zip), so ask the API.
    /// </summary>
    private static string ResolveAssetUrl()
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Backcast-setup");
        string json = http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest")
            .GetAwaiter().GetResult();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (name.Contains("windows_amd64") && name.EndsWith(".zip"))
                return asset.GetProperty("browser_download_url").GetString()
                       ?? throw new InvalidOperationException("asset entry without download URL");
        }
        throw new InvalidOperationException("no windows_amd64 asset in the latest release");
    }

    /// <summary>Downloads and unpacks the relay. Throws with a friendly message on failure.</summary>
    public static void EnsureDownloaded()
    {
        if (IsDownloaded) return;
        Directory.CreateDirectory(Dir);
        string zipPath = Path.Combine(Path.GetTempPath(), "backcast-mediamtx.zip");
        try
        {
            string url = ResolveAssetUrl();
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(4);
            using (var fs = File.Create(zipPath))
            using (var dl = http.GetStreamAsync(url).GetAwaiter().GetResult())
                dl.CopyTo(fs);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not download the relay ({ex.Message}). " +
                "Check your internet connection and try again.", ex);
        }
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e => e.Name == "mediamtx.exe")
                        ?? throw new InvalidOperationException("mediamtx.exe missing from the archive");
            entry.ExtractToFile(ExePath, overwrite: true);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    /// <summary>
    /// Minimal relay config: RTMP in, RTSP out; HLS/WebRTC/SRT/MoQ/API off so
    /// their ports (8888, 8889, 8890, 8892…) can never conflict with other
    /// software — MediaMTX shuts down entirely if ANY listener fails to bind,
    /// which is exactly what happened with port 8888 on the dev machine.
    /// </summary>
    private const string ConfigYml = """
        # Managed by Backcast — RTMP in, RTSP out, everything else off.
        rtmp: yes
        rtsp: yes
        rtspTransports: [tcp]
        hls: no
        webrtc: no
        srt: no
        moq: no
        api: no
        metrics: no
        pprof: no
        playback: no
        paths:
          all_others:
        """;

    public static string ConfigPath => Path.Combine(Dir, "mediamtx.yml");

    /// <summary>
    /// Path the current publisher is pushing (parsed from the relay's own
    /// output). RTMP clients append the stream key to the server path
    /// (rtmp://host/discord + key "discord" → "discord/discord"), so the
    /// app follows whatever path is actually live instead of assuming.
    /// </summary>
    public static string? LatestPublishedPath { get; private set; }

    private static void OnRelayLine(object sender, DataReceivedEventArgs e)
    {
        string? line = e.Data;
        if (string.IsNullOrEmpty(line)) return;
        Log.Write($"mediamtx: {line}");
        // e.g. "INF [RTMP] [conn 127.0.0.1:1234] is publishing to path 'discord/discord'"
        const string marker = "is publishing to path '";
        int idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            int start = idx + marker.Length;
            int end = line.IndexOf('\'', start);
            if (end > start)
                LatestPublishedPath = line[start..end];
        }
    }

    /// <summary>Starts the relay hidden (idempotent). Returns false when it can't run.</summary>
    public static bool Start()
    {
        if (IsRunning) return true;
        if (!IsDownloaded) return false;

        // A relay from a previous app session can outlive the app. Its output
        // pipe belongs to the dead parent, so we can't watch its paths —
        // kill it and start our own to keep the path-follow pipeline alive.
        if (RelayIsAlive())
        {
            Log.Write("mediamtx: taking over an already-running relay");
            KillOrphans();
            Thread.Sleep(400); // let the ports free up
        }

        try
        {
            File.WriteAllText(ConfigPath, ConfigYml);
            _proc = Process.Start(new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = $"\"{ConfigPath}\"",
                WorkingDirectory = Dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (_proc == null) return false;
            _proc.OutputDataReceived += OnRelayLine;
            _proc.ErrorDataReceived += OnRelayLine;
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            // MediaMTX exits outright when any listener can't bind — verify
            // it survived startup before claiming success.
            Thread.Sleep(750);
            if (_proc.HasExited)
            {
                Log.Write($"mediamtx exited immediately (code {_proc.ExitCode}) — see log above");
                _proc = null;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"mediamtx start failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>TCP-probe the RTSP port: is a relay already serving?</summary>
    private static bool RelayIsAlive()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connect = client.BeginConnect("127.0.0.1", 8554, null, null);
            bool ok = connect.AsyncWaitHandle.WaitOne(400);
            if (ok && client.Connected)
            {
                client.EndConnect(connect);
                return true;
            }
        }
        catch { /* nothing listening */ }
        return false;
    }

    /// <summary>Kills any mediamtx.exe running from OUR install directory.</summary>
    private static void KillOrphans()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("mediamtx"))
            {
                try
                {
                    if (string.Equals(proc.MainModule?.FileName, ExePath, StringComparison.OrdinalIgnoreCase))
                        proc.Kill(true);
                }
                catch { /* access denied or already exiting */ }
            }
        }
        catch { }
    }

    public static void Stop()
    {
        try
        {
            if (IsRunning) _proc!.Kill(true);
        }
        catch { /* already gone */ }
        _proc = null;
    }
}
