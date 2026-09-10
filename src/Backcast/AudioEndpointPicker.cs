namespace Backcast;

/// <summary>
/// Resolves the mpv "audio-device" to route the stream into a non-audible
/// endpoint (VB-Cable Input or an unused HDMI/monitor output) so the user
/// hears nothing while Discord's per-process capture still gets the audio.
///
/// Note: the spec wanted this set before mpv_initialize, but
/// audio-device-list only exists on an initialized context — selection runs
/// right after init instead (documented deviation; runtime switching via
/// "set audio-device" is per spec).
/// </summary>
internal static class AudioEndpointPicker
{
    /// <summary>
    /// Finds an endpoint matching the hint (case-insensitive substring on
    /// description and name). Returns null when nothing matches.
    /// </summary>
    public static MpvPlayer.AudioDevice? Find(
        IReadOnlyList<MpvPlayer.AudioDevice> devices, string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        foreach (var d in devices)
        {
            if (d.Description.Contains(hint, StringComparison.OrdinalIgnoreCase)
                || d.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return d;
        }
        return null;
    }
}
