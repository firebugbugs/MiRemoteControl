using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MiRemoteControl.Client;
using System.Runtime.InteropServices;

namespace MiRemoteControl.Desktop;

public partial class App : Application
{
    private readonly UserSettings _userSettings = UserSettingsStore.Load();
    private MainWindow? _mainWindow;
    public bool ExitRequested { get; private set; }
    public bool IsDarkTheme => _userSettings.IsDarkTheme;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RequestedThemeVariant = _userSettings.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
            _mainWindow = new MainWindow();
            if (Program.IsBackgroundLaunch)
            {
                _mainWindow.ShowInTaskbar = false;
                _mainWindow.Opened += (_, _) => _mainWindow.Hide();
            }
            desktop.MainWindow = _mainWindow;
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void SetTheme(bool isDarkTheme)
    {
        _userSettings.IsDarkTheme = isDarkTheme;
        RequestedThemeVariant = isDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        UserSettingsStore.Save(_userSettings);
    }

    private void TrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();
    private void ShowMainWindowMenuClicked(object? sender, EventArgs e) => ShowMainWindow();

    private async void ExitMenuClicked(object? sender, EventArgs e)
    {
        if (ExitRequested) return;
        ExitRequested = true;
        try { await new CoreClient().InvokeAsync("core", "stop", timeoutMs: 1500); }
        catch { /* The desktop must still exit when the backend is unavailable. */ }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.ShowInTaskbar = true;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        if (OperatingSystem.IsWindows() && _mainWindow.TryGetPlatformHandle()?.Handle is nint handle && handle != 0)
            ActivateWindow(handle);
    }

    // The Studio window is usually summoned by the remote while another
    // application owns the foreground, and this process received no input
    // event, so plain SetForegroundWindow is denied and the window would
    // surface behind the foreground window. Raise it through the Z-order
    // first, then earn the activation right with a synthetic ALT tap held
    // across the SetForegroundWindow call (released only after activation so
    // the bare ALT never reaches the previously focused app's menu bar).
    // Runs on the UI thread: every wait is bounded and nothing may throw.
    private static void ActivateWindow(nint handle)
    {
        AllowSetForegroundWindow(-1); // ASFW_ANY
        ShowWindow(handle, 9); // SW_RESTORE
        // Put the window above the current foreground window only while
        // activation is requested. It must always be demoted before return.
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground != 0 ? GetWindowThreadProcessId(foreground, out _) : 0;
        var targetThread = GetWindowThreadProcessId(handle, out _);
        var attached = foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread &&
                       AttachThreadInput(foregroundThread, targetThread, true);
        try
        {
            if (!SetForegroundWindow(handle)) EarnForeground(handle);
        }
        finally
        {
            if (attached) AttachThreadInput(foregroundThread, targetThread, false);
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (GetForegroundWindow() != handle && clock.ElapsedMilliseconds < 300) System.Threading.Thread.Sleep(25);
        // This is a temporary Z-order aid, never a persistent window mode.
        // The old conditional demotion raced with Windows' foreground update:
        // focus could succeed just after the check and leave the window pinned.
        SetWindowPos(handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    }

    private static void EarnForeground(nint handle)
    {
        if (AnyModifierHeld()) return; // never synthesize modifiers into a user chord
        Send(KeyboardInput(0x12, 0));
        try { SetForegroundWindow(handle); }
        finally { Send(KeyboardInput(0x12, 2)); }
    }
    private static bool AnyModifierHeld()
    {
        foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        return false;
    }
    // Best effort: activation must not throw on the UI thread, so an
    // incomplete SendInput is ignored and the caller falls back to the
    // raised-but-unfocused state.
    private static void Send(params INPUT[] inputs)
    {
        if (inputs.Length > 0 && SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>()) != inputs.Length) { }
    }
    private static INPUT KeyboardInput(ushort key, uint flags) => new() { type = 1, data = new() { keyboard = new() { vk = key, scan = 0, flags = flags } } };

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public UNION data; }
    [StructLayout(LayoutKind.Explicit)] private struct UNION
    {
        [FieldOffset(0)] public KEYBOARD keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBOARD { public ushort vk, scan; public uint flags, time; public nuint extra; }
    private static readonly nint HWND_TOPMOST = -1;
    private static readonly nint HWND_NOTOPMOST = -2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
}
