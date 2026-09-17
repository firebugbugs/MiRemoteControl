using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MiRemoteControl.Plugin.ChatGpt;

internal sealed record WindowTarget(nint Handle, int ProcessId, string Title);
internal static class NativeWindow
{
    private static readonly nint HWND_TOPMOST = -1;
    private static readonly nint HWND_NOTOPMOST = -2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>
    /// Enumerates visible top-level windows whose owning process executable is
    /// accepted by <paramref name="isTargetProcess"/>. The ChatGPT desktop app
    /// ships both as an MSIX package (under a version-stamped directory in
    /// WindowsApps) and as a plain installer, so the caller supplies the
    /// identity rule instead of a single fixed executable path.
    /// </summary>
    public static List<WindowTarget> Find(Func<string, bool> isTargetProcess)
    {
        var result = new List<WindowTarget>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var process = Process.GetProcessById(pid);
                var executable = process.MainModule?.FileName;
                if (executable is null || !isTargetProcess(executable)) return true;
                var title = new StringBuilder(512);
                GetWindowText(hwnd, title, title.Capacity);
                if (title.Length > 0) result.Add(new(hwnd, pid, title.ToString()));
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException) { }
            return true;
        }, 0);
        return result;
    }
    public static WindowTarget Select(List<WindowTarget> windows, IReadOnlyDictionary<string,string> args)
    {
        if (args.TryGetValue("window", out var handle))
        {
            var id = handle.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt64(handle[2..], 16) : long.Parse(handle);
            return windows.SingleOrDefault(w => w.Handle == (nint)id)
                ?? throw new ControlException("WindowNotFound", "指定窗口不存在或不属于目标可执行程序。");
        }
        if (windows.Count == 1) return windows[0];
        var foreground = windows.FirstOrDefault(w => w.Handle == GetForegroundWindow());
        if (foreground is not null) return foreground;
        throw new ControlException(windows.Count == 0 ? "WindowNotFound" : "AmbiguousWindow",
            windows.Count == 0 ? "ChatGPT 没有可见窗口，请执行 open。" : "存在多个目标窗口，请使用 --window 指定。");
    }
    // Window area for deterministic candidate ordering when several ChatGPT
    // windows exist and none holds the foreground.
    public static long AreaOf(WindowTarget window)
    {
        try { return GetWindowRect(window.Handle, out var rect) ? Math.Max(0L, (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top)) : 0L; }
        catch { return 0L; }
    }
    public static void Focus(WindowTarget target, bool promoteToTopmost = false)
    {
        Validate(target);
        if (IsForeground(target)) return;
        // Physical-remote events are dispatched by the background Host rather
        // than by the foreground desktop process. Grant the Host permission
        // to activate the target before attaching input queues; otherwise
        // Windows may leave ChatGPT visible only as a taskbar button.
        AllowSetForegroundWindow(-1); // ASFW_ANY
        if (IsIconic(target.Handle)) ShowWindow(target.Handle, 9);
        // Windows may reject SetForegroundWindow when the request originates
        // from the hidden Host process. Temporarily attach the input queues so
        // the already-running target can be restored and activated just like a
        // user clicking its taskbar button.
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground != 0 ? GetWindowThreadProcessId(foreground, out _) : 0;
        var targetThread = GetWindowThreadProcessId(target.Handle, out _);
        var currentThread = GetCurrentThreadId();
        // Foreground restrictions are evaluated against the thread that owns
        // the current foreground window. Attach that thread to the target
        // thread while activating; attaching the hidden Host thread itself
        // is insufficient when the user is working in another process.
        var attachedForeground = foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread &&
                                 AttachThreadInput(foregroundThread, targetThread, true);
        // Also attach the caller thread. This covers the case where the
        // foreground window changes between the initial query and activation.
        var attachedCurrent = currentThread != 0 && targetThread != 0 && currentThread != targetThread &&
                              AttachThreadInput(currentThread, targetThread, true);
        try
        {
            ShowWindow(target.Handle, 9);
            // Editing needs keyboard focus, not visibility above a topmost
            // TV editor. Only an explicit open operation may promote ChatGPT;
            // ordinary activation stays in the normal window band below TV.
            if (promoteToTopmost)
            {
                SetWindowPos(target.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                BringWindowToTop(target.Handle);
            }
            if (!SetForegroundWindow(target.Handle)) EarnForeground(target.Handle);
            // SetForegroundWindow is sufficient after the temporary
            // top-most ordering above. SwitchToThisWindow is a legacy
            // toggle API and can immediately reactivate the previous
            // window when called from a background process.
        }
        finally
        {
            // Foreground changes asynchronously. Demote unconditionally so a
            // successful activation that lands just after this block cannot
            // leave ChatGPT permanently above every other application.
            if (promoteToTopmost)
                SetWindowPos(target.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            if (attachedCurrent) AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) AttachThreadInput(foregroundThread, targetThread, false);
        }
        var clock = Stopwatch.StartNew();
        while (GetForegroundWindow() != target.Handle && clock.ElapsedMilliseconds < 1000) Thread.Sleep(25);
        if (GetForegroundWindow() != target.Handle)
        {
            // Never leave the target pinned top-most when activation failed;
            // a window that is always-on-top but has no focus is worse than
            // one resting in the normal Z-order.
            if (promoteToTopmost)
                SetWindowPos(target.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            throw new ControlException("FocusDenied", "Windows 未允许切到 ChatGPT 前台。请先激活 ChatGPT，再执行命令。");
        }
    }
    // A hidden background process holds no foreground-activation right of its
    // own, so SetForegroundWindow is denied and the target surfaces behind
    // the current foreground window. Holding a synthetic ALT key across the
    // activation attempt makes this process the author of the last input
    // event — the documented condition under which Windows grants
    // SetForegroundWindow. The key is released only after activation so the
    // bare ALT never reaches the previously focused application's menu bar.
    private static void EarnForeground(nint handle)
    {
        if (AnyModifierHeld()) return; // never synthesize modifiers into a user chord
        Send([Input(0x12, 0, 0)]);
        try { SetForegroundWindow(handle); }
        finally { Send([Input(0x12, 0, 2)]); }
    }
    public static void Validate(WindowTarget target)
    {
        GetWindowThreadProcessId(target.Handle, out var pid);
        if (!IsWindow(target.Handle) || pid != target.ProcessId)
            throw new ControlException("TargetChanged", "目标窗口已经改变。");
    }
    public static bool IsForeground(WindowTarget target) => GetForegroundWindow() == target.Handle;
    public static void RequireForeground(WindowTarget target)
    {
        Validate(target);
        if (GetForegroundWindow() != target.Handle) throw new ControlException("TargetChanged", "焦点已离开 ChatGPT，停止输入。");
    }
    public static void Close(WindowTarget target)
    {
        Validate(target);
        // SC_CLOSE is the title-bar close command; never kill the application process.
        if (!PostMessage(target.Handle, 0x112, 0xF060, 0)) throw new Win32Exception();
    }
    public static void Key(WindowTarget target, ushort key, ushort modifier = 0)
    {
        RequireForeground(target);
        CheckModifiers();
        var inputs = new List<INPUT>();
        if (modifier != 0) inputs.Add(Input(modifier, 0, 0));
        inputs.Add(Input(key, 0, 0));
        inputs.Add(Input(key, 0, 2));
        if (modifier != 0) inputs.Add(Input(modifier, 0, 2));
        Send(inputs.ToArray());
    }
    public static void Text(WindowTarget target, string text, Action checkFocus)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Any(c => char.IsControl(c) && c != '\n')) throw new ControlException("InvalidText", "文本含不支持的控制字符；制表符请替换为空格。");
        foreach (var line in text.Split('\n').Select((value, index) => (value, index)))
        {
            RequireForeground(target); checkFocus();
            if (line.index > 0) Key(target, 0x0D, 0x10); // Shift+Enter: newline, never submit.
            foreach (var rune in line.value.EnumerateRunes())
            {
                RequireForeground(target); checkFocus(); CheckModifiers();
                Send(rune.ToString().SelectMany(c => new[] { Input(0, c, 4), Input(0, c, 6) }).ToArray());
                // ChatGPT's controlled contenteditable must process each input
                // event before the next one or Chromium can retain only the
                // tail of a full-value replacement.
                Thread.Sleep(10);
            }
        }
    }
    public static void PasteText(WindowTarget target, string text, Action checkFocus)
    {
        using var clipboardReady = new ManualResetEventSlim();
        using var pasteFinished = new ManualResetEventSlim();
        Exception? clipboardFailure = null;
        var clipboardThread = new Thread(() =>
        {
            System.Runtime.InteropServices.ComTypes.IDataObject? previous = null;
            var initialized = OleInitialize(0) >= 0;
            try
            {
                if (OleGetClipboard(out previous) < 0) previous = null;
                var replacement = new System.Windows.DataObject();
                replacement.SetData(System.Windows.DataFormats.UnicodeText, text);
                var replacementObject = (System.Runtime.InteropServices.ComTypes.IDataObject)replacement;
                Marshal.ThrowExceptionForHR(OleSetClipboard(replacementObject));
                clipboardReady.Set();
                pasteFinished.Wait();
                Marshal.ThrowExceptionForHR(OleSetClipboard(previous));
            }
            catch (Exception exception)
            {
                clipboardFailure = exception;
                clipboardReady.Set();
            }
            finally
            {
                if (initialized) OleUninitialize();
            }
        });
        clipboardThread.SetApartmentState(ApartmentState.STA);
        clipboardThread.IsBackground = true;
        clipboardThread.Start();
        clipboardReady.Wait();
        if (clipboardFailure is not null)
            throw new ControlException("ClipboardUnavailable", $"无法准备完整文本粘贴：{clipboardFailure.Message}");
        try
        {
            RequireForeground(target);
            checkFocus();
            Key(target, 0x56, 0x11); // Ctrl+V
            Thread.Sleep(100);
        }
        finally
        {
            pasteFinished.Set();
            clipboardThread.Join(2000);
        }
    }
    private static void CheckModifiers()
    {
        if (AnyModifierHeld())
            throw new ControlException("ModifierHeld", "检测到用户按住修饰键，已取消键盘输入。");
    }
    private static bool AnyModifierHeld()
    {
        foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        return false;
    }
    private static INPUT Input(ushort key, ushort scan, uint flags) => new() { type = 1, data = new() { keyboard = new() { vk = key, scan = scan, flags = flags } } };
    private static void Send(INPUT[] inputs)
    {
        if (inputs.Length > 0 && SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new ControlException("InputFailed", "SendInput 未完整发送，可能有权限限制；结果未知，请检查界面。");
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public UNION data; }
    [StructLayout(LayoutKind.Explicit)] private struct UNION
    {
        [FieldOffset(0)] public KEYBOARD keyboard;
        [FieldOffset(0)] public MOUSE mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBOARD { public ushort vk, scan; public uint flags, time; public nuint extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSE { public int x, y; public uint mouseData, flags, time; public nuint extra; }
    private delegate bool EnumWindowsProc(nint hwnd, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint data);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint hwnd, uint message, nuint wparam, nint lparam);
    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int OleGetClipboard(out System.Runtime.InteropServices.ComTypes.IDataObject? dataObject);
    [DllImport("ole32.dll")] private static extern int OleSetClipboard(System.Runtime.InteropServices.ComTypes.IDataObject? dataObject);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
internal sealed class ControlException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
