using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MiRemoteControl.Desktop.Infrastructure;

internal sealed class EnhancedRemoteKeysController : IDisposable
{
    private EventWaitHandle? _stopEvent;
    private Process? _helper;

    public bool IsRunning
    {
        get
        {
            try { return _helper is { HasExited: false }; }
            catch { return false; }
        }
    }

    public async Task<(bool Success, string Message)> StartAsync()
    {
        if (!OperatingSystem.IsWindows()) return (false, "增强按键捕获仅支持 Windows。");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return (false, "增强按键捕获目前仅支持 x64 Windows。");
        if (IsRunning) return (true, "增强按键捕获已启动。");

        Stop();
        var stopEventName = $"Local\\MiRemoteControl.HidTap.Stop.{Environment.ProcessId}.{Guid.NewGuid():N}";
        _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, stopEventName);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            Stop();
            return (false, "无法定位当前程序文件。");
        }

        var parentProcessId = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"--hid-tap-helper {parentProcessId} {stopEventName}"
        };

        try
        {
            WriteLog($"请求启动增强按键辅助进程：{executable} {start.Arguments}");
            _helper = Process.Start(start);
            if (_helper is null) throw new InvalidOperationException("辅助进程未启动。");
            await Task.Delay(500);
            if (_helper.HasExited)
            {
                WriteLog($"增强按键辅助进程提前退出，代码 {_helper.ExitCode}。");
                Stop();
                return (false, "增强按键组件启动失败，请查看日志。");
            }
            WriteLog($"增强按键辅助进程已启动，PID {_helper.Id}。");
            return (true, "增强按键捕获正在准备；首次启用需要下载并校验组件。");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            WriteLog("用户取消了增强按键管理员授权。");
            Stop();
            return (false, "已取消管理员授权。");
        }
        catch (Exception exception)
        {
            WriteLog("增强按键辅助进程启动失败：" + exception);
            Stop();
            return (false, $"增强按键组件启动失败：{exception.Message}");
        }
    }

    public void Stop()
    {
        try { _stopEvent?.Set(); } catch { }
        if (_helper is not null)
        {
            try { _helper.WaitForExit(1200); } catch { }
            _helper.Dispose();
        }
        _helper = null;
        _stopEvent?.Dispose();
        _stopEvent = null;
    }

    public void Dispose() => Stop();

    private static void WriteLog(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiRemoteControl");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "hid-tap.log"),
                $"{DateTimeOffset.Now:O} [desktop] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
