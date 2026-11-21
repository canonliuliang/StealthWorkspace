using System.Runtime.InteropServices;

namespace StealthWorkspace.Services;

// WebView2 receives Enter before XAML's PreviewKeyDown. While a released link
// is being processed, observe only whether this app's UI thread starts another
// keyboard gesture. Neither the key code nor a typed character is inspected.
internal sealed class PopupGestureKeyboard : IDisposable
{
    private const int GetMessageHook = 3;
    private const uint RemoveMessage = 1, KeyDown = 0x0100, SystemKeyDown = 0x0104;
    private readonly nint _window;
    private readonly Action _cancel;
    private readonly MessageCallback _callback;
    private nint _handle;

    internal PopupGestureKeyboard(nint window, Action cancel)
    {
        _window = window;
        _cancel = cancel;
        _callback = OnMessage;
        _handle = SetWindowsHookExW(GetMessageHook, _callback, 0, GetCurrentThreadId());
    }

    private nint OnMessage(int code, nuint action, nint message)
    {
        // MSG begins with hwnd and message. Leave its wParam and lParam unread.
        if (code >= 0 && action == RemoveMessage && (uint)Marshal.ReadInt32(message, nint.Size) is KeyDown or SystemKeyDown &&
            GetForegroundWindow() == _window) _cancel();
        return CallNextHookEx(0, code, action, message);
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        UnhookWindowsHookEx(_handle);
        _handle = 0;
        GC.KeepAlive(_callback);
    }

    private delegate nint MessageCallback(int code, nuint action, nint message);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int kind, MessageCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint handle, int code, nuint action, nint message);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint handle);
}
