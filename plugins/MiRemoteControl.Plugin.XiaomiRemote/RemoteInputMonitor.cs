using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MiRemoteControl.Plugin.XiaomiRemote;

public sealed record RemoteKey(string DevicePath, ushort VirtualKey, ushort MakeCode, bool Extended, string? HidReport = null, ushort? HidUsage = null)
{
    // Consumer-control remotes do not necessarily expose keyboard scan codes.  Keep
    // their non-zero HID report as the learned button identity instead.
    public string Id => HidUsage is { } usage
        ? $"{RemoteDeviceIdentity.Normalize(DevicePath)}|HID_USAGE|{usage:X4}"
        : HidReport is { Length: > 0 }
        ? $"{RemoteDeviceIdentity.Normalize(DevicePath)}|HID|{HidReport}"
        : $"{RemoteDeviceIdentity.Normalize(DevicePath)}|{VirtualKey:X4}|{MakeCode:X4}|{(Extended ? 1 : 0)}";
}

internal static class RemoteDeviceIdentity
{
    private static readonly string[] XiaomiVendorTokens = ["VID&012717", "VID_012717", "VID&2717", "VID_2717"];
    private static readonly Regex HardwareId = new(
        @"VID(?:&|_)(?<vid>[0-9A-F]+).*?PID(?:&|_)(?<pid>[0-9A-F]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsSupportedFamily(string path) =>
        XiaomiVendorTokens.Any(token => path.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
        path.StartsWith("HID-TAP:", StringComparison.OrdinalIgnoreCase);

    // Raw Input paths contain a machine-specific Bluetooth instance suffix.
    // A learned binding should survive re-pairing, another remote of the same
    // family, and moving the application to another PC.
    public static string Normalize(string path)
    {
        if (path.StartsWith("VIRTUAL:", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("SYSTEM:", StringComparison.OrdinalIgnoreCase)) return path.ToUpperInvariant();
        if (IsSupportedFamily(path)) return "XIAOMI-HID";
        var hardware = HardwareId.Match(path);
        if (hardware.Success)
            return $"HID:{hardware.Groups["vid"].Value.ToUpperInvariant()}:{hardware.Groups["pid"].Value.ToUpperInvariant()}";
        return path.ToUpperInvariant();
    }
}
public sealed record RemoteInputEvent(RemoteKey Key, string Button, bool IsDown, DateTimeOffset OccurredAt);
public sealed record RemoteInputView(string Button, bool IsDown, string KeyId, DateTimeOffset OccurredAt);
public sealed record RemoteStatus(bool Listening, bool Learning, string? LearnedKey, IReadOnlyList<string> Devices, string? LastEvent, RemoteInputView? LastInput, IReadOnlyList<string> PressedButtons, IReadOnlyList<RemoteInputView> RecentInputs);

public static class RemoteButtons
{
    public static IReadOnlyList<string> Supported { get; } =
        ["Power", "Up", "Down", "Left", "Right", "Ok", "Back", "Home", "Menu", "Tv", "VolumeUp", "VolumeDown"];

    public static bool TryNormalize(string? value, out string button)
    {
        var key = (value ?? "").Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).Trim();
        button = Supported.FirstOrDefault(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase)) ?? "";
        return button.Length > 0;
    }
}

public sealed class RemoteInputMonitor : IDisposable
{
    private const int TvHotKeyId = 0x4D52;
    private const uint WmHotKey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const ushort TvVirtualKey = 0xC0;
    private const ushort TvScanCode = 0x29;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private readonly ConcurrentDictionary<nint, string> _paths = new();
    private readonly Dictionary<string, Dictionary<ushort, RemoteKey>> _activeHidKeys = [];
    private readonly object _hidSync = new();
    private nint _window;
    private volatile bool _learning;
    private RemoteKey? _voiceKey;
    private string? _lastEvent;
    private RemoteInputView? _lastInput;
    private readonly HashSet<string> _pressedButtons = [];
    private readonly Queue<RemoteInputView> _recentInputs = [];
    private readonly object _stateSync = new();
    private Exception? _startError;
    private bool _tvShortcutHeld;
    public event Action<RemoteInputEvent>? Input;
    public event Action<RemoteKey>? Learned;

    public RemoteInputMonitor(RemoteKey? voiceKey)
    {
        _voiceKey = voiceKey;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "MiRemoteControl.RawInput" };
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(3))) throw new InvalidOperationException("Raw Input 消息线程未能启动：" + _startError?.Message);
    }
    public RemoteStatus Status()
    {
        lock (_stateSync) return new(_window != 0, _learning, _voiceKey?.Id,
            _paths.Values.OrderBy(x => x).ToArray(), _lastEvent, _lastInput, _pressedButtons.OrderBy(x => x).ToArray(), _recentInputs.ToArray());
    }
    public RemoteKey? VoiceKey => _voiceKey;
    public void BeginLearning() => _learning = true;
    public void CancelLearning() => _learning = false;
    public void SetVoiceKey(RemoteKey? key) => _voiceKey = key;
    public bool IsVoiceKey(RemoteKey key) => _voiceKey?.Id == key.Id;
    public void SimulateClick(string button)
    {
        if (!RemoteButtons.TryNormalize(button, out var normalized)) throw new ArgumentException("不支持的虚拟遥控器按键。", nameof(button));
        var key = new RemoteKey("VIRTUAL:CLI", 0, 0, false, normalized);
        Publish(key, true, normalized, allowLearning: false);
        Publish(key, false, normalized, allowLearning: false);
    }
    private void MessageLoop()
    {
        try { MessageLoopCore(); }
        catch (Exception e) { _startError = e; _lastEvent = $"RawInputStartError: {e.Message}"; _started.Set(); }
    }
    private void MessageLoopCore()
    {
        var cls = "MiRemoteControlRawInput";
        var proc = new WndProc(WindowProc);
        var keyboardHook = new HookProc(KeyboardHookProc);
        var wc = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), lpszClassName = cls };
        if (RegisterClass(ref wc) == 0) throw new InvalidOperationException($"RegisterClass failed: {Marshal.GetLastWin32Error()}");
        // A hidden top-level window works with RIDEV_INPUTSINK on more Bluetooth/HID
        // stacks than HWND_MESSAGE, which some drivers reject with ERROR_INVALID_PARAMETER.
        _window = CreateWindowEx(0, cls, null, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (_window == 0) throw new InvalidOperationException($"CreateWindow failed: {Marshal.GetLastWin32Error()}");
        // INPUTSINK | DEVNOTIFY: discover connected remotes immediately and
        // refresh their identity when Bluetooth reconnects, before any key.
        var keyboard = new[] { new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = 0x2100, hwndTarget = _window } };
        if (!RegisterRawInputDevices(keyboard, 1, Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new InvalidOperationException($"Register keyboard raw input failed: {Marshal.GetLastWin32Error()}");
        // Consumer Control is optional: several Windows HID stacks reject a separate 0x0C
        // registration although they surface media keys through the keyboard collection.
        var consumer = new[] { new RAWINPUTDEVICE { usUsagePage = 0x0C, usUsage = 0x01, dwFlags = 0x2100, hwndTarget = _window } };
        if (!RegisterRawInputDevices(consumer, 1, Marshal.SizeOf<RAWINPUTDEVICE>()))
            _lastEvent = $"ConsumerControlNotRegistered: {Marshal.GetLastWin32Error()}";
        var tvHotKeyRegistered = RegisterHotKey(_window, TvHotKeyId, ModNoRepeat, TvVirtualKey);
        if (!tvHotKeyRegistered)
            _lastEvent = $"TvHotKeyNotRegistered: {Marshal.GetLastWin32Error()}";
        RefreshRemoteDevices();
        var hook = SetWindowsHookEx(13, keyboardHook, GetModuleHandle(null), 0);
        _started.Set();
        while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        if (tvHotKeyRegistered) UnregisterHotKey(_window, TvHotKeyId);
        if (_window != 0) DestroyWindow(_window);
        _window = 0;
        if (hook != 0) UnhookWindowsHookEx(hook);
        GC.KeepAlive(keyboardHook);
        GC.KeepAlive(proc);
    }
    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == 0x00FE) // WM_INPUT_DEVICE_CHANGE
        {
            RefreshRemoteDevices();
            return 0;
        }
        if (msg == WmHotKey && wParam == TvHotKeyId)
        {
            PublishTvShortcutClick();
            return 0;
        }
        if (msg == 0x00FF) // WM_INPUT
        {
            try { HandleInput(lParam); } catch (Exception e) { _lastEvent = $"RawInputError: {e.Message}"; }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }
        if (msg == 0x0010) { PostQuitMessage(0); return 0; }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
    private void HandleInput(nint raw)
    {
        uint length = 0;
        GetRawInputData(raw, 0x10000003, 0, ref length, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (length == 0 || length > 4096) return;
        var bytes = Marshal.AllocHGlobal((int)length);
        try
        {
            if (GetRawInputData(raw, 0x10000003, bytes, ref length, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != length) return;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(bytes);
            switch (header.dwType)
            {
                case 1:
                    HandleKeyboardInput(bytes, header);
                    break;
                case 2:
                    HandleHidInput(bytes, header, (int)length);
                    break;
            }
        }
        finally { Marshal.FreeHGlobal(bytes); }
    }
    private void HandleKeyboardInput(nint bytes, RAWINPUTHEADER header)
    {
        var keyboard = Marshal.PtrToStructure<RAWKEYBOARD>(bytes + Marshal.SizeOf<RAWINPUTHEADER>());
        // Some remote HID usages (notably power) are not mapped
        // by kbdhid to a virtual key. Keep VKey=0 reports so the make code can
        // still be diagnosed and normalized below; 0xFF is the HID error code.
        if (keyboard.VKey == 255 && keyboard.MakeCode is not (0x5E or 0x5F)) return;
        var path = DevicePath(header.hDevice);
        if (!IsTargetRemote(path) && !_learning) return;
        _paths.TryAdd(header.hDevice, path);
        Publish(new RemoteKey(path, keyboard.VKey, keyboard.MakeCode, (keyboard.Flags & 0x02) != 0), (keyboard.Flags & 0x01) == 0);
    }
    private void HandleHidInput(nint bytes, RAWINPUTHEADER header, int length)
    {
        var offset = Marshal.SizeOf<RAWINPUTHEADER>();
        if (length < offset + Marshal.SizeOf<RAWHID>()) return;
        var hid = Marshal.PtrToStructure<RAWHID>(bytes + offset);
        var dataOffset = offset + Marshal.SizeOf<RAWHID>();
        var total = checked((long)hid.dwSizeHid * hid.dwCount);
        if (hid.dwSizeHid == 0 || total <= 0 || total > length - dataOffset) return;

        var path = DevicePath(header.hDevice);
        if (!IsTargetRemote(path) && !_learning) return;
        _paths.TryAdd(header.hDevice, path);
        for (var i = 0; i < hid.dwCount; i++)
        {
            var report = new byte[hid.dwSizeHid];
            Marshal.Copy(bytes + dataOffset + checked((int)(i * hid.dwSizeHid)), report, 0, report.Length);
            if (XiaomiRemoteHidReportParser.TryParse(report, out var usages))
            {
                PublishHidUsageChanges($"raw:{header.hDevice}", path, report, usages);
                continue;
            }

            // Unknown report layouts still remain learnable/diagnosable.
            var key = new RemoteKey(path, 0, 0, false, Convert.ToHexString(report));
            Publish(key, true);
            Publish(key, false);
        }
    }
    public bool SubmitHidTapReport(string devicePath, ReadOnlySpan<byte> report)
    {
        if (!IsTargetRemote(devicePath) || !XiaomiRemoteHidReportParser.TryParse(report, out var usages)) return false;
        PublishHidUsageChanges("tap:" + devicePath, devicePath, report.ToArray(), usages);
        return true;
    }
    private void PublishHidUsageChanges(string source, string path, byte[] report, IReadOnlyList<ushort> usages)
    {
        lock (_hidSync)
        {
            if (!_activeHidKeys.TryGetValue(source, out var active))
            {
                active = [];
                _activeHidKeys[source] = active;
            }

            var next = usages.ToHashSet();
            foreach (var released in active.Where(pair => !next.Contains(pair.Key)).Select(pair => pair.Value).ToArray())
            {
                active.Remove(released.HidUsage!.Value);
                Publish(released, false);
            }

            foreach (var usage in usages)
            {
                if (active.ContainsKey(usage)) continue;
                var key = new RemoteKey(path, 0, 0, false, Convert.ToHexString(report), usage);
                active[usage] = key;
                Publish(key, true);
            }

            if (active.Count == 0) _activeHidKeys.Remove(source);
        }
    }
    private void Publish(RemoteKey key, bool down) => Publish(key, down, NormalizeButton(key), allowLearning: true);
    private void Publish(RemoteKey key, bool down, string button, bool allowLearning)
    {
        lock (_stateSync)
        {
            _lastEvent = $"{(down ? "Down" : "Up")} {button} · {key.Id}";
            _lastInput = new RemoteInputView(button, down, key.Id, DateTimeOffset.Now);
            _recentInputs.Enqueue(_lastInput);
            while (_recentInputs.Count > 40) _recentInputs.Dequeue();
            if (down) _pressedButtons.Add(button); else _pressedButtons.Remove(button);
        }
        if (allowLearning && _learning && down)
        {
            _voiceKey = key;
            _learning = false;
            Learned?.Invoke(key);
        }
        Input?.Invoke(new RemoteInputEvent(key, button, down, DateTimeOffset.Now));
    }
    private static string NormalizeButton(RemoteKey key)
    {
        if (key.HidUsage is { } usage && XiaomiRemoteHidReportParser.TryGetButton(usage, out var hidButton))
            return hidButton;
        // The Xiaomi remote exposes the physical power key through the
        // keyboard hook as VK=0xFF with scan code 0x5E (some firmware uses
        // 0x5F).  The virtual-key value is intentionally not trusted here.
        if (key.MakeCode is 0x5E or 0x5F) return "Power";
        return key.VirtualKey switch
        {
            0x74 => "Voice",
            0x26 => "Up",
            0x28 => "Down",
            0x25 => "Left",
            0x27 => "Right",
            0x0D => "Ok",
            0x08 or 0x1B or 0xA6 => "Back",
            0x24 or 0xAC => "Home",
            0x5D => "Menu",
            0xAD => "Mute",
            0xAE => "VolumeDown",
            0xAF => "VolumeUp",
            0x5F or 0xB6 or 0xB8 => "Power",
            0xB7 => "Tv",
            0xC0 => "Tv",
            _ when key.HidReport is { Length: > 0 } => NormalizeHid(key.HidReport),
            _ => key.HidUsage is { } unknownUsage ? $"HidUsage_{unknownUsage:X4}" : $"Key_{key.VirtualKey:X2}"
        };
    }
    private bool IsTargetRemote(string path) =>
        RemoteDeviceIdentity.IsSupportedFamily(path) ||
        (_voiceKey is not null && RemoteDeviceIdentity.Normalize(_voiceKey.DevicePath) == RemoteDeviceIdentity.Normalize(path));

    private void RefreshRemoteDevices()
    {
        uint count = 0;
        var entrySize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, entrySize) == uint.MaxValue || count > 4096) return;
        var devices = new RAWINPUTDEVICELIST[count];
        var read = GetRawInputDeviceList(devices, ref count, entrySize);
        if (read == uint.MaxValue) return; // retain the last known list on transient failures
        var connected = new HashSet<nint>();
        foreach (var device in devices.Take((int)read))
        {
            if (device.dwType is not (1 or 2)) continue;
            var path = DevicePath(device.hDevice);
            if (!IsTargetRemote(path)) continue;
            connected.Add(device.hDevice);
            _paths[device.hDevice] = path;
        }
        foreach (var handle in _paths.Keys)
            if (!connected.Contains(handle)) _paths.TryRemove(handle, out _);
    }
    private static string NormalizeHid(string report)
    {
        if (report.Contains("E9", StringComparison.OrdinalIgnoreCase)) return "VolumeUp";
        if (report.Contains("EA", StringComparison.OrdinalIgnoreCase)) return "VolumeDown";
        if (report.Contains("E2", StringComparison.OrdinalIgnoreCase)) return "Mute";
        if (report.Contains("2302", StringComparison.OrdinalIgnoreCase) || report.Contains("0223", StringComparison.OrdinalIgnoreCase)) return "Home";
        if (report.Contains("4100", StringComparison.OrdinalIgnoreCase) || report.EndsWith("41", StringComparison.OrdinalIgnoreCase)) return "Ok";
        if (report.Contains("8900", StringComparison.OrdinalIgnoreCase) || report.EndsWith("89", StringComparison.OrdinalIgnoreCase)) return "Tv";
        if (report.Contains("30", StringComparison.OrdinalIgnoreCase)) return "Power";
        return "Hid_" + report;
    }
    private nint KeyboardHookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var vk = (ushort)Marshal.ReadInt32(lParam);
            var scan = (ushort)Marshal.ReadInt32(lParam, sizeof(uint));
            var flags = (uint)Marshal.ReadInt32(lParam, sizeof(uint) * 2);
            var message = (uint)wParam;
            var down = message is 0x0100 or 0x0104;
            var up = message is 0x0101 or 0x0105;
            var isPowerScan = scan is 0x5E or 0x5F;
            var isTvKey = vk == TvVirtualKey && scan == TvScanCode;
            if (isTvKey || (_paths.Values.Any(IsTargetRemote) &&
                (isPowerScan || vk is 0xA6 or 0xAC or 0x5D or 0x5F or 0xAD or 0xAE or 0xAF or 0xB6 or 0xB7 or 0xB8)))
            {
                if (isTvKey)
                {
                    if (down && !_tvShortcutHeld)
                    {
                        _tvShortcutHeld = true;
                        Publish(TvShortcutKey(), true);
                    }
                    else if (up && _tvShortcutHeld)
                    {
                        _tvShortcutHeld = false;
                        Publish(TvShortcutKey(), false);
                    }
                }
                else if (down || up)
                {
                    Publish(new RemoteKey("SYSTEM:MEDIA-KEY", vk, scan, (flags & 0x01) != 0), down);
                }
                // This remote usage is translated by Windows into an OEM text
                // key. Consume it here so target applications never receive a
                // stray grave-accent/middle-dot character.
                if (isTvKey && (down || up)) return 1;
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private void PublishTvShortcutClick()
    {
        // WM_HOTKEY may arrive in addition to the low-level hook callback.
        // If the hook already owns this press, it will publish the matching
        // release; otherwise synthesize one complete click here.
        if (_tvShortcutHeld) return;
        var key = TvShortcutKey();
        Publish(key, true);
        Publish(key, false);
    }

    private static RemoteKey TvShortcutKey() =>
        new("SYSTEM:TV-HOTKEY", TvVirtualKey, TvScanCode, false);
    private static string DevicePath(nint device)
    {
        uint size = 0;
        GetRawInputDeviceInfoSize(device, 0x20000007, 0, ref size);
        var text = new StringBuilder((int)size + 1);
        return GetRawInputDeviceInfo(device, 0x20000007, text, ref size) > 0 ? text.ToString() : $"HANDLE:{device}";
    }
    public void Dispose()
    {
        if (_window != 0) PostMessage(_window, 0x0010, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }
    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);
    private delegate nint HookProc(int code, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WNDCLASS { public uint style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName, lpszClassName; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public nint hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICELIST { public nint hDevice; public uint dwType; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public uint dwType, dwSize; public nint hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWHID { public uint dwSizeHid, dwCount; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WNDCLASS wndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint ex, string cls, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceInfo(nint device, uint command, StringBuilder data, ref uint size);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)] private static extern uint GetRawInputDeviceInfoSize(nint device, uint command, nint data, ref uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int hook, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
