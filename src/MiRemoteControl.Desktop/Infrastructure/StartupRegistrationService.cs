using Microsoft.Win32;

namespace MiRemoteControl.Desktop.Infrastructure;

internal static class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MiRemoteControl";

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("随系统启动仅支持 Windows。");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true) ??
                        throw new InvalidOperationException("无法打开当前用户启动项。");
        if (enabled) key.SetValue(ValueName, CommandLine(), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string CommandLine()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("无法定位当前程序文件。");
        return $"\"{executable}\" --background";
    }
}
