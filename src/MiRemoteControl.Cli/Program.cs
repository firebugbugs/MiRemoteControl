using System.Text;
using System.Text.Json;
using MiRemoteControl.Client;
using MiRemoteControl.Contracts;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0 || args[0] is "--help" or "help")
{
    Console.WriteLine("""
MiRemoteControl — C# 核心与插件控制终端
  mrc start | status | stop | plugins
  mrc remote buttons
  mrc remote status
  mrc remote select <plugin-id>
  mrc remote driver select <remote-plugin-id>
  mrc remote press <power|up|down|left|right|ok|back|home|menu|tv|volume-up|volume-down>
  mrc voice models
  mrc voice model status
  mrc voice model select <model-file-name>
  mrc voice latest status | play | pause | toggle
  mrc invoke <plugin-id> <action-id> [--key value ...]
所有命令可加 --json；目标插件的参数和动作通过 plugins 查询后使用 invoke 调用。
remote press 会产生与真实遥控器一致的按键状态；Voice 不允许模拟。Power 会开关当前选择的工作插件。
语音模型支持 Whisper GGML 文件，以及 Qwen3-ASR/SenseVoice 的 sherpa-onnx 模型目录；使用 models 查询后按返回的 id 选择。
voice latest 控制只保留一份的最近语音录音；play 从头播放或继续，pause 暂停，toggle 供界面切换。
input 默认追加；select 选择当前选项，submit 点击确认卡片提交按钮。
start 会启动 Avalonia 框架并由它托管后台；框架退出时遥控器插件也会停止。
关闭后台使用 mrc stop；停止目标任务请调用对应插件声明的 stop 动作。
""");
    return 0;
}
var json = false;
try
{
    var tokens = args;
    var client = new CoreClient();
    CommandResult result;
    if (tokens[0] == "start")
    {
        if (tokens.Skip(1).Any(t => t != "--json")) throw new ArgumentException("start 仅支持 --json。");
        json = tokens.Skip(1).Contains("--json");
        result = await client.StartAsync();
    }
    else
    {
        string plugin, action;
        int offset;
        string? positionalKey = null;
        string? positionalValue = null;
        if (tokens[0] is "status" or "plugins" or "stop") { plugin = "core"; action = tokens[0]; offset = 1; }
        else if (tokens[0] == "remote" && tokens.Length >= 2)
        {
            plugin = "core";
            switch (tokens[1].ToLowerInvariant())
            {
                case "buttons": action = "remote.buttons"; offset = 2; break;
                case "status": action = "remote.status"; offset = 2; break;
                case "press" when tokens.Length >= 3:
                    action = "remote.press"; offset = 3; positionalKey = "button"; positionalValue = tokens[2]; break;
                case "select" when tokens.Length >= 3:
                    action = "remote.select"; offset = 3; positionalKey = "plugin"; positionalValue = tokens[2]; break;
                case "driver" when tokens.Length >= 4 && tokens[2].Equals("select", StringComparison.OrdinalIgnoreCase):
                    action = "remote.driver.select"; offset = 4; positionalKey = "plugin"; positionalValue = tokens[3]; break;
                default: throw new ArgumentException("remote 需要 status、buttons、select <plugin-id>、driver select <remote-plugin-id> 或 press <button>。");
            }
        }
        else if (tokens[0] == "voice" && tokens.Length >= 2)
        {
            plugin = "core";
            if (tokens[1].Equals("models", StringComparison.OrdinalIgnoreCase))
            {
                action = "voice.models";
                offset = 2;
            }
            else if (tokens[1].Equals("model", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 3 &&
                     tokens[2].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                action = "voice.model.status";
                offset = 3;
            }
            else if (tokens[1].Equals("model", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 4 &&
                     tokens[2].Equals("select", StringComparison.OrdinalIgnoreCase))
            {
                action = "voice.model.select";
                offset = 4;
                positionalKey = "model";
                positionalValue = tokens[3];
            }
            else if (tokens[1].Equals("latest", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 3 &&
                     tokens[2].ToLowerInvariant() is "status" or "play" or "pause" or "toggle")
            {
                action = "voice.latest." + tokens[2].ToLowerInvariant();
                offset = 3;
            }
            else throw new ArgumentException("voice 需要 models、model status、model select <model-file-name> 或 latest status/play/pause/toggle。");
        }
        else if (tokens[0] == "invoke" && tokens.Length >= 3) { plugin = tokens[1]; action = tokens[2]; offset = 3; }
        else throw new ArgumentException("未知命令。执行 mrc help 查看用法。");
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (positionalKey is not null) options[positionalKey] = positionalValue!;
        for (int i = offset; i < tokens.Length; i++)
        {
            if (!tokens[i].StartsWith("--")) throw new ArgumentException($"非法参数：{tokens[i]}");
            var key = tokens[i][2..];
            if (key == "json") { json = true; continue; }
            if (options.ContainsKey(key)) throw new ArgumentException($"重复参数：{key}");
            if (key == "replace") options[key] = "true";
            else if (++i < tokens.Length) options[key] = tokens[i];
            else throw new ArgumentException($"参数缺少值：{key}");
        }
        if (options.Remove("file", out var file))
        {
            if (options.ContainsKey("text")) throw new ArgumentException("--text 与 --file 不能同时使用。");
            options["text"] = await File.ReadAllTextAsync(file, Encoding.UTF8);
        }
        if (plugin == "core" && action is not ("remote.press" or "remote.select" or "remote.driver.select" or "voice.model.select" or "plugin.icon") && options.Count != 0)
            throw new ArgumentException("该核心命令不接受额外参数。");
        if (plugin != "core" || action == "remote.press") await client.AllowForegroundAsync();
        result = await client.InvokeAsync(plugin, action, options, timeoutMs: plugin == "core" && action == "voice.model.select" ? 300000 : 20000);
    }
    if (json) Console.WriteLine(JsonSerializer.Serialize(result, Wire.Json));
    else
    {
        Console.WriteLine($"{result.Code}: {result.Message}");
        if (result.Data is not null) Console.WriteLine(JsonSerializer.Serialize(result.Data, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
    }
    return result.Success ? 0 : 4;
}
catch (ArgumentException e) { PrintError("InvalidArgument", e.Message, json); return 2; }
catch (Exception e) when (e is TimeoutException or OperationCanceledException)
{ PrintError("Timeout", "连接/动作超时。请检查 mrc status；有副作用的动作结果未知，请勿直接重试。", json); return 6; }
catch (Exception e) { PrintError("ClientError", e.Message, json); return 3; }
static void PrintError(string code, string message, bool json)
{
    if (json) Console.WriteLine(JsonSerializer.Serialize(CommandResult.Fail(code, message), Wire.Json));
    Console.Error.WriteLine(message);
}
