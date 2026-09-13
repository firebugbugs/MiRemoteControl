using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using MiRemoteControl.Contracts;

namespace MiRemoteControl.Client;

public sealed class CoreClient
{
    public async Task<CommandResult> InvokeAsync(string plugin, string action, Dictionary<string,string>? arguments = null,
        int timeoutMs = 20000, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        using var pipe = new NamedPipeClientStream(".", Wire.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(Math.Min(1500, timeoutMs), timeout.Token);
        var id = Guid.NewGuid().ToString("N");
        await Wire.WriteAsync(pipe, new CommandRequest(id, plugin, action, arguments), timeout.Token);
        var response = await Wire.ReadAsync<CommandResponse>(pipe, timeout.Token);
        if (response.Id != id && response.Id != "") throw new InvalidDataException("Response ID mismatch.");
        if (plugin == "core" && action == "status" && response.Result.Data is System.Text.Json.JsonElement status &&
            (!status.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != Wire.Version))
            throw new InvalidDataException("核心协议版本不兼容，请使用同一构建的客户端与后台。");
        return response.Result;
    }
    public async Task<CommandResult> StartAsync(CancellationToken ct = default)
    {
        if (await TryStatusAsync(ct) is { } running) return running;
        var desktopName = OperatingSystem.IsWindows() ? "MiRemoteControl.exe" : "MiRemoteControl";
        var path = ResolveSiblingPath(desktopName);
        if (path is null)
            return CommandResult.Fail("DesktopMissing", "未找到 Avalonia 桌面程序。请先运行 build.ps1，或从 artifacts/app 启动程序。");
        DetachedProcess.Start(path);
        return await WaitUntilReadyAsync(ct);
    }

    public async Task<CommandResult> StartForDesktopAsync(int ownerProcessId, CancellationToken ct = default)
    {
        if (ownerProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(ownerProcessId));
        if (await TryStatusAsync(ct) is { } running) return running;
        var hostName = OperatingSystem.IsWindows() ? "MiRemoteControl.Host.exe" : "MiRemoteControl.Host";
        var path = ResolveSiblingPath(hostName);
        if (path is null)
            return CommandResult.Fail("HostMissing", "未找到后台 Host。请先运行 build.ps1，或从 artifacts/app 启动程序。");
        DetachedProcess.Start(path, "--parent-pid",
            ownerProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await WaitUntilReadyAsync(ct);
    }

    private async Task<CommandResult?> TryStatusAsync(CancellationToken ct)
    {
        try { return await InvokeAsync("core", "status", timeoutMs: 500, ct: ct); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    private async Task<CommandResult> WaitUntilReadyAsync(CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(200, ct);
            try { return await InvokeAsync("core", "status", timeoutMs: 500, ct: ct); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException) { ct.ThrowIfCancellationRequested(); }
        }
        return CommandResult.Fail("StartTimeout", "后台启动超时，请检查日志。");
    }

    private static string? ResolveSiblingPath(string fileName)
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, fileName) };
        // When launched from Visual Studio/Rider, Desktop output is under
        // src/MiRemoteControl.Desktop/bin/... while the runnable host is in
        // the repository's artifacts/app directory. This keeps F5 functional
        // without adding a Windows-only project reference to Avalonia.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, "artifacts", "app", fileName));
        return candidates.FirstOrDefault(File.Exists);
    }
    public async Task AllowForegroundAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return;
        var status = await InvokeAsync("core", "status", ct: ct);
        if (status.Data is System.Text.Json.JsonElement data && data.TryGetProperty("processId", out var id))
            AllowSetForegroundWindow(id.GetInt32());
    }
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
