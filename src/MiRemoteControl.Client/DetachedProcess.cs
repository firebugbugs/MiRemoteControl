using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MiRemoteControl.Client;

internal static class DetachedProcess
{
    public static void Start(string path, params string[] arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(path)
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            _ = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException($"无法启动后台：{path}");
            return;
        }
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), flags = 1, showWindow = 0 };
        var commandLine = new StringBuilder(Quote(path));
        foreach (var argument in arguments) commandLine.Append(' ').Append(Quote(argument));
        if (!CreateProcess(path, commandLine, 0, 0, false, 0x08000000,
                0, Path.GetDirectoryName(path), ref startup, out var process))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(process.thread);
        CloseHandle(process.process);
    }
    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"") + '"';
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? reserved, desktop, title;
        public uint x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
        public ushort showWindow, reserved2;
        public nint reservedBytes, stdInput, stdOutput, stdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public nint process, thread; public uint processId, threadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment,
        string? directory, ref STARTUPINFO startup, out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
