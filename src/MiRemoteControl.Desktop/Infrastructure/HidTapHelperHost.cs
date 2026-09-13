using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using MiRemoteControl.Contracts;
using SharpCompress.Compressors.Xz;

namespace MiRemoteControl.Desktop.Infrastructure;

internal readonly record struct HidTapHelperArguments(int ParentProcessId, string StopEventName);

internal static class HidTapHelperHost
{
    private const string FridaVersion = "17.15.3";
    private const string ArchiveName = "frida-gadget-17.15.3-windows-x86_64.dll.xz";
    private const string ArchiveSha256 = "B566D70189B6D551AD8F4E0BEA24DE08A3D4C0F559BB35B2BDB67D45182240C2";
    private const string DllSha256 = "6FCA4007B2284C765A6C15C967A741F536B5865BF83867326A54029A3B752748";
    private const string GadgetDllName = "RemoteMicRC003HidTap.dll";
    private const string GadgetConfigName = "RemoteMicRC003HidTap.config";
    private const string GadgetScriptName = "rc003_hid_gadget.js";
    private const int HidTapPort = 30684;
    private const string RemoteDevicePath = @"BTHLEDEVICE#VID&012717_PID&32B8#RC003";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public static bool TryParseArguments(string[] args, out HidTapHelperArguments arguments)
    {
        arguments = default;
        return args.Length == 3 &&
               string.Equals(args[0], "--hid-tap-helper", StringComparison.Ordinal) &&
               int.TryParse(args[1], out var parentProcessId) && parentProcessId > 0 &&
               !string.IsNullOrWhiteSpace(args[2]) &&
               SetArguments(parentProcessId, args[2], out arguments);
    }

    public static bool IsRc003Connected()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return FindRc003Host() is not null; }
        catch { return false; }
    }

    private static bool SetArguments(int parentProcessId, string stopEventName, out HidTapHelperArguments arguments)
    {
        arguments = new HidTapHelperArguments(parentProcessId, stopEventName);
        return true;
    }

    public static void Run(HidTapHelperArguments arguments)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            WriteLog($"辅助进程已进入，父进程 {arguments.ParentProcessId}。");
            using var parent = Process.GetProcessById(arguments.ParentProcessId);
            using var stopEvent = EventWaitHandle.OpenExisting(arguments.StopEventName);
            using var shutdown = new CancellationTokenSource();
            var watcher = Task.Run(async () =>
            {
                while (!shutdown.IsCancellationRequested)
                {
                    if (stopEvent.WaitOne(250) || parent.HasExited)
                    {
                        shutdown.Cancel();
                        return;
                    }
                    await Task.Yield();
                }
            });
            try { RunAsync(shutdown.Token).GetAwaiter().GetResult(); }
            finally
            {
                shutdown.Cancel();
                try { watcher.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            }
        }
        catch (Exception exception)
        {
            WriteLog("辅助进程退出：" + exception.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            WriteLog("辅助进程没有管理员权限。");
            return;
        }
        EnableDebugPrivilege();

        string runtimeDll;
        try
        {
            runtimeDll = await EnsureRuntimeAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            WriteLog("准备 Frida Gadget 失败：" + exception.Message);
            return;
        }

        var waitingForRemote = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var target = FindRc003Host();
            if (target is null)
            {
                if (!waitingForRemote) WriteLog("等待已连接的 Xiaomi RC003。");
                waitingForRemote = true;
                await Task.Delay(RetryDelay, cancellationToken);
                continue;
            }
            waitingForRemote = false;
            try
            {
                await CaptureFromHostAsync(target.Value, runtimeDll, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                WriteLog($"RC003 捕获失败（WUDFHost {target.Value}）：{exception.Message}");
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task CaptureFromHostAsync(int targetProcessId, string runtimeDll, CancellationToken cancellationToken)
    {
        using var listener = new TcpListener(IPAddress.Loopback, HidTapPort);
        listener.Start(1);

        VerifyWudfHost(targetProcessId);
        InjectLibrary(targetProcessId, runtimeDll);
        WriteLog($"已按开源成品方案加载 RC003 HID tap，等待 WUDFHost {targetProcessId} 回连 127.0.0.1:{HidTapPort}。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        client.NoDelay = true;
        WriteLog($"收到 WUDFHost {targetProcessId} 的 TCP 连接。");
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);

        var readyLine = await reader.ReadLineAsync(timeout.Token);
        if (readyLine is null) throw new InvalidDataException("组件连接未提供就绪信息。");
        using (var ready = JsonDocument.Parse(readyLine))
        {
            var root = ready.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "ready" ||
                !root.TryGetProperty("pid", out var pid) || pid.GetInt32() != targetProcessId)
                throw new InvalidDataException("组件就绪信息校验失败。");
        }

        WriteLog("增强按键捕获已就绪。");
        long sequence = 0;
        NamedPipeClientStream? pipe = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (!TryReadReport(line, out var reportHex)) continue;
                WriteLog($"捕获 RC003 HID 报告：{DescribeReport(reportHex)} raw={reportHex}");
                pipe = await EnsurePipeAsync(pipe, cancellationToken);
                try
                {
                    await Wire.WriteAsync(pipe, new HidTapFrame(++sequence, RemoteDevicePath, reportHex), cancellationToken);
                }
                catch (IOException)
                {
                    await pipe.DisposeAsync();
                    pipe = null;
                }
            }
        }
        finally
        {
            if (pipe is not null) await pipe.DisposeAsync();
        }
    }

    private static bool TryReadReport(string line, out string reportHex)
    {
        reportHex = "";
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "gatt_read" ||
                !root.TryGetProperty("raw", out var value)) return false;
            var text = value.GetString();
            if (text is null || text.Length != 18) return false;
            var bytes = Convert.FromHexString(text);
            if (bytes.Length != 9 || bytes[0] != 1 || bytes[1] != 0 || bytes[2] != 0) return false;
            for (var index = 3; index < bytes.Length; index += 2)
            {
                var usage = (ushort)(bytes[index] | bytes[index + 1] << 8);
                if (usage != 0 && usage is not (0xF1 or 0x80 or 0x81)) return false;
            }
            reportHex = text.ToUpperInvariant();
            return true;
        }
        catch (JsonException) { return false; }
        catch (FormatException) { return false; }
    }

    private static string DescribeReport(string reportHex)
    {
        var bytes = Convert.FromHexString(reportHex);
        var names = new List<string>(3);
        for (var index = 3; index < bytes.Length; index += 2)
        {
            var usage = (ushort)(bytes[index] | bytes[index + 1] << 8);
            var name = usage switch
            {
                0xF1 => "Back(0x00F1)",
                0x80 => "VolumeUp(0x0080)",
                0x81 => "VolumeDown(0x0081)",
                _ => null
            };
            if (name is not null) names.Add(name);
        }
        return names.Count == 0 ? "Release" : string.Join('+', names);
    }

    private static async Task<NamedPipeClientStream> EnsurePipeAsync(NamedPipeClientStream? pipe, CancellationToken cancellationToken)
    {
        if (pipe is { IsConnected: true }) return pipe;
        if (pipe is not null) await pipe.DisposeAsync();
        var connected = new NamedPipeClientStream(".", Wire.HidTapPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await connected.ConnectAsync(3000, cancellationToken);
        return connected;
    }

    [SupportedOSPlatform("windows")]
    private static int? FindRc003Host()
    {
        const string registryPath = @"SYSTEM\CurrentControlSet\Enum\BTHLEDevice";
        using var root = Registry.LocalMachine.OpenSubKey(registryPath);
        if (root is null) return null;
        var matches = new HashSet<int>();
        foreach (var serviceName in root.GetSubKeyNames())
        {
            var lower = serviceName.ToLowerInvariant();
            if (!lower.StartsWith("{00001812-0000-1000-8000-00805f9b34fb}", StringComparison.Ordinal) ||
                !lower.Contains("dev_vid&012717_pid&32b8_", StringComparison.Ordinal)) continue;
            using var service = root.OpenSubKey(serviceName);
            if (service is null) continue;
            foreach (var instanceName in service.GetSubKeyNames())
            {
                using var diagnostics = service.OpenSubKey(instanceName + @"\Device Parameters\WUDFDiagnosticInfo");
                var value = diagnostics?.GetValue("HostPid");
                var pid = value switch
                {
                    int signed when signed > 0 => signed,
                    uint unsigned when unsigned <= int.MaxValue => (int)unsigned,
                    long signed when signed is > 0 and <= int.MaxValue => (int)signed,
                    _ => 0
                };
                if (pid > 0) matches.Add(pid);
            }
        }
        return matches.Count == 1 ? matches.Single() : null;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<string> EnsureRuntimeAsync(CancellationToken cancellationToken)
    {
        var tapRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MiRemoteControl", "HidTap");
        var assetRoot = Path.Combine(tapRoot, FridaVersion);
        Directory.CreateDirectory(assetRoot);
        var archivePath = Path.Combine(assetRoot, ArchiveName);

        if (!File.Exists(archivePath) || HashFile(archivePath) != ArchiveSha256)
        {
            var temporaryPath = archivePath + ".download";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                await using var source = await http.GetStreamAsync(
                    $"https://github.com/frida/frida/releases/download/{FridaVersion}/{ArchiveName}", cancellationToken);
                await using (var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    await source.CopyToAsync(destination, cancellationToken);
                if (HashFile(temporaryPath) != ArchiveSha256)
                    throw new InvalidDataException("Frida Gadget 压缩包校验失败。");
                File.Move(temporaryPath, archivePath, true);
            }
            finally
            {
                try { File.Delete(archivePath + ".download"); } catch { }
            }
        }

        // A stable path/name and port are intentional. Once Gadget is loaded in
        // WUDFHost it stays resident and reconnects to later application runs;
        // creating one random DLL per run leaves stale agents in the host.
        var runtimeRoot = Path.Combine(tapRoot, $"{FridaVersion}-x64-{DllSha256[..12]}");
        Directory.CreateDirectory(runtimeRoot);
        SecureSessionDirectory(runtimeRoot);
        var dllPath = Path.Combine(runtimeRoot, GadgetDllName);
        if (!File.Exists(dllPath) || HashFile(dllPath) != DllSha256)
        {
            var temporaryDll = dllPath + ".extracting";
            try
            {
                await using var source = File.OpenRead(archivePath);
                await using var xz = new XZStream(source);
                await using (var destination = new FileStream(temporaryDll, FileMode.Create, FileAccess.Write, FileShare.None))
                    await xz.CopyToAsync(destination, cancellationToken);
                if (HashFile(temporaryDll) != DllSha256)
                    throw new InvalidDataException("Frida Gadget DLL 校验失败。");
                File.Move(temporaryDll, dllPath, true);
            }
            finally
            {
                try { File.Delete(temporaryDll); } catch { }
            }
        }

        var scriptPath = Path.Combine(runtimeRoot, GadgetScriptName);
        using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                   "MiRemoteControl.Desktop.Infrastructure.Rc003HidTap.js") ??
               throw new InvalidOperationException("找不到内置 HID 捕获脚本。"))
        using (var destination = File.Create(scriptPath)) source.CopyTo(destination);

        var configPath = Path.Combine(runtimeRoot, GadgetConfigName);
        File.WriteAllText(configPath, JsonSerializer.Serialize(new
        {
            interaction = new
            {
                type = "script",
                path = GadgetScriptName,
                parameters = new { host = "127.0.0.1", port = HidTapPort },
                on_change = "ignore"
            },
            runtime = "qjs",
            teardown = "minimal"
        }));
        SecureSessionDirectory(runtimeRoot);
        return dllPath;
    }

    [SupportedOSPlatform("windows")]
    private static void SecureSessionDirectory(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
            FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void VerifyWudfHost(int processId)
    {
        using var process = Process.GetProcessById(processId);
        var path = Path.GetFullPath(process.MainModule?.FileName ?? "");
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var expected = Path.Combine(systemDirectory, "WUDFHost.exe");
        var alternate = Path.Combine(systemDirectory, "WUDF", "WUDFHost.exe");
        if (!string.Equals(path, expected, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, alternate, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("RC003 驱动宿主身份验证失败。");
    }

    private static void InjectLibrary(int processId, string libraryPath)
    {
        const uint access = 0x0002 | 0x0008 | 0x0020 | 0x0400 | 0x0010;
        using var process = new SafeNativeHandle(OpenProcess(access, false, processId));
        if (process.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var pathBytes = Encoding.Unicode.GetBytes(libraryPath + '\0');
        var remote = VirtualAllocEx(process.DangerousGetHandle(), 0, (nuint)pathBytes.Length, 0x3000, 0x04);
        if (remote == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!WriteProcessMemory(process.DangerousGetHandle(), remote, pathBytes, (nuint)pathBytes.Length, out var written) ||
                written != (nuint)pathBytes.Length)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var kernel = GetModuleHandle("kernel32.dll");
            var loadLibrary = GetProcAddress(kernel, "LoadLibraryW");
            if (loadLibrary == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            using var thread = new SafeNativeHandle(CreateRemoteThread(process.DangerousGetHandle(), 0, 0, loadLibrary, remote, 0, 0));
            if (thread.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (WaitForSingleObject(thread.DangerousGetHandle(), 20_000) != 0)
                throw new TimeoutException("增强按键组件加载超时。");
            if (!GetExitCodeThread(thread.DangerousGetHandle(), out var exitCode) || exitCode == 0)
                throw new InvalidOperationException("Windows 未能加载增强按键组件。");
        }
        finally
        {
            VirtualFreeEx(process.DangerousGetHandle(), remote, 0, 0x8000);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    [SupportedOSPlatform("windows")]
    private static void EnableDebugPrivilege()
    {
        using var token = new SafeNativeHandle(OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var handle)
            ? handle
            : 0);
        if (token.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var privileges = new TokenPrivileges
        {
            PrivilegeCount = 1,
            Luid = luid,
            Attributes = 0x00000002
        };
        if (!AdjustTokenPrivileges(token.DangerousGetHandle(), false, ref privileges, 0, 0, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var error = Marshal.GetLastWin32Error();
        if (error != 0) throw new System.ComponentModel.Win32Exception(error);
    }

    private static void WriteLog(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiRemoteControl");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "hid-tap.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private sealed class SafeNativeHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeNativeHandle(nint nativeHandle) : base(true) => SetHandle(nativeHandle);
        protected override bool ReleaseHandle() => CloseHandle(this.handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint GetModuleHandle(string moduleName);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern nint GetProcAddress(nint module, string procedureName);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateRemoteThread(nint process, nint attributes, nuint stackSize, nint startAddress, nint parameter, uint flags, nint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeThread(nint thread, out uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(nint token, bool disableAllPrivileges, ref TokenPrivileges newState, uint bufferLength, nint previousState, nint returnLength);
}
