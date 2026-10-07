using System.Runtime.InteropServices;

/// <summary>Only the disposable CI desktop. Restore the captured scale; never alter a user's display.</summary>
internal sealed class DisplayDpiFixture : IDisposable
{
    private static readonly int[] Scales = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct Header { public uint Type, Size; public long Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct GetScale { public Header Header; public int Minimum, Current, Maximum; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct SetScale { public Header Header; public int Relative; }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, IntPtr pathInfo, ref uint modes, IntPtr modeInfo, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetDeviceInfo(ref GetScale scale);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] private static extern int SetDeviceInfo(ref SetScale scale);
    private readonly GetScale _original;
    private bool _changed;
    private DisplayDpiFixture(GetScale original) => _original = original;

    public static DisplayDpiFixture? TryCapture()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        { Console.WriteLine("DPI desktop change NotPerformed: reserved for disposable GitHub runner."); return null; }
        var status = GetDisplayConfigBufferSizes(2, out var paths, out var modes);
        if (status != 0 || paths != 1 || modes > 100)
        { Console.WriteLine($"DPI desktop change NotPerformed: single active display required; status={status}, paths={paths}."); return null; }
        var path = Marshal.AllocHGlobal(72);
        var mode = Marshal.AllocHGlobal(checked((int)modes * 64));
        try
        {
            status = QueryDisplayConfig(2, ref paths, path, ref modes, mode, IntPtr.Zero);
            if (status != 0)
            { Console.WriteLine($"DPI desktop change NotPerformed: QueryDisplayConfig={status}."); return null; }
            // DISPLAYCONFIG_PATH_INFO starts with SOURCE_INFO (LUID, source id).
            // -3/-4 are driver-specific DPI packets, not a supported public contract.
            // Rejection is NotPerformed; it must not count as a display-scale test.
            var scale = new GetScale { Header = new() { Type = unchecked((uint)-3), Size = (uint)Marshal.SizeOf<GetScale>(),
                Adapter = Marshal.ReadInt64(path), Id = (uint)Marshal.ReadInt32(path, 8) } };
            status = GetDeviceInfo(ref scale);
            if (status != 0 || -scale.Minimum < 0 || -scale.Minimum >= Scales.Length)
            { Console.WriteLine($"DPI desktop change NotPerformed: display driver scale query={status}."); return null; }
            Console.WriteLine($"DPI display config: min={scale.Minimum}, current={scale.Current}, max={scale.Maximum}, recommended={Scales[-scale.Minimum]}%.");
            return new(scale);
        }
        finally { Marshal.FreeHGlobal(mode); Marshal.FreeHGlobal(path); }
    }

    public bool TrySet(int percent)
    {
        var index = Array.IndexOf(Scales, percent);
        if (index < 0) return false;
        var relative = index + _original.Minimum;
        if (relative < _original.Minimum || relative > _original.Maximum) return false;
        var set = new SetScale { Header = _original.Header, Relative = relative };
        set.Header.Type = unchecked((uint)-4); set.Header.Size = (uint)Marshal.SizeOf<SetScale>();
        var status = SetDeviceInfo(ref set);
        Console.WriteLine($"DPI display config set {percent}%: status={status}.");
        _changed |= status == 0;
        return status == 0;
    }

    public void Dispose()
    {
        if (!_changed) return;
        var set = new SetScale { Header = _original.Header, Relative = _original.Current };
        set.Header.Type = unchecked((uint)-4); set.Header.Size = (uint)Marshal.SizeOf<SetScale>();
        var status = SetDeviceInfo(ref set);
        if (status != 0) throw new InvalidOperationException("Could not restore runner display scale: " + status);
        Console.WriteLine("DPI display config: original scale restored.");
    }
}

/// <summary>Optional supported-mode resize of the disposable CI display so 200% can fit. Restore on exit.</summary>
internal sealed class DisplayResolutionFixture : IDisposable
{
    // DEVMODEW fixed portion: dmSize at 68, dmDriverExtra at 70,
    // dmPelsWidth/dmPelsHeight at 172/176. Reject driver extensions.
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(IntPtr device, uint mode, [In, Out] byte[] settings);
    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsW")]
    private static extern int ChangeDisplaySettings(byte[] settings, uint flags);
    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    private readonly byte[] _original;
    private DisplayResolutionFixture(byte[] original) => _original = original;
    private static byte[] Buffer() { var data = new byte[220]; BitConverter.GetBytes((ushort)220).CopyTo(data, 68); return data; }

    public static DisplayResolutionFixture? TryResize()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || GetDisplayConfigBufferSizes(2, out var paths, out _) != 0 || paths != 1) return null;
        var original = Buffer();
        if (!EnumDisplaySettings(IntPtr.Zero, uint.MaxValue, original) || BitConverter.ToUInt16(original, 70) != 0) return null;
        var width = BitConverter.ToInt32(original, 172); var height = BitConverter.ToInt32(original, 176);
        Console.WriteLine($"DPI original desktop resolution: {width}x{height}.");
        if (width >= 1920 && height >= 1200) return null;
        var candidates = new List<byte[]>();
        for (uint index = 0; index < 500; index++)
        {
            var mode = Buffer();
            if (!EnumDisplaySettings(IntPtr.Zero, index, mode)) break;
            var w = BitConverter.ToInt32(mode, 172); var h = BitConverter.ToInt32(mode, 176);
            if (BitConverter.ToUInt16(mode, 70) == 0 && w >= 1920 && h >= 1200 && w <= 2560 && h <= 1600) candidates.Add(mode);
        }
        foreach (var mode in candidates.OrderBy(m => (long)BitConverter.ToInt32(m, 172) * BitConverter.ToInt32(m, 176)))
        {
            if (ChangeDisplaySettings(mode, 2) != 0 || ChangeDisplaySettings(mode, 0) != 0) continue;
            Console.WriteLine($"DPI test desktop resolution: {BitConverter.ToInt32(mode, 172)}x{BitConverter.ToInt32(mode, 176)}.");
            return new(original);
        }
        Console.WriteLine("DPI larger desktop NotPerformed: no supported mode >=1920x1200; existing scale range retained.");
        return null;
    }

    public void Dispose()
    {
        var status = ChangeDisplaySettings(_original, 0);
        if (status != 0) throw new InvalidOperationException("Could not restore runner resolution: " + status);
        Console.WriteLine("DPI original desktop resolution restored.");
    }
}
