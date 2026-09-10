using System.Runtime.InteropServices;

namespace Backcast;

/// <summary>
/// Render endpoint enumeration via raw COM interop — no third-party
/// packages. Mirrors what the plugin's audio-out.c picks from.
/// </summary>
internal static class AudioEndpoints
{
    internal sealed record Endpoint(string Id, string Name, bool IsDefaultConsole, bool IsDefaultComm);

    public static List<Endpoint> List()
    {
        var result = new List<Endpoint>();
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDeviceCollection? col = null;
            Marshal.ThrowExceptionForHR(en.EnumAudioEndpoints(0 /*eRender*/, 1 /*DEVICE_STATE_ACTIVE*/, out col));
            uint count;
            col.GetCount(out count);

            string? defConsole = DefaultId(en, 0 /*eConsole*/);
            string? defComm = DefaultId(en, 2 /*eCommunications*/);

            for (uint i = 0; i < count; i++)
            {
                IMMDevice? dev = null;
                col.Item(i, out dev);
                if (dev == null) continue;
                string? id = null, name = null;
                try
                {
                    IntPtr idPtr = IntPtr.Zero;
                    dev.GetId(out idPtr);
                    id = Marshal.PtrToStringUni(idPtr);
                    Marshal.FreeCoTaskMem(idPtr);

                    IPropertyStore? store = null;
                    dev.OpenPropertyStore(0 /*STGM_READ*/, out store);
                    if (store != null)
                    {
                        PropVariant pv = default;
                        store.GetValue(ref PKEY_Device_FriendlyName, out pv);
                        if (pv.PointerValue != IntPtr.Zero)
                            name = Marshal.PtrToStringUni(pv.PointerValue);
                    }
                }
                catch { /* skip broken endpoint */ }
                if (id != null)
                    result.Add(new Endpoint(id, name ?? id, id == defConsole, id == defComm));
            }
        }
        catch (Exception ex)
        {
            Log.Write($"endpoint enumeration failed: {ex.Message}");
        }
        return result;
    }

    private static string? DefaultId(IMMDeviceEnumerator en, int role)
    {
        try
        {
            IMMDevice? dev = null;
            en.GetDefaultAudioEndpoint(0 /*eRender*/, role, out dev);
            if (dev == null) return null;
            IntPtr idPtr = IntPtr.Zero;
            dev.GetId(out idPtr);
            string? id = Marshal.PtrToStringUni(idPtr);
            Marshal.FreeCoTaskMem(idPtr);
            return id;
        }
        catch { return null; }
    }

    /// <summary>Friendly name for an endpoint id, or a shortened id.</summary>
    public static string NameOf(List<Endpoint> endpoints, string? id)
    {
        if (id == null) return "";
        var ep = endpoints.FirstOrDefault(e => e.Id == id);
        if (ep != null) return ep.Name;
        // WASAPI ids look like {0.0.0.00000000}.{guid} — show the guid tail
        int dot = id.LastIndexOf('.');
        return dot >= 0 ? id[(dot + 1)..] : id;
    }

    // ---- COM plumbing ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, int propertyId)
    {
        public Guid FormatId = formatId;
        public int PropertyId = propertyId;
    }

    // PKEY_Device_FriendlyName (mutable so it can be passed by ref to COM)
    private static PropertyKey PKEY_Device_FriendlyName =
        new(new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), 14);

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public short VariantType;
        [FieldOffset(8)] public IntPtr PointerValue;
    }
}
