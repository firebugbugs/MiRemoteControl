using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MiRemoteControl.Desktop.Infrastructure;

internal sealed class GlobalTvKeyMonitor : IDisposable
{
    private const int HotKeyId = 0x4D52; // MR
    private const uint WmHotKey = 0x0312;
    private const uint WmTimer = 0x0113;
    private const uint WmQuit = 0x0012;
    private const uint ModNoRepeat = 0x4000;
    private const int VkOem3 = 0xC0;
    private const int TvScanCode = 0x29;
    private static readonly string DiagnosticPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiRemoteControl", "tv-key.log");

    private readonly Action _handleTv;
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private HookProc? _hookProc;
    private uint _threadId;
    private long _lastTriggeredAt;
    private bool _pollHeld;

    public GlobalTvKeyMonitor(Action handleTv)
    {
        _handleTv = handleTv;
        if (!OperatingSystem.IsWindows()) return;
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "MiRemoteControl.TvGlobalKey"
        };
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("TV 全局按键线程未能启动。");
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _hookProc = HookCallback;
        var hotKeyRegistered = RegisterHotKey(0, HotKeyId, ModNoRepeat, VkOem3);
        var hook = SetWindowsHookEx(13, _hookProc, GetModuleHandle(null), 0);
        var timer = SetTimer(0, 0, 8, 0);
        _started.Set();

        try
        {
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.message == WmHotKey && message.wParam == HotKeyId)
                {
                    TriggerTv("hotkey");
                }
                else if (message.message == WmTimer)
                {
                    var down = (GetAsyncKeyState(VkOem3) & 0x8000) != 0;
                    if (down && !_pollHeld) TriggerTv("async-state");
                    _pollHeld = down;
                }
            }
        }
        finally
        {
            if (timer != 0) KillTimer(0, timer);
            if (hook != 0) UnhookWindowsHookEx(hook);
            if (hotKeyRegistered) UnregisterHotKey(0, HotKeyId);
        }
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            var scan = Marshal.ReadInt32(lParam, sizeof(uint));
            if (vk == VkOem3 && scan == TvScanCode)
            {
                var message = (uint)wParam;
                if (message is 0x0100 or 0x0104) TriggerTv("low-level-hook");
                if (message is 0x0100 or 0x0101 or 0x0104 or 0x0105) return 1;
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private void TriggerTv(string source)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref _lastTriggeredAt);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(220)) return;
        Interlocked.Exchange(ref _lastTriggeredAt, now);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticPath)!);
            File.AppendAllText(DiagnosticPath, $"{DateTimeOffset.Now:O}\t{source}{Environment.NewLine}");
        }
        catch { /* diagnostics must never block the key action */ }
        _handleTv();
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, 0, 0);
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

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetTimer(nint hwnd, nint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hwnd, nint id);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
