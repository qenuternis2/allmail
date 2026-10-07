using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>Test-only bounded suspension of one already captured fixture browser process.</summary>
internal sealed class NativeProcessSuspension : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint id);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(SafeProcessHandle process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(SafeProcessHandle process);
    private readonly object _gate = new();
    private readonly SafeProcessHandle _process;
    private readonly Timer _watchdog;
    private bool _suspended;

    public NativeProcessSuspension(NativeProcessFamily.OwnedProcess owned)
    {
        if (owned.Process.HasExited || owned.Process.ProcessName != "msedgewebview2")
            throw new InvalidOperationException("Only a captured live fixture Runtime may be suspended.");
        _process = OpenProcess(0x00101800, false, (uint)owned.Process.Id); // query, synchronize, suspend/resume
        if (_process.IsInvalid) { var error = Marshal.GetLastWin32Error(); _process.Dispose(); throw new Win32Exception(error); }
        var status = NtSuspendProcess(_process);
        if (status < 0) { _process.Dispose(); throw new InvalidOperationException($"Fixture suspend failed: {status:X8}"); }
        _suspended = true;
        // This independent thread also releases the fixture if its STA is blocked or the test fails.
        _watchdog = new Timer(_ => Resume(), null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
    }
    private void Resume()
    {
        lock (_gate)
        {
            if (!_suspended) return;
            var status = NtResumeProcess(_process);
            if (status < 0) Console.Error.WriteLine($"Fixture resume failed: {status:X8}");
            else _suspended = false;
        }
    }
    public void Dispose()
    {
        _watchdog.Dispose(); Resume();
        lock (_gate) _process.Dispose();
    }
}
