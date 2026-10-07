using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint id);

    internal sealed class OwnedProcess(Process process, SafeProcessHandle lifetime) : IDisposable
    {
        public Process Process { get; } = process;
        public void Dispose() { Process.Dispose(); lifetime.Dispose(); }
    }

    public static OwnedProcess[] Capture(int browserId)
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
            var owned = new List<OwnedProcess>();
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
                        // Process.SafeHandle requests ALL_ACCESS, denied for sandboxed
                        // Runtime children. Observation only needs query + synchronize.
                        var lifetime = OpenProcess(0x00101000, false, (uint)id);
                        if (lifetime.IsInvalid)
                        {
                            var error = Marshal.GetLastWin32Error();lifetime.Dispose();
                            if (error == 87) { process.Dispose(); continue; } // Already exited.
                            throw new System.ComponentModel.Win32Exception(error);
                        }
                        try {
                            if (process.HasExited || process.StartTime < born) {process.Dispose();lifetime.Dispose();}
                            else owned.Add(new OwnedProcess(process,lifetime));
                        } catch {lifetime.Dispose();throw;}
                    }
                    catch { process.Dispose(); throw; }
                }
                return owned.ToArray();
            }
            catch { foreach (var process in owned) process.Dispose(); throw; }
        }
        finally { CloseHandle(handle); }
    }

    public static (int Count, long Bytes) Measure(IEnumerable<OwnedProcess> processes)
    {
        var count = 0; long bytes = 0;
        foreach (var owned in processes)
        {
            var process=owned.Process;
            try { if (!process.HasExited) { bytes += process.WorkingSet64; count++; } }
            catch (InvalidOperationException) { }
        }
        return (count, bytes);
    }
}
