using System.Runtime.InteropServices;

namespace StealthWorkspace.Services;

/// <summary>Routes explicit browser commands through Chromium's own shortcuts.
/// WinUI's WebView2 does not expose the controller's ZoomFactor or the user's find text.</summary>
internal static class BrowserZoomInput
{
    internal static bool IsOwnerForeground(nint ownerWindow) => ownerWindow != 0 && GetForegroundWindow() == ownerWindow;
    internal static bool Send(nint ownerWindow, ushort key) =>
        key is (0x30 or 0xBB or 0xBD) && SendControlChord(ownerWindow, key);

    internal static bool SendDownloadsForAudit(nint ownerWindow) =>
        SmokeTest.BrowserAudit && SendControlChord(ownerWindow, 0x4A);

    private static bool SendControlChord(nint ownerWindow, ushort key)
    {
        if (SmokeTest.Enabled)
        {
            nint foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out uint process);
            SmokeTest.Report($"zoom input: owner={ownerWindow}; foreground={foreground}; foreground-root={GetAncestor(foreground, 2)}; foreground-owner={GetAncestor(foreground, 3)}; foreground-pid={process}; current-pid={Environment.ProcessId}; ABI-size={Marshal.SizeOf<Input>()}");
        }
        if (!IsOwnerForeground(ownerWindow)) return false;
        if (Down(0x12) || Down(0x5B) || Down(0x5C)) return false;
        bool controlHeld = Down(0x11);
        var input = new List<Input>(4);
        if (!controlHeld) input.Add(Key(0x11, false));
        input.Add(Key(key, false)); input.Add(Key(key, true));
        if (!controlHeld) input.Add(Key(0x11, true));
        // The OS inserts this short sequence atomically, preserving modifiers
        // that the user already holds. It is never sent to a background window.
        uint sent = SendInput((uint)input.Count, input.ToArray(), Marshal.SizeOf<Input>());
        if (SmokeTest.Enabled) SmokeTest.Report($"zoom input: sent={sent}/{input.Count}; error={Marshal.GetLastWin32Error()}");
        if (sent > 0 && sent < input.Count && !controlHeld)
            SendInput(1, new[] { Key(0x11, true) }, Marshal.SizeOf<Input>());
        return sent == input.Count;
    }

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    internal static bool SendFind(nint ownerWindow, bool advance, bool backwards)
    {
        if (!IsOwnerForeground(ownerWindow) || Down(0x12) || Down(0x5B) || Down(0x5C)) return false;
        bool controlHeld = Down(0x11), shiftHeld = Down(0x10);
        bool controlNeeded = !advance, shiftNeeded = advance && backwards;
        var input = new List<Input>(8);
        if (controlHeld != controlNeeded) input.Add(Key(0x11, !controlNeeded));
        if (shiftHeld != shiftNeeded) input.Add(Key(0x10, !shiftNeeded));
        ushort key = advance ? (ushort)0x72 : (ushort)0x46;
        input.Add(Key(key, false)); input.Add(Key(key, true));
        if (shiftHeld != shiftNeeded) input.Add(Key(0x10, !shiftHeld));
        if (controlHeld != controlNeeded) input.Add(Key(0x11, !controlHeld));
        uint sent = SendInput((uint)input.Count, input.ToArray(), Marshal.SizeOf<Input>());
        if (SmokeTest.Enabled) SmokeTest.Report($"find input: sent={sent}/{input.Count}; ctrl={controlHeld}; shift={shiftHeld}");
        if (sent > 0 && sent < input.Count)
        {
            var restore = new List<Input>(2);
            if (controlHeld != controlNeeded) restore.Add(Key(0x11, !controlHeld));
            if (shiftHeld != shiftNeeded) restore.Add(Key(0x10, !shiftHeld));
            if (restore.Count > 0) SendInput((uint)restore.Count, restore.ToArray(), Marshal.SizeOf<Input>());
        }
        return sent == input.Count;
    }

    private static Input Key(ushort key, bool up) => new() { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0 } } };
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse; // Determines the ABI union size on x86 and ARM64/x64.
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
}
