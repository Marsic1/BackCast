using System.Diagnostics;
using System.IO.Compression;

namespace Backcast;

/// <summary>
/// One-click VB-Cable fetch: download the official driver pack, unpack it to
/// %APPDATA%\Backcast\vbcable, launch the x64 installer (elevated). The
/// installer itself needs one "Install driver" click from the user — that
/// step is a Windows driver installation, not automatable.
/// </summary>
internal static class VbCable
{
    // Official VB-Audio download (driver pack zip); the pack contains
    // VBCable_Setup_x64.exe / VBCable_Setup.exe.
    private const string DownloadUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_DriverPack43.zip";

    public static string Dir => Path.Combine(AppSettings.DirectoryPath, "vbcable");

    /// <summary>Returns the setup exe path; downloads first when needed.</summary>
    public static string EnsureDownloaded()
    {
        string setup = Path.Combine(Dir, "VBCable_Setup_x64.exe");
        if (File.Exists(setup)) return setup;

        Directory.CreateDirectory(Dir);
        string zipPath = Path.Combine(Path.GetTempPath(), "backcast-vbcable.zip");
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(4);
            using (var fs = File.Create(zipPath))
            using (var dl = http.GetStreamAsync(DownloadUrl).GetAwaiter().GetResult())
                dl.CopyTo(fs);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not download VB-Cable ({ex.GetType().Name}). " +
                "You can also get it manually from https://vb-audio.com/Cable/", ex);
        }
        try
        {
            ZipFile.ExtractToDirectory(zipPath, Dir, overwriteFiles: true);
        }
        finally
        {
            File.Delete(zipPath);
        }
        if (!File.Exists(setup))
            throw new InvalidOperationException("Setup executable not found in the download.");
        return setup;
    }

    /// <summary>Launches the installer (UAC prompt). Caller tells the user what to do next.</summary>
    public static void RunInstaller()
    {
        string setup = EnsureDownloaded();
        using var _ = Process.Start(new ProcessStartInfo
        {
            FileName = setup,
            WorkingDirectory = Dir,
            UseShellExecute = true, // required for the elevation prompt
        });
    }
}
