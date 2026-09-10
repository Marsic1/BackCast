using System.Runtime.InteropServices;
using System.Text;

namespace Backcast.Native;

/// <summary>
/// Raw libmpv (mpv-2.dll) P/Invoke layer. All strings cross the boundary as
/// UTF-8; the default CLR string marshaling is wrong for char* here, so every
/// string-taking entry point has a safe wrapper below.
/// </summary>
internal static class Mpv
{
    private const string Lib = "libmpv-2.dll";

    // ---- error codes ----
    internal const int MpvErrorSuccess = 0;
    internal const int MpvErrorInvalidParameter = -1;
    internal const int MpvErrorLoadingFailed = -5;

    // ---- event ids (client API, include/mpv/client.h — verified against
    // the header; the deprecated ids 9/10/13–15/19/23 are intentionally absent) ----
    internal const int EventNone = 0;
    internal const int EventShutdown = 1;
    internal const int EventLogMessage = 2;
    internal const int EventGetPropertyReply = 3;
    internal const int EventSetPropertyReply = 4;
    internal const int EventCommandReply = 5;
    internal const int EventStartFile = 6;
    internal const int EventEndFile = 7;
    internal const int EventFileLoaded = 8;
    internal const int EventIdle = 11;
    internal const int EventClientMessage = 16;
    internal const int EventVideoReconfig = 17;
    internal const int EventAudioReconfig = 18;
    internal const int EventSeek = 20;
    internal const int EventPlaybackRestart = 21;
    internal const int EventPropertyChange = 22;
    internal const int EventQueueOverflow = 24;
    internal const int EventHook = 25;

    // ---- end-file reasons ----
    internal const int EndFileReasonEof = 0;
    internal const int EndFileReasonStop = 2;
    internal const int EndFileReasonQuit = 3;
    internal const int EndFileReasonError = 4;
    internal const int EndFileReasonRedirect = 5;

    // ---- formats ----
    internal const int FormatString = 1;
    internal const int FormatFlag = 3;
    internal const int FormatDouble = 5;
    internal const int FormatNode = 6;
    internal const int FormatNodeArray = 7;
    internal const int FormatNodeMap = 8;

    // ---- structs (x64 layout) ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEvent
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEventProperty
    {
        public IntPtr Name;      // const char*
        public int Format;
        public IntPtr Data;      // points at int / double / node depending on format
    }

    // struct mpv_node { union { char* string; int flag; int64; double; mpv_node_list* list; } u; mpv_format format; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvNode
    {
        public IntPtr U;
        public int Format;
    }

    // struct mpv_node_list { int num; mpv_node* values; char** keys; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvNodeList
    {
        public int Num;
        public IntPtr Values;  // mpv_node*
        public IntPtr Keys;    // char** (NULL for plain arrays)
    }

    // ---- raw entry points (cdecl) ----

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_option_string(IntPtr ctx, IntPtr name, IntPtr value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_property_string(IntPtr ctx, IntPtr name, IntPtr value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_get_property_string(IntPtr ctx, IntPtr name);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property(IntPtr ctx, IntPtr name, int format, ref MpvNode data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_observe_property(IntPtr ctx, ulong replyUserData, IntPtr name, int format);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free(IntPtr data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free_node_contents(ref MpvNode node);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong mpv_client_api_version();

    // ---- safe helpers ----

    internal static IntPtr AllocUtf8(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s + "\0");
        IntPtr p = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return p;
    }

    internal static void FreeUtf8(IntPtr p)
    {
        if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
    }

    internal static string? PtrToUtf8(IntPtr p) =>
        p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    /// <summary>Runs an mpv command. The argument array is NULL-terminated as required.</summary>
    internal static int Command(IntPtr ctx, params string[] args)
    {
        IntPtr[] ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
                ptrs[i] = AllocUtf8(args[i]);
            ptrs[args.Length] = IntPtr.Zero; // required terminator
            return mpv_command(ctx, ptrs);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
                FreeUtf8(ptrs[i]);
        }
    }

    internal static int SetOptionString(IntPtr ctx, string name, string value)
    {
        IntPtr n = AllocUtf8(name), v = AllocUtf8(value);
        try { return mpv_set_option_string(ctx, n, v); }
        finally { FreeUtf8(n); FreeUtf8(v); }
    }

    internal static int SetPropertyString(IntPtr ctx, string name, string value)
    {
        IntPtr n = AllocUtf8(name), v = AllocUtf8(value);
        try { return mpv_set_property_string(ctx, n, v); }
        finally { FreeUtf8(n); FreeUtf8(v); }
    }

    internal static string? GetPropertyString(IntPtr ctx, string name)
    {
        IntPtr n = AllocUtf8(name);
        try
        {
            IntPtr r = mpv_get_property_string(ctx, n);
            if (r == IntPtr.Zero) return null;
            try { return PtrToUtf8(r); }
            finally { mpv_free(r); }
        }
        finally { FreeUtf8(n); }
    }

    internal static int ObserveProperty(IntPtr ctx, ulong userData, string name, int format)
    {
        IntPtr n = AllocUtf8(name);
        try { return mpv_observe_property(ctx, userData, n, format); }
        finally { FreeUtf8(n); }
    }
}
