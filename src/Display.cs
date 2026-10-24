using System.Runtime.InteropServices;

namespace InstaClip;

/// <summary>A monitor the way FFmpeg's ddagrab sees it: output number <see cref="Index"/> of the default GPU.</summary>
record Display(int Index, string DeviceName, Rectangle Bounds, IntPtr Monitor, bool Primary)
{
    public string Label => $"{Index + 1}: {Bounds.Width}×{Bounds.Height}" + (Primary ? " (main)" : "");

    /// <summary>
    /// ddagrab opens the default adapter and captures its output number output_idx,
    /// so list exactly those outputs, numbered the same way.
    /// </summary>
    public static List<Display> All()
    {
        var list = new List<Display>();
        IDXGIFactory1 factory = null;
        IDXGIAdapter1 adapter = null;
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var ptr) < 0) return list;
            factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(ptr);
            Marshal.Release(ptr);
            if (factory.EnumAdapters1(0, out adapter) < 0) return list;
            for (uint i = 0; adapter.EnumOutputs(i, out var output) >= 0; i++)
            {
                try
                {
                    if (output.GetDesc(out var d) >= 0 && d.AttachedToDesktop != 0)
                    {
                        var r = Rectangle.FromLTRB(d.Left, d.Top, d.Right, d.Bottom);
                        list.Add(new Display((int)i, d.DeviceName, r, d.Monitor, r.Left == 0 && r.Top == 0));
                    }
                }
                finally { Marshal.ReleaseComObject(output); }
            }
        }
        catch { }
        finally
        {
            if (adapter != null) Marshal.ReleaseComObject(adapter);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
        return list;
    }

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DxgiOutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    // Only the vtable slots up to the methods used are declared; the placeholders are never called.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();   // IDXGIObject
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();   // IDXGIFactory
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();   // IDXGIObject
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIOutput
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();   // IDXGIObject
        [PreserveSig] int GetDesc(out DxgiOutputDesc desc);
    }
}
