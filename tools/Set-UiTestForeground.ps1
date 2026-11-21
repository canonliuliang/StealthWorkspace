param([Parameter(Mandatory)][Diagnostics.Process]$TestProcess)
$TestProcess.Refresh()
if ($TestProcess.HasExited -or $TestProcess.ProcessName -ne 'StealthWorkspace' -or
    $TestProcess.MainWindowTitle -notlike '*测试窗口*' -or $TestProcess.MainWindowHandle -eq 0) { return $false }
if (!('StealthUiFixtureFocus' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class StealthUiFixtureFocus {
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint caller, uint target, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    public static bool Activate(IntPtr target, uint fixturePid) {
        GetWindowThreadProcessId(target, out uint owner);
        if (owner != fixturePid || target == IntPtr.Zero) return false;
        var foreground = GetForegroundWindow();
        if (foreground == target) return true;
        uint previous = GetWindowThreadProcessId(foreground, out _);
        uint caller = GetCurrentThreadId();
        bool attached = previous != 0 && previous != caller && AttachThreadInput(caller, previous, true);
        try {
            BringWindowToTop(target);
            SetForegroundWindow(target);
            return GetForegroundWindow() == target;
        } finally {
            if (attached) AttachThreadInput(caller, previous, false);
        }
    }
}
'@
}
return [StealthUiFixtureFocus]::Activate($TestProcess.MainWindowHandle, $TestProcess.Id)
