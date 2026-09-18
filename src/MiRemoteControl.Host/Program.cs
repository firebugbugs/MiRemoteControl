using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using MiRemoteControl.Contracts;
using MiRemoteControl.Core;

if (!TryResolveOwner(args, out var owner))
{
    Log("host", new InvalidOperationException("Host 必须由 Avalonia 桌面框架使用 --parent-pid 启动。"));
    return;
}
using (owner)
using (var singleton = new Mutex(true, Wire.PipeName, out var isNew))
{
    if (!isNew) return;
    try { RunAsync(owner).GetAwaiter().GetResult(); }
    catch (Exception exception) { Log("host", exception); }
    finally { singleton.ReleaseMutex(); }
}

static async Task RunAsync(Process owner)
{
    var pluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
    var remotePluginDirectory = Path.Combine(pluginDirectory, PluginFolders.Remotes);
    var targetPluginDirectory = Path.Combine(pluginDirectory, PluginFolders.Targets);
    Directory.CreateDirectory(remotePluginDirectory);
    Directory.CreateDirectory(targetPluginDirectory);
    using var catalog = new PluginCatalog(pluginDirectory);
    using var shutdown = new CancellationTokenSource();
    using var targetGate = new SemaphoreSlim(1, 1);
    using var connectionSlots = new SemaphoreSlim(8, 8);

    var selectedTargetPluginId = catalog.Descriptors
        .Where(plugin => plugin.Kind == PluginKinds.Target)
        .OrderBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)
        .Select(plugin => plugin.Id)
        .FirstOrDefault();
    var selectedRemotePluginId = catalog.Descriptors
        .Where(plugin => plugin.Kind == PluginKinds.Remote)
        .OrderBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)
        .Select(plugin => plugin.Id)
        .FirstOrDefault();

    var context = new HostPluginContext(
        catalog,
        targetGate,
        () => selectedTargetPluginId,
        shutdown.Token,
        owner.Id);
    if (selectedRemotePluginId is null)
        catalog.Errors.Add("未找到遥控器插件。");
    else if (!catalog.StartHosted(selectedRemotePluginId, context, out var startError))
        catalog.Errors.Add($"{selectedRemotePluginId}: {startError}");

    _ = MonitorOwnerAsync(owner, shutdown);
    var sessions = new List<Task>();
    try
    {
        while (!shutdown.IsCancellationRequested)
        {
            await connectionSlots.WaitAsync(shutdown.Token);
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(Wire.PipeName, PipeDirection.InOut, 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(shutdown.Token);
            }
            catch
            {
                pipe?.Dispose();
                connectionSlots.Release();
                throw;
            }
            sessions.RemoveAll(task => task.IsCompleted);
            sessions.Add(ServeWithSlotAsync(pipe));
        }
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    finally
    {
        catalog.StopHosted(selectedRemotePluginId);
    }
    await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromSeconds(5))
        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    async Task ServeWithSlotAsync(NamedPipeServerStream pipe)
    {
        try { await ServeAsync(pipe); }
        finally { connectionSlots.Release(); }
    }

    async Task ServeAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var request = await Wire.ReadAsync<CommandRequest>(pipe, timeout.Token);
                timeout.CancelAfter(IsLongRequest(request)
                    ? TimeSpan.FromMinutes(5)
                    : TimeSpan.FromSeconds(30));
                var stop = request.Plugin == "core" && request.Action == "stop";
                CommandResult result;
                if (request.Plugin == "core")
                    result = await ExecuteCoreAsync(request.Action, request.Arguments, timeout.Token);
                else
                    result = await ExecutePluginAsync(request, timeout.Token);

                await Wire.WriteAsync(pipe, new CommandResponse(request.Id, result), timeout.Token);
                if (stop) shutdown.Cancel();
            }
            catch (Exception exception)
            {
                Log("host", exception);
                try
                {
                    await Wire.WriteAsync(pipe,
                        new CommandResponse("", CommandResult.Fail("HostError", exception.Message)),
                        CancellationToken.None);
                }
                catch { /* disconnected client */ }
            }
        }
    }

    async Task<CommandResult> ExecuteCoreAsync(
        string action,
        Dictionary<string, string>? arguments,
        CancellationToken ct)
    {
        return action switch
        {
            "status" => await CoreStatusAsync(ct),
            "plugins" => CommandResult.Ok("已加载插件", PluginSummary()),
            "plugin.icon" => PluginIcon(arguments),
            "remote.select" => SelectTargetPlugin(arguments),
            "remote.driver.select" => SelectRemotePlugin(arguments),
            "remote.status" => await ExecuteRemoteAsync("status", arguments, ct),
            "remote.buttons" => await ExecuteRemoteAsync("buttons", arguments, ct),
            "remote.press" => await ExecuteRemoteAsync("press", arguments, ct),
            "remote.learnVoice" => await ExecuteRemoteAsync("learnVoice", arguments, ct),
            "remote.cancelLearn" => await ExecuteRemoteAsync("cancelLearn", arguments, ct),
            "remote.clearVoice" => await ExecuteRemoteAsync("clearVoice", arguments, ct),
            "remote.reconnectVoice" => await ExecuteRemoteAsync("reconnectVoice", arguments, ct),
            "voice.models" => await ExecuteRemoteAsync("voice.models", arguments, ct),
            "voice.model.status" => await ExecuteRemoteAsync("voice.model.status", arguments, ct),
            "voice.model.select" => await ExecuteRemoteAsync("voice.model.select", arguments, ct),
            "voice.latest.status" => await ExecuteRemoteAsync("voice.latest.status", arguments, ct),
            "voice.latest.play" => await ExecuteRemoteAsync("voice.latest.play", arguments, ct),
            "voice.latest.pause" => await ExecuteRemoteAsync("voice.latest.pause", arguments, ct),
            "voice.latest.toggle" => await ExecuteRemoteAsync("voice.latest.toggle", arguments, ct),
            "remoteVoice.testStart" => await ExecuteRemoteAsync("remoteVoice.testStart", arguments, ct),
            "remoteVoice.testStop" => await ExecuteRemoteAsync("remoteVoice.testStop", arguments, ct),
            "voice.start" => await ExecuteRemoteAsync("voice.start", arguments, ct),
            "voice.stop" => await ExecuteRemoteAsync("voice.stop", arguments, ct),
            "stop" => CommandResult.Ok("后台正在停止"),
            _ => CommandResult.Fail("UnknownAction", "未知核心操作")
        };
    }

    async Task<CommandResult> CoreStatusAsync(CancellationToken ct)
    {
        var remoteStatus = await ExecuteRemoteAsync("status", null, ct);
        if (!remoteStatus.Success)
            return CommandResult.Fail(remoteStatus.Code, remoteStatus.Message);
        var state = JsonSerializer.SerializeToElement(remoteStatus.Data, Wire.Json);
        return CommandResult.Ok("后台已就绪", new
        {
            protocolVersion = Wire.Version,
            processId = Environment.ProcessId,
            ownerProcessId = owner.Id,
            pluginDirectory,
            remotePluginDirectory,
            targetPluginDirectory,
            selectedPlugin = selectedTargetPluginId,
            selectedRemotePlugin = selectedRemotePluginId,
            plugins = catalog.Descriptors,
            pluginSources = catalog.SourcePaths.Select(item => new { id = item.Key, path = item.Value }),
            errors = catalog.Errors,
            remote = ReadProperty(state, "remote"),
            hidTap = ReadProperty(state, "hidTap"),
            remoteVoice = ReadProperty(state, "remoteVoice"),
            voice = ReadProperty(state, "voice"),
            latestVoice = ReadProperty(state, "latestVoice"),
            speechModel = ReadProperty(state, "speechModel"),
            whisper = ReadProperty(state, "whisper"),
            voiceModels = ReadProperty(state, "voiceModels"),
            audioDevices = ReadProperty(state, "audioDevices")
        });
    }

    object PluginSummary() => new
    {
        pluginDirectory,
        remotePluginDirectory,
        targetPluginDirectory,
        selectedPlugin = selectedTargetPluginId,
        selectedRemotePlugin = selectedRemotePluginId,
        plugins = catalog.Descriptors,
        pluginSources = catalog.SourcePaths.Select(item => new { id = item.Key, path = item.Value }),
        errors = catalog.Errors
    };

    // Icons are served on demand instead of inside the frequent status
    // payload: each package icon is a few KB of base64 that never changes
    // between host restarts, and the UI caches it per host process.
    CommandResult PluginIcon(Dictionary<string, string>? arguments)
    {
        if (!TryReadSinglePluginArgument(arguments, out var pluginId))
            return CommandResult.Fail("InvalidArgument", "plugin.icon 需要且只接受 plugin 参数。");
        var descriptor = catalog.Descriptors.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
            return CommandResult.Fail("PluginNotFound", $"未找到插件：{pluginId}");
        var icon = catalog.GetIcon(descriptor.Id);
        return icon is null
            ? CommandResult.Fail("PluginIconMissing", $"插件 {descriptor.Name} 没有图标。")
            : CommandResult.Ok($"插件 {descriptor.Name} 的图标", new { icon = Convert.ToBase64String(icon) });
    }

    CommandResult SelectTargetPlugin(Dictionary<string, string>? arguments)
    {
        if (!TryReadSinglePluginArgument(arguments, out var pluginId))
            return CommandResult.Fail("InvalidArgument", "remote select 需要且只接受 plugin 参数。");
        var descriptor = catalog.Descriptors.FirstOrDefault(plugin =>
            plugin.Kind == PluginKinds.Target &&
            string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
            return CommandResult.Fail("PluginNotFound", $"未找到可工作的目标插件：{pluginId}");
        selectedTargetPluginId = descriptor.Id;
        return CommandResult.Ok($"当前工作插件已切换为 {descriptor.Name}。",
            new { selectedPlugin = selectedTargetPluginId });
    }

    CommandResult SelectRemotePlugin(Dictionary<string, string>? arguments)
    {
        if (!TryReadSinglePluginArgument(arguments, out var pluginId))
            return CommandResult.Fail("InvalidArgument", "remote driver select 需要且只接受 plugin 参数。");
        var descriptor = catalog.Descriptors.FirstOrDefault(plugin =>
            plugin.Kind == PluginKinds.Remote &&
            string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
            return CommandResult.Fail("PluginNotFound", $"未找到遥控器插件：{pluginId}");
        if (string.Equals(selectedRemotePluginId, descriptor.Id, StringComparison.OrdinalIgnoreCase))
            return CommandResult.Ok($"当前遥控器插件已经是 {descriptor.Name}。",
                new { selectedRemotePlugin = selectedRemotePluginId });

        var previous = selectedRemotePluginId;
        catalog.StopHosted(previous);
        if (!catalog.StartHosted(descriptor.Id, context, out var error))
        {
            if (previous is not null) catalog.StartHosted(previous, context, out _);
            return CommandResult.Fail("PluginStartFailed", error ?? $"无法启动遥控器插件：{descriptor.Name}");
        }
        selectedRemotePluginId = descriptor.Id;
        return CommandResult.Ok($"当前遥控器插件已切换为 {descriptor.Name}。",
            new { selectedRemotePlugin = selectedRemotePluginId });
    }

    async Task<CommandResult> ExecuteRemoteAsync(
        string action,
        Dictionary<string, string>? arguments,
        CancellationToken ct)
    {
        if (selectedRemotePluginId is null)
            return CommandResult.Fail("RemotePluginMissing", "未加载遥控器插件。");
        return await catalog.ExecuteAsync(
            new CommandRequest("", selectedRemotePluginId, action, arguments),
            ct);
    }

    async Task<CommandResult> ExecutePluginAsync(CommandRequest request, CancellationToken ct)
    {
        var descriptor = catalog.Descriptors.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, request.Plugin, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
            return CommandResult.Fail("PluginNotFound", $"未找到插件：{request.Plugin}");
        return descriptor.Kind == PluginKinds.Remote
            ? await catalog.ExecuteAsync(request, ct)
            : await context.ExecuteAsync(request.Plugin, request.Action, request.Arguments, ct);
    }
}

static async Task MonitorOwnerAsync(Process owner, CancellationTokenSource shutdown)
{
    try
    {
        await owner.WaitForExitAsync(shutdown.Token);
        shutdown.Cancel();
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    catch (Exception exception)
    {
        Log("owner-monitor", exception);
        shutdown.Cancel();
    }
}

static bool TryResolveOwner(string[] arguments, out Process owner)
{
    owner = null!;
    if (arguments.Length != 2 || arguments[0] != "--parent-pid" ||
        !int.TryParse(arguments[1], out var processId) || processId <= 0)
        return false;
    try
    {
        owner = Process.GetProcessById(processId);
        return !owner.HasExited;
    }
    catch { return false; }
}

static bool TryReadSinglePluginArgument(
    Dictionary<string, string>? arguments,
    out string pluginId)
{
    pluginId = "";
    if (arguments is not { Count: 1 } ||
        !arguments.TryGetValue("plugin", out var value) ||
        string.IsNullOrWhiteSpace(value))
        return false;
    pluginId = value;
    return true;
}

static JsonElement ReadProperty(JsonElement element, string name)
{
    if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        return value.Clone();

    // JsonElement's default value is Undefined, which cannot be serialized
    // inside the aggregate core status payload. Optional plugin fields must be
    // represented as JSON null instead, otherwise the whole UI status refresh
    // fails and makes a connected remote appear offline.
    return JsonSerializer.SerializeToElement<object?>(null, Wire.Json);
}

static bool IsLongRequest(CommandRequest request) =>
    (request.Plugin == "core" && request.Action == "voice.model.select") ||
    (request.Plugin != "core" && request.Action == "voice.model.select");

static void Log(string source, Exception exception)
{
    try
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiRemoteControl",
            "logs");
        Directory.CreateDirectory(directory);
        File.AppendAllText(
            Path.Combine(directory, $"host-{DateTime.Today:yyyyMMdd}.log"),
            $"{DateTimeOffset.Now:O} [{source}] {exception.GetType().Name}: {exception.Message}{Environment.NewLine}");
    }
    catch { /* diagnostics must not terminate the host */ }
}

internal sealed class HostPluginContext(
    PluginCatalog catalog,
    SemaphoreSlim targetGate,
    Func<string?> selectedTarget,
    CancellationToken hostShutdown,
    int ownerProcessId) : IRemotePluginHostContext
{
    public string? SelectedTargetPluginId => selectedTarget();
    public IReadOnlyList<PluginDescriptor> Plugins => catalog.Descriptors.Where(p => p.Kind == PluginKinds.Target).ToArray();

    public bool IsStudioForeground()
    {
        if (!OperatingSystem.IsWindows() || ownerProcessId <= 0) return false;
        var window = GetForegroundWindow();
        // A thread id of zero means the window handle is stale — treat that as
        // "not the studio" so remote keys keep their target behavior.
        return window != 0 &&
               GetWindowThreadProcessId(window, out var processId) != 0 &&
               processId == ownerProcessId;
    }

    public async Task<CommandResult> ExecuteAsync(
        string pluginId,
        string action,
        Dictionary<string, string>? arguments = null,
        CancellationToken ct = default)
    {
        if (!Plugins.Any(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase)))
            return CommandResult.Fail("HarnessNotFound", "遥控器宿主上下文只能调用 Harness 插件。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, hostShutdown);
        // Standard input probes (and the legacy input.read action) are passive
        // UIA reads polled continuously by fullscreen mirrors. Serializing them
        // behind foreground-taking actions
        // (voice input holds the gate for seconds while typing) starved the
        // writers and made mirrored text lag a full utterance behind.
        if (action is "input.probe" or "input.read" or "input.watch")
            return await catalog.ExecuteAsync(
                new CommandRequest("", pluginId, action, arguments),
                linked.Token);
        await targetGate.WaitAsync(linked.Token);
        try
        {
            return await catalog.ExecuteAsync(
                new CommandRequest("", pluginId, action, arguments),
                linked.Token);
        }
        finally { targetGate.Release(); }
    }

    public void Log(string source, Exception exception) => ProgramLog(source, exception);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    private static void ProgramLog(string source, Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MiRemoteControl",
                "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, $"host-{DateTime.Today:yyyyMMdd}.log"),
                $"{DateTimeOffset.Now:O} [{source}] {exception.GetType().Name}: {exception.Message}{Environment.NewLine}");
        }
        catch { /* diagnostics must not terminate the host */ }
    }
}
