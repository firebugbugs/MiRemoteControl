using System.Runtime.InteropServices;

namespace MiRemoteControl.Desktop.Infrastructure;

internal sealed class GlobalEscapeMonitor : IDisposable
{
    private readonly Func<bool> _handleEscape;
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private HookProc? _hookProc;
    private uint _threadId;
    private int _suppressKeyUp;

    public GlobalEscapeMonitor(Func<bool> handleEscape)
    {
        _handleEscape = handleEscape;
        if (!OperatingSystem.IsWindows()) return;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "MiRemoteControl.EscapeHook" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(2));
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _hookProc = HookCallback;
        var hook = SetWindowsHookEx(13, _hookProc, GetModuleHandle(null), 0);
        _started.Set();
        if (hook == 0) return;
        try
        {
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally { UnhookWindowsHookEx(hook); }
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && Marshal.ReadInt32(lParam) == 0x1B)
        {
            var message = (uint)wParam;
            var down = message is 0x0100 or 0x0104;
            var up = message is 0x0101 or 0x0105;
            if (down && _handleEscape())
            {
                Volatile.Write(ref _suppressKeyUp, 1);
                return 1;
            }
            if (up && Interlocked.Exchange(ref _suppressKeyUp, 0) != 0) return 1;
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012, 0, 0); // WM_QUIT
        _thread?.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int x, y;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MSG message);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
