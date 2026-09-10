using System.Runtime.InteropServices;
using Backcast.Native;

namespace Backcast;

/// <summary>
/// Owns the libmpv context: lifecycle, options, loadfile, the event thread
/// and the reconnect watchdog. Raises StateChanged on background threads —
/// UI code must marshal.
///
/// Audio rule: this class never touches the app's Windows audio session
/// (no mute, no volume) — muting locally would mute what Discord captures.
/// Endpoint routing is done via the mpv "audio-device" property only.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    // observe-property userdata slots
    private const ulong UdCoreIdle = 1;
    private const ulong UdEof = 2;
    private const ulong UdPausedForCache = 3;
    private const ulong UdAspect = 5;
    private const ulong UdTimePos = 4;

    // watchdog tuning (per spec)
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan NoDataThreshold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    // time-pos must tick at least this often to count as advancing (60 fps stream)
    private static readonly TimeSpan PositionAlive = TimeSpan.FromMilliseconds(1200);

    private IntPtr _ctx;
    private Thread? _eventThread;
    private Thread? _watchdogThread;
    private volatile bool _running;
    private readonly object _stateLock = new();
    private readonly object _cmdLock = new();

    // observed-property snapshot (guarded by _stateLock)
    private bool _coreIdle = true;
    private bool _eof;
    private double _timePos;
    private DateTime _lastPosChangeUtc = DateTime.MinValue;

    private DateTime _lastLoadAttemptUtc = DateTime.MinValue;
    private DateTime _startUtc = DateTime.MinValue;
    private PlaybackState _state = PlaybackState.Waiting;
    private string _errorText = "";

    public IntPtr VideoHandle { get; }
    public string StreamUrl { get; }
    public IReadOnlyList<string> ExtraOptions { get; }

    /// <summary>Fires on background threads. state + human-readable detail.</summary>
    public event Action<PlaybackState, string>? StateChanged;

    /// <summary>Stream aspect ratio (w/h) when it changes — resolution/aspect changes from OBS.</summary>
    public event Action<double>? StreamAspectChanged;

    public PlaybackState State { get { lock (_stateLock) return _state; } }

    public MpvPlayer(IntPtr videoHandle, string streamUrl, IReadOnlyList<string> extraOptions)
    {
        VideoHandle = videoHandle;
        StreamUrl = streamUrl;
        ExtraOptions = extraOptions;

        _ctx = Mpv.mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create() failed — is mpv-2.dll next to the exe?");

        ApplyOptions();

        int rc = Mpv.mpv_initialize(_ctx);
        if (rc < 0)
        {
            Mpv.mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
            throw new InvalidOperationException($"mpv_initialize failed (code {rc})");
        }

        int rc1 = Mpv.ObserveProperty(_ctx, UdCoreIdle, "core-idle", Mpv.FormatFlag);
        int rc2 = Mpv.ObserveProperty(_ctx, UdTimePos, "time-pos", Mpv.FormatDouble);
        if (rc1 < 0 || rc2 < 0)
            Log.Write($"observe failed: core-idle rc={rc1} time-pos rc={rc2}");
        Mpv.ObserveProperty(_ctx, UdEof, "eof-reached", Mpv.FormatFlag);
        Mpv.ObserveProperty(_ctx, UdPausedForCache, "paused-for-cache", Mpv.FormatFlag);
        Mpv.ObserveProperty(_ctx, UdAspect, "video-params/aspect", Mpv.FormatDouble);
        _startUtc = DateTime.UtcNow;

        _running = true;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _watchdogThread = new Thread(WatchdogLoop) { IsBackground = true, Name = "mpv-watchdog" };
        _eventThread.Start();
        _watchdogThread.Start();
    }

    private void ApplyOptions()
    {
        SetOpt("wid", VideoHandle.ToInt64().ToString());
        // low-latency, but not to the point of starvation: the previous
        // setup (nobuffer + 50ms audio + reorder_queue_size=0) starved the
        // demuxer on the relay path — time-pos ticked only in 2s bursts,
        // which the watchdog read as "no data" and reloaded every 2s,
        // restarting the probe cycle forever (observed as ~2s+ latency and
        // relay connection churn). Keep the profile + framedrop, drop the
        // starvation options.
        SetOpt("profile", "low-latency");
        // audio pacing adds up to one audio-buffer of video delay (video
        // waits for its synced audio frame): 33 ms is the practical floor
        SetOpt("audio-buffer", "0.033");
        // A SMALL demuxer cache (not none): with cache=no any network
        // jitter starves the decoder and, combined with display-desync,
        // froze the picture while time-pos kept ticking (watchdog blinded).
        // The readahead/hysteresis pair is the steady-state live-edge lag:
        // the demuxer keeps ~readahead+hysteresis buffered ahead of the
        // display point, so keep both as small as jitter tolerance allows.
        // 0.2 + 0.1 absorbs momentary gaps without a visible half-second.
        SetOpt("cache", "yes");
        SetOpt("demuxer-readahead-secs", "0.2");
        SetOpt("demuxer-hysteresis-secs", "0.1");
        SetOpt("demuxer-max-bytes", "15728640");   // 15 MiB hard cap
        SetOpt("demuxer-max-back-bytes", "2097152"); // 2 MiB back-buffer
        SetOpt("hwdec", "auto-safe");
        SetOpt("network-timeout", "5");
        SetOpt("framedrop", "vo");
        SetOpt("keep-open", "no");
        SetOpt("osc", "no");
        SetOpt("input-default-bindings", "no");
        SetOpt("input-vo-keyboard", "no");

        // Probe SMALL: on a live stream the probe amount becomes permanent
        // latency — the demuxer buffers that much stream before the first
        // frame, and live RTSP/UDP has no history to catch up with (5 MB at
        // ~5 Mbps measured as 12.5 s start delay and a permanently lagging
        // player). 512 KB ≈ 1 s of stream: enough to reach the first keyframe
        // with a 1 s GOP (the spec's 32 KB could not, which is why its
        // loadfile failed), with analyzeduration capped at 500 ms.
        // NOTE: key=value list options are COMMA-separated in mpv.
        string lavf = "probesize=512000,analyzeduration=500000";
        if (StreamUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            lavf += ",rtsp_transport=tcp";
        SetOpt("demuxer-lavf-o", lavf);

        foreach (string opt in ExtraOptions)
        {
            string trimmed = opt.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            int eq = trimmed.IndexOf('=');
            if (eq <= 0) continue; // ignore malformed lines rather than dying
            SetOpt(trimmed[..eq], trimmed[(eq + 1)..]);
        }
    }

    private void SetOpt(string name, string value)
    {
        int rc = Mpv.SetOptionString(_ctx, name, value);
        if (rc < 0)
            Log.Write($"option '{name}'='{value}' rejected (code {rc})");
    }

    /// <summary>(Re)loads the stream URL. Safe to call from any thread, any time.</summary>
    public void Load()
    {
        lock (_cmdLock)
        {
            lock (_stateLock)
            {
                _lastLoadAttemptUtc = DateTime.UtcNow;
                _lastPosChangeUtc = DateTime.MinValue;
            }
            int rc = Mpv.Command(_ctx, "loadfile", StreamUrl, "replace");
            if (rc < 0)
                SetState(PlaybackState.Error, $"loadfile rejected (code {rc}) — check the stream URL in Settings");
        }
    }

    // ---- audio ----

    public sealed record AudioDevice(string Name, string Description);

    public IReadOnlyList<AudioDevice> GetAudioDevices()
    {
        var result = new List<AudioDevice>();
        var node = new Mpv.MpvNode();
        IntPtr namePtr = Mpv.AllocUtf8("audio-device-list");
        try
        {
            if (Mpv.mpv_get_property(_ctx, namePtr, Mpv.FormatNode, ref node) >= 0)
            {
                try
                {
                    // audio-device-list is returned as a NODE *array* of maps
                    if (node.Format == Mpv.FormatNodeArray && node.U != IntPtr.Zero)
                        ParseDeviceList(node.U, result);
                }
                finally
                {
                    Mpv.mpv_free_node_contents(ref node);
                }
            }
        }
        finally
        {
            Mpv.FreeUtf8(namePtr);
        }
        return result;
    }

    private static void ParseDeviceList(IntPtr listPtr, List<AudioDevice> result)
    {
        if (listPtr == IntPtr.Zero) return;
        var list = Marshal.PtrToStructure<Mpv.MpvNodeList>(listPtr);
        if (list.Num <= 0 || list.Values == IntPtr.Zero) return; // empty array: values can be NULL
        int nodeSize = Marshal.SizeOf<Mpv.MpvNode>();
        for (int i = 0; i < list.Num; i++)
        {
            var entry = Marshal.PtrToStructure<Mpv.MpvNode>(list.Values + i * nodeSize);
            if (entry.Format != Mpv.FormatNodeMap || entry.U == IntPtr.Zero) continue;
            var map = Marshal.PtrToStructure<Mpv.MpvNodeList>(entry.U);
            if (map.Num <= 0) continue;
            string? name = null, desc = null;
            for (int k = 0; k < map.Num; k++)
            {
                if (map.Keys == IntPtr.Zero || map.Values == IntPtr.Zero) break;
                string? key = Mpv.PtrToUtf8(Marshal.ReadIntPtr(map.Keys + k * IntPtr.Size));
                var val = Marshal.PtrToStructure<Mpv.MpvNode>(map.Values + k * nodeSize);
                if (val.Format == Mpv.FormatString)
                {
                    string? s = Mpv.PtrToUtf8(val.U);
                    if (key == "name") name = s;
                    else if (key == "description") desc = s;
                }
            }
            if (name != null)
                result.Add(new AudioDevice(name, desc ?? name));
        }
    }

    /// <summary>Runtime audio-device switch — no reload needed.</summary>
    public bool SetAudioDevice(string device) =>
        Mpv.SetPropertyString(_ctx, "audio-device", device) >= 0;

    // ---- event thread ----

    private void EventLoop()
    {
        while (_running)
        {
            IntPtr evPtr = Mpv.mpv_wait_event(_ctx, 0.25);
            if (evPtr == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<Mpv.MpvEvent>(evPtr);
            if (ev.EventId is Mpv.EventFileLoaded or Mpv.EventEndFile
                or Mpv.EventStartFile or Mpv.EventIdle)
            {
                Log.Write($"event id={ev.EventId} err={ev.Error} ud={ev.ReplyUserdata}");
            }
            switch (ev.EventId)
            {
                case Mpv.EventShutdown:
                    Log.Write("event shutdown");
                    _running = false;
                    return;

                case Mpv.EventPropertyChange:
                    HandlePropertyChange(ev);
                    break;

                case Mpv.EventFileLoaded:
                    // stream opened and demuxed; live-ness is confirmed by the
                    // watchdog once time-pos actually advances
                    break;

                case Mpv.EventEndFile:
                    HandleEndFile(ev);
                    break;
            }
        }
    }

    private void HandlePropertyChange(Mpv.MpvEvent ev)
    {
        if (ev.Data == IntPtr.Zero) return;
        var prop = Marshal.PtrToStructure<Mpv.MpvEventProperty>(ev.Data);
        string? name = Mpv.PtrToUtf8(prop.Name);

        lock (_stateLock)
        {
            switch (ev.ReplyUserdata)
            {
                case UdCoreIdle:
                    _coreIdle = prop.Format == Mpv.FormatFlag && Marshal.ReadInt32(prop.Data) != 0;
                    break;
                case UdEof:
                    _eof = prop.Format == Mpv.FormatFlag && Marshal.ReadInt32(prop.Data) != 0;
                    break;
                case UdPausedForCache:
                    // not needed for the v1 state machine, but cheap to keep
                    break;
                case UdAspect:
                    if (prop.Format == Mpv.FormatDouble)
                    {
                        double aspect = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(prop.Data));
                        if (aspect > 0.1) StreamAspectChanged?.Invoke(aspect);
                    }
                    break;
                case UdTimePos:
                    if (prop.Format == Mpv.FormatDouble)
                    {
                        double pos = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(prop.Data));
                        if (Math.Abs(pos - _timePos) > 0.0001)
                        {
                            _timePos = pos;
                            _lastPosChangeUtc = DateTime.UtcNow;
                        }
                    }
                    break;
            }
        }
    }

    private void HandleEndFile(Mpv.MpvEvent ev)
    {
        if (ev.Data == IntPtr.Zero) return;
        int reason = Marshal.ReadInt32(ev.Data);
        Log.Write($"end-file reason={reason}");
        lock (_stateLock)
        {
            _lastPosChangeUtc = DateTime.MinValue;
        }
        if (reason == Mpv.EndFileReasonError)
        {
            // No data from OBS yet (or the sender died): keep WAITING and let
            // the watchdog retry — a missing transmitter is not an app error.
            // A hard config error (bad URL) is already reported by Load().
            SetState(PlaybackState.Waiting, "waiting for OBS stream");
        }
    }

    // ---- watchdog ----

    private void WatchdogLoop()
    {
        while (_running)
        {
            Thread.Sleep(WatchdogInterval);

            DateTime now = DateTime.UtcNow;
            bool advancing, noData, loadDue;
            lock (_stateLock)
            {
                advancing = _lastPosChangeUtc != DateTime.MinValue
                            && (now - _lastPosChangeUtc) < PositionAlive;
                noData = _lastPosChangeUtc == DateTime.MinValue
                         || (now - _lastPosChangeUtc) > NoDataThreshold;
                loadDue = (now - _lastLoadAttemptUtc) >= RetryInterval;
            }
            if (advancing)
            {
                SetState(PlaybackState.Live, "");
            }
            else
            {
                SetState(PlaybackState.Waiting, "waiting for OBS stream");
                if (noData && loadDue)
                    Load();
            }
        }
    }

    private void SetState(PlaybackState newState, string detail)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = newState != _state;
            _state = newState;
            if (newState == PlaybackState.Error || detail.Length > 0)
                _errorText = detail;
        }
        if (changed)
            StateChanged?.Invoke(newState, detail);
    }

    public void Dispose()
    {
        if (_ctx == IntPtr.Zero) return;
        _running = false;
        _eventThread?.Join(1000);
        _watchdogThread?.Join(1000);
        lock (_cmdLock)
        {
            Mpv.mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
        }
    }
}
