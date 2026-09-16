using System.Text.Json;
using MiRemoteControl.Contracts;

namespace MiRemoteControl.Plugin.XiaomiRemote;

public sealed class XiaomiRemotePlugin : IRemotePlugin, IAsyncRemotePlugin, IHostedRemotePlugin
{
    private readonly object _lifecycle = new();
    private IRemotePluginHostContext? _host;
    private CancellationTokenSource? _shutdown;
    private RemoteSettingsStore? _settingsStore;
    private RemoteSettings? _settings;
    private RemoteInputMonitor? _remote;
    private HidTapInputSource? _hidTap;
    private VoiceInputService? _voice;
    private WhisperVoiceService? _speech;
    private AtvvRemoteVoiceService? _remoteVoice;
    private LatestVoiceRecordingService? _latestVoice;
    private readonly object _powerDispatchSync = new();
    private readonly object _remoteCommandSync = new();
    private readonly object _backRepeatSync = new();
    private CancellationTokenSource? _backRepeat;
    private DateTimeOffset _lastPhysicalPower = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRemoteOk = DateTimeOffset.MinValue;

    public PluginDescriptor Descriptor { get; } = new(
        "mrc.remote.xiaomi",
        "小米蓝牙遥控器",
        "1.1.0",
        [
            new("status", "状态", "读取遥控器、语音与模型状态"),
            new("buttons", "按键列表", "列出可模拟的遥控器按键"),
            new("press", "模拟按键", "模拟一次遥控器按键"),
            new("learnVoice", "学习语音键", "等待并记录物理语音键"),
            new("cancelLearn", "取消学习", "取消语音键学习"),
            new("clearVoice", "清除语音键", "清除保存的语音键"),
            new("reconnectVoice", "重连遥控器语音", "重新连接 ATVV 语音服务"),
            new("voice.models", "语音模型", "列出本地语音模型"),
            new("voice.model.status", "模型状态", "读取当前语音模型状态"),
            new("voice.model.select", "选择模型", "切换本地语音模型"),
            new("voice.latest.status", "录音状态", "读取最近一条语音录音状态"),
            new("voice.latest.play", "播放录音", "播放最近一条语音录音"),
            new("voice.latest.pause", "暂停录音", "暂停最近一条语音录音"),
            new("voice.latest.toggle", "切换播放", "播放或暂停最近一条语音录音"),
            new("remoteVoice.testStart", "开始语音诊断", "开始 ATVV 语音抓包测试"),
            new("remoteVoice.testStop", "停止语音诊断", "结束 ATVV 语音抓包测试"),
            new("voice.start", "开始本地语音", "开始本地麦克风语音识别"),
            new("voice.stop", "停止本地语音", "停止本地麦克风语音识别")
        ],
        PluginKinds.Remote);

    public void Start(IRemotePluginHostContext context)
    {
        lock (_lifecycle)
        {
            if (_shutdown is not null) return;

            var settingsStore = new RemoteSettingsStore();
            var settings = settingsStore.Load();
            var remote = new RemoteInputMonitor(settings.VoiceKey);
            var hidTap = new HidTapInputSource();
            var voice = new VoiceInputService();
            var speech = new WhisperVoiceService();
            var remoteVoice = new AtvvRemoteVoiceService(
                settings.BatteryLevel,
                settings.BatteryUpdatedAt,
                (level, updatedAt) =>
                {
                    lock (_lifecycle)
                    {
                        if (!ReferenceEquals(_settingsStore, settingsStore)) return;
                        var updated = (_settings ?? new RemoteSettings()) with
                        {
                            BatteryLevel = level,
                            BatteryUpdatedAt = updatedAt
                        };
                        _settings = updated;
                        settingsStore.Save(updated);
                    }
                });
            var latestVoice = new LatestVoiceRecordingService();
            var shutdown = new CancellationTokenSource();

            _host = context;
            _settingsStore = settingsStore;
            _settings = settings;
            _remote = remote;
            _hidTap = hidTap;
            _voice = voice;
            _speech = speech;
            _remoteVoice = remoteVoice;
            _latestVoice = latestVoice;
            _shutdown = shutdown;

            remote.Learned += key =>
            {
                lock (_lifecycle)
                {
                    if (!ReferenceEquals(_settingsStore, settingsStore)) return;
                    var updated = (_settings ?? new RemoteSettings()) with { VoiceKey = key };
                    _settings = updated;
                    settingsStore.Save(updated);
                }
            };
            hidTap.Report += frame =>
            {
                var accepted = false;
                try { accepted = remote.SubmitHidTapReport(frame.DevicePath, Convert.FromHexString(frame.ReportHex)); }
                catch (FormatException) { }
                hidTap.Accept(frame, accepted);
            };
            remote.Input += input => HandleRemoteInput(
                input, context, remote, remoteVoice, shutdown.Token);
            remoteVoice.AudioStarted += _ => latestVoice.StopForNewCapture();
            remoteVoice.AudioCompleted += pcm => _ = RecognizeRemoteSpeechSafelyAsync(
                pcm, context, voice, speech, remoteVoice, latestVoice, shutdown.Token);

            _ = remoteVoice.ConnectAsync();
            _ = speech.InitializeAsync();
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            if (_shutdown is null) return;
            _shutdown.Cancel();
            _latestVoice?.Dispose();
            _remoteVoice?.Dispose();
            _speech?.Dispose();
            _voice?.Dispose();
            _hidTap?.Dispose();
            _remote?.Dispose();
            _shutdown.Dispose();

            _latestVoice = null;
            _remoteVoice = null;
            _speech = null;
            _voice = null;
            _hidTap = null;
            _remote = null;
            _settings = null;
            _settingsStore = null;
            _shutdown = null;
            _host = null;
        }
    }

    public CommandResult Execute(string action, IReadOnlyDictionary<string, string> arguments) =>
        ExecuteAsync(action, arguments, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<CommandResult> ExecuteAsync(
        string action,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var remote = _remote;
        var hidTap = _hidTap;
        var voice = _voice;
        var speech = _speech;
        var remoteVoice = _remoteVoice;
        var latestVoice = _latestVoice;
        var host = _host;
        if (remote is null || hidTap is null || voice is null || speech is null ||
            remoteVoice is null || latestVoice is null || host is null)
            return CommandResult.Fail("PluginNotStarted", "小米遥控器插件尚未启动。");

        return action switch
        {
            "status" => Status(remote, hidTap, voice, speech, remoteVoice, latestVoice),
            "buttons" => CommandResult.Ok("可用的虚拟遥控器按键",
                new { buttons = RemoteButtons.Supported, excluded = new[] { "Voice" }, selectedPlugin = host.SelectedTargetPluginId }),
            "press" => await PressRemoteAsync(arguments, remote, host, ct),
            "learnVoice" => LearnVoice(remote),
            "cancelLearn" => CancelLearn(remote),
            "clearVoice" => ClearVoice(remote),
            "reconnectVoice" => await ReconnectRemoteVoiceAsync(remoteVoice),
            "voice.models" => VoiceModels(speech, "已发现本地语音模型"),
            "voice.model.status" => VoiceModels(speech, "语音模型状态"),
            "voice.model.select" => await SelectVoiceModelAsync(arguments, speech),
            "voice.latest.status" => CommandResult.Ok(latestVoice.Status.Message, latestVoice.Status),
            "voice.latest.play" => LatestVoicePlayback(latestVoice, "play"),
            "voice.latest.pause" => LatestVoicePlayback(latestVoice, "pause"),
            "voice.latest.toggle" => LatestVoicePlayback(latestVoice, "toggle"),
            "remoteVoice.testStart" => await StartRemoteVoiceTestAsync(remoteVoice),
            "remoteVoice.testStop" => StopRemoteVoiceTest(remoteVoice),
            "voice.start" => StartVoice(arguments, voice, latestVoice),
            "voice.stop" => await StopVoiceAsync(voice, latestVoice),
            _ => CommandResult.Fail("UnknownAction", "未知小米遥控器操作")
        };
    }

    private CommandResult Status(
        RemoteInputMonitor remote,
        HidTapInputSource hidTap,
        VoiceInputService voice,
        WhisperVoiceService speech,
        AtvvRemoteVoiceService remoteVoice,
        LatestVoiceRecordingService latestVoice)
    {
        return CommandResult.Ok("小米遥控器服务已就绪", new
        {
            remote = remote.Status(),
            hidTap = hidTap.Status,
            remoteVoice = remoteVoice.Status,
            voice = voice.Status(),
            latestVoice = latestVoice.Status,
            speechModel = speech.Status,
            whisper = speech.Status,
            voiceModels = speech.Models(),
            audioDevices = voice.Devices()
        });
    }

    private static CommandResult VoiceModels(WhisperVoiceService speech, string message) =>
        CommandResult.Ok(message, new
        {
            directory = speech.ModelDirectory,
            selectedModel = speech.Status.SelectedModel,
            loadedModel = speech.Status.LoadedModel,
            models = speech.Models(force: true),
            status = speech.Status
        });

    private static CommandResult LearnVoice(RemoteInputMonitor remote)
    {
        remote.BeginLearning();
        return CommandResult.Ok("学习已开始，请按一次遥控器语音键。", remote.Status());
    }

    private static CommandResult CancelLearn(RemoteInputMonitor remote)
    {
        remote.CancelLearning();
        return CommandResult.Ok("已取消学习。");
    }

    private CommandResult ClearVoice(RemoteInputMonitor remote)
    {
        remote.SetVoiceKey(null);
        var updated = (_settings ?? new RemoteSettings()) with { VoiceKey = null };
        _settings = updated;
        _settingsStore!.Save(updated);
        return CommandResult.Ok("已清除语音键绑定。");
    }

    private async Task<CommandResult> PressRemoteAsync(
        IReadOnlyDictionary<string, string> arguments,
        RemoteInputMonitor remote,
        IRemotePluginHostContext host,
        CancellationToken ct)
    {
        if (arguments.Count != 1 || !arguments.TryGetValue("button", out var requested) ||
            !RemoteButtons.TryNormalize(requested, out var button))
            return CommandResult.Fail("InvalidButton",
                $"remote press 需要一个有效按键：{string.Join(", ", RemoteButtons.Supported)}；Voice 不支持模拟。");
        remote.SimulateClick(button);
        if (!string.Equals(button, "Power", StringComparison.OrdinalIgnoreCase))
            return CommandResult.Ok($"已模拟遥控器按键：{button}",
                new { button, handled = false, selectedPlugin = host.SelectedTargetPluginId, remote = remote.Status() });
        return await ExecuteSelectedPowerAsync(host, ct);
    }

    private void HandleRemoteInput(
        RemoteInputEvent input,
        IRemotePluginHostContext host,
        RemoteInputMonitor remote,
        AtvvRemoteVoiceService remoteVoice,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        var virtualInput = input.Key.DevicePath.StartsWith("VIRTUAL:", StringComparison.OrdinalIgnoreCase);
        if (input.IsDown && !virtualInput && string.Equals(input.Button, "Power", StringComparison.OrdinalIgnoreCase))
        {
            var dispatch = false;
            lock (_powerDispatchSync)
            {
                if (DateTimeOffset.UtcNow - _lastPhysicalPower > TimeSpan.FromMilliseconds(450))
                {
                    _lastPhysicalPower = DateTimeOffset.UtcNow;
                    dispatch = true;
                }
            }
            if (dispatch) _ = ExecuteSelectedPowerSafelyAsync(host, ct);
        }
        if (virtualInput) return;
        if (input.Button is "Back" or "Ok")
        {
            if (input.Button == "Back")
            {
                // Hold-to-repeat: Back deletes while it stays pressed, like a
                // keyboard backspace.
                HandleBackKey(input.IsDown, host, ct);
            }
            else if (input.IsDown)
            {
                var dispatch = false;
                lock (_remoteCommandSync)
                {
                    if (DateTimeOffset.UtcNow - _lastRemoteOk > TimeSpan.FromMilliseconds(180))
                    {
                        _lastRemoteOk = DateTimeOffset.UtcNow;
                        dispatch = true;
                    }
                }
                if (dispatch) _ = ExecuteSelectedCommandSafelyAsync(host, ct);
            }
        }
        if (input.IsDown && !remoteVoice.Status.Ready) _ = remoteVoice.ConnectAsync();
        if (input.Button == "Voice") remoteVoice.NotifyVoiceKey(input.IsDown);
        _ = remote.IsVoiceKey(input.Key);
    }

    private async Task ExecuteSelectedPowerSafelyAsync(IRemotePluginHostContext host, CancellationToken ct)
    {
        try { await ExecuteSelectedPowerAsync(host, ct); }
        catch (Exception exception) { host.Log(Descriptor.Id, exception); }
    }

    private async Task<CommandResult> ExecuteSelectedPowerAsync(IRemotePluginHostContext host, CancellationToken ct)
    {
        var pluginId = host.SelectedTargetPluginId;
        if (string.IsNullOrWhiteSpace(pluginId))
            return CommandResult.Fail("NoSelectedPlugin", "当前没有可工作的插件。");
        var descriptor = host.Plugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null) return CommandResult.Fail("PluginNotFound", $"未找到当前工作插件：{pluginId}");
        var actions = descriptor.Actions.Select(action => action.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actions.Contains(HarnessPluginActions.Status) ||
            !actions.Contains(HarnessPluginActions.Open) ||
            !actions.Contains(HarnessPluginActions.Close))
            return CommandResult.Fail("PowerUnsupported",
                $"插件 {descriptor.Name} 未提供 status/open/close，不能使用电源键。");

        var status = await host.ExecuteAsync(pluginId, HarnessPluginActions.Status, ct: ct);
        if (!status.Success) return status;
        if (!TryReadBool(status.Data, "running", out var running))
            return CommandResult.Fail("InvalidPluginStatus", $"插件 {descriptor.Name} 的 status 未返回 running。");
        if (!TryReadBool(status.Data, "focused", out var focused))
            return CommandResult.Fail("InvalidPluginStatus", $"插件 {descriptor.Name} 的 status 未返回 focused。");
        var action = running && focused ? HarnessPluginActions.Close : HarnessPluginActions.Open;
        var result = await host.ExecuteAsync(pluginId, action, ct: ct);
        return result.Success
            ? CommandResult.Ok(result.Message,
                new
                {
                    button = "Power",
                    handled = true,
                    selectedPlugin = pluginId,
                    pluginAction = action,
                    running = action == "open",
                    focused = action == "open"
                })
            : result;
    }

    private void HandleBackKey(bool isDown, IRemotePluginHostContext host, CancellationToken ct)
    {
        CancellationTokenSource? repeater = null;
        lock (_backRepeatSync)
        {
            if (!isDown)
            {
                _backRepeat?.Cancel();
                _backRepeat = null;
                return;
            }
            if (_backRepeat is not null) return; // already held
            _backRepeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            repeater = _backRepeat;
        }
        _ = RepeatBackspaceAsync(host, repeater);
    }

    private async Task RepeatBackspaceAsync(IRemotePluginHostContext host, CancellationTokenSource repeater)
    {
        var ct = repeater.Token;
        try
        {
            if (!TryGetSelectedTarget(host, HarnessPluginActions.Backspace, out var pluginId)) return;
            // Like a held keyboard BKSP: the first deletion fires immediately,
            // then after the initial delay it repeats at a steady rate for as
            // long as the key stays down.
            await DeleteOneAsync(host, pluginId, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(450), ct);
            while (!ct.IsCancellationRequested)
            {
                await DeleteOneAsync(host, pluginId, ct);
                await Task.Delay(TimeSpan.FromMilliseconds(90), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception) { host.Log(Descriptor.Id, exception); }
        finally
        {
            lock (_backRepeatSync)
            {
                if (ReferenceEquals(_backRepeat, repeater)) _backRepeat = null;
            }
            repeater.Dispose();
        }
    }

    private async Task DeleteOneAsync(IRemotePluginHostContext host, string pluginId, CancellationToken ct)
    {
        await host.ExecuteAsync(pluginId, HarnessPluginActions.Backspace, ct: ct);
    }

    private async Task ExecuteSelectedCommandSafelyAsync(IRemotePluginHostContext host, CancellationToken ct)
    {
        try
        {
            if (!TryGetSelectedTarget(host, HarnessPluginActions.Status, out var pluginId)) return;
            var descriptor = FindPlugin(host, pluginId);
            if (descriptor is null) return;
            var actions = descriptor.Actions.Select(action => action.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var status = await host.ExecuteAsync(pluginId, HarnessPluginActions.Status, ct: ct);
            if (!status.Success) return;
            var action = TryReadBool(status.Data, "canStop", out var canStop) && canStop && actions.Contains(HarnessPluginActions.Stop)
                ? HarnessPluginActions.Stop
                : actions.Contains(HarnessPluginActions.Send) ? HarnessPluginActions.Send : null;
            if (action is not null) await host.ExecuteAsync(pluginId, action, ct: ct);
        }
        catch (Exception exception) { host.Log(Descriptor.Id, exception); }
    }

    private async Task RecognizeRemoteSpeechSafelyAsync(
        byte[] pcm,
        IRemotePluginHostContext host,
        VoiceInputService voice,
        WhisperVoiceService speech,
        AtvvRemoteVoiceService remoteVoice,
        LatestVoiceRecordingService latestVoice,
        CancellationToken ct)
    {
        try
        {
            if (!latestVoice.SavePcm(pcm))
                host.Log(Descriptor.Id, new IOException(latestVoice.Status.LastError ?? latestVoice.Status.Message));
            var text = await speech.RecognizePcmAsync(pcm);
            if (string.IsNullOrWhiteSpace(text))
                text = await voice.RecognizePcmAsync(pcm, "小米遥控器 · ATVV");
            remoteVoice.ReportRecognition(text, speech.Status.LastError ?? voice.Status().LastError);
            if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(text)) return;
            if (!TryGetSelectedTarget(host, HarnessPluginActions.Input, out var pluginId)) return;
            var input = await host.ExecuteAsync(pluginId, HarnessPluginActions.Input, new() { ["text"] = text }, ct);
            if (!input.Success) VoiceDiagnostics.Log($"TARGET-INPUT-FAIL plugin={pluginId} {input.Code}: {input.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception) { host.Log(Descriptor.Id, exception); }
    }

    private static PluginDescriptor? FindPlugin(IRemotePluginHostContext host, string pluginId) =>
        host.Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetSelectedTarget(IRemotePluginHostContext host, string requiredAction, out string pluginId)
    {
        pluginId = host.SelectedTargetPluginId ?? "";
        var descriptor = pluginId.Length == 0 ? null : FindPlugin(host, pluginId);
        return descriptor?.Kind == PluginKinds.Target && descriptor.Actions.Any(action =>
            string.Equals(action.Id, requiredAction, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadBool(object? data, string property, out bool value)
    {
        value = false;
        if (data is null) return false;
        var element = data is JsonElement json ? json : JsonSerializer.SerializeToElement(data, Wire.Json);
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var item) ||
            item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = item.GetBoolean();
        return true;
    }

    private async Task<CommandResult> SelectVoiceModelAsync(
        IReadOnlyDictionary<string, string> arguments,
        WhisperVoiceService speech)
    {
        if (arguments.Count != 1 || !arguments.TryGetValue("model", out var modelId) || string.IsNullOrWhiteSpace(modelId))
            return CommandResult.Fail("InvalidArgument", "voice model select 需要且只接受 model 参数。");
        var result = await speech.SelectModelAsync(modelId);
        return result.Success
            ? CommandResult.Ok(result.Message, new
            {
                directory = speech.ModelDirectory,
                selectedModel = speech.Status.SelectedModel,
                loadedModel = speech.Status.LoadedModel,
                model = result.Model,
                status = speech.Status
            })
            : CommandResult.Fail(result.Code, result.Message);
    }

    private CommandResult StartVoice(
        IReadOnlyDictionary<string, string> arguments,
        VoiceInputService voice,
        LatestVoiceRecordingService latestVoice)
    {
        int? device = _settings?.AudioDevice;
        if (arguments.TryGetValue("device", out var raw))
        {
            if (!int.TryParse(raw, out var parsed))
                return CommandResult.Fail("InvalidArgument", "device 必须是整数。");
            device = parsed;
        }
        if (!voice.Start(device))
            return CommandResult.Fail("VoiceUnavailable", voice.Status().LastError ?? "语音识别不可用。");
        latestVoice.StopForNewCapture();
        var updated = (_settings ?? new RemoteSettings()) with { AudioDevice = device };
        _settings = updated;
        _settingsStore!.Save(updated);
        return CommandResult.Ok("本地语音识别已开始。", voice.Status());
    }

    private static async Task<CommandResult> StopVoiceAsync(
        VoiceInputService voice,
        LatestVoiceRecordingService latestVoice)
    {
        var text = await voice.StopAsync();
        if (voice.TakeLastPcm() is { Length: > 0 } pcm && !latestVoice.SavePcm(pcm))
            return CommandResult.Fail("VoiceRecordingFailed",
                latestVoice.Status.LastError ?? latestVoice.Status.Message);
        return string.IsNullOrWhiteSpace(text)
            ? CommandResult.Fail("NoSpeech", voice.Status().LastError ?? "未识别到语音。")
            : CommandResult.Ok("语音已识别。", new { text });
    }

    private static CommandResult LatestVoicePlayback(LatestVoiceRecordingService latestVoice, string operation)
    {
        bool success;
        string message;
        switch (operation)
        {
            case "play": success = latestVoice.Play(out message); break;
            case "pause": success = latestVoice.Pause(out message); break;
            case "toggle": success = latestVoice.Toggle(out message); break;
            default: success = false; message = "未知的语音播放操作"; break;
        }
        return success
            ? CommandResult.Ok(message, latestVoice.Status)
            : CommandResult.Fail("VoicePlaybackUnavailable", message);
    }

    private static bool SetUnknown(out string message)
    {
        message = "未知的语音播放操作";
        return false;
    }

    private static async Task<CommandResult> ReconnectRemoteVoiceAsync(AtvvRemoteVoiceService remoteVoice)
    {
        await remoteVoice.ConnectAsync();
        return remoteVoice.Status.Ready
            ? CommandResult.Ok("遥控器语音已连接。", remoteVoice.Status)
            : CommandResult.Fail("AtvvUnavailable", remoteVoice.Status.LastError ?? remoteVoice.Status.Message);
    }

    private static async Task<CommandResult> StartRemoteVoiceTestAsync(AtvvRemoteVoiceService remoteVoice)
    {
        await remoteVoice.BeginDiagnosticTestAsync();
        return remoteVoice.Status.Ready
            ? CommandResult.Ok("语音抓包测试已开始。", remoteVoice.Status)
            : CommandResult.Fail("AtvvUnavailable", remoteVoice.Status.LastError ?? remoteVoice.Status.TestStage);
    }

    private static CommandResult StopRemoteVoiceTest(AtvvRemoteVoiceService remoteVoice)
    {
        remoteVoice.EndDiagnosticTest();
        return CommandResult.Ok("语音抓包测试已结束。", remoteVoice.Status);
    }

    public void Dispose() => Stop();
}
