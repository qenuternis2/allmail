using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>Owned fixture process descendants and comparable host/Runtime memory samples.</summary>
internal static class NativeProcessFamily
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        public uint Size, Usage, Id;
        public nuint Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool First(nint snapshot, ref Entry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] private static extern bool Next(nint snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    public static Process[] Capture(int browserId)
    {
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == -1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entries = new List<Entry>(); var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Name = "" };
            if (!First(handle, ref entry)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            do { entries.Add(entry); } while (Next(handle, ref entry));
            var ids = new HashSet<int> { browserId }; bool changed;
            do { changed = false; foreach (var process in entries) if (ids.Contains((int)process.Parent)) changed |= ids.Add((int)process.Id); } while (changed);
            var owned = new List<Process>();
            try
            {
                using var browser = Process.GetProcessById(browserId);
                var born = browser.StartTime;
                foreach (var id in ids)
                {
                    Process process;
                    try { process = Process.GetProcessById(id); }
                    catch (ArgumentException) { continue; } // A child can exit during the snapshot.
                    try
                    {
                        // A Windows PID/parent PID may be reused. Retain the exact process
                        // handle and exclude stale descendants of an earlier owner of that PID.
                        _ = process.SafeHandle;
                        if (process.HasExited || process.StartTime < born) process.Dispose();
                        else owned.Add(process);
                    }
                    catch { process.Dispose(); throw; }
                }
                return owned.ToArray();
            }
            catch { foreach (var process in owned) process.Dispose(); throw; }
        }
        finally { CloseHandle(handle); }
    }

    public static (int Count, long Bytes) Measure(IEnumerable<Process> processes)
    {
        var count = 0; long bytes = 0;
        foreach (var process in processes)
        {
            try { if (!process.HasExited) { bytes += process.WorkingSet64; count++; } }
            catch (InvalidOperationException) { }
        }
        return (count, bytes);
    }
}
