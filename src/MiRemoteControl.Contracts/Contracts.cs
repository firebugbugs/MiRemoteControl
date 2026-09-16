using System.Text.Json;

namespace MiRemoteControl.Contracts;

public record CommandRequest(string Id, string Plugin, string Action, Dictionary<string, string>? Arguments = null);
public record CommandResult(bool Success, string Code, string Message, object? Data = null)
{
    public static CommandResult Ok(string message, object? data = null) => new(true, "Ok", message, data);
    public static CommandResult Fail(string code, string message) => new(false, code, message);
}
public record CommandResponse(string Id, CommandResult Result);
public record HidTapFrame(long Sequence, string DevicePath, string ReportHex);
public record ActionDescriptor(string Id, string Name, string Description);
public record InputProbeSnapshot(
    bool Available,
    string Text,
    long Revision,
    DateTimeOffset CapturedAt,
    string Source,
    int? CaretIndex = null);
public record TargetPluginStatus(
    bool Running,
    bool Focused,
    bool CanSend = false,
    bool CanStop = false,
    object? Details = null);
public static class HarnessPluginActions
{
    public const string Status = "status";
    public const string Open = "open";
    public const string Close = "close";
    public const string Input = "input";
    public const string Backspace = "backspace";
    public const string InputFocus = "input.focus";
    /// <summary>Waits until the target editor text changes and returns its complete value.</summary>
    public const string InputWatch = "input.watch";
    /// <summary>Replaces the complete target editor value, including replacing it with an empty string.</summary>
    public const string InputReplace = "input.replace";
    /// <summary>Inserts or deletes at the target editor's current caret.</summary>
    public const string InputEdit = "input.edit";
    /// <summary>Moves the editing caret without changing the input text.</summary>
    public const string CursorMove = "cursor.move";
    /// <summary>
    /// An editing mirror (TV big screen) takes over the draft. While
    /// <c>active=true</c> the target holds the draft in memory: it must not
    /// steal the foreground, must reject remote editing keys, and answers
    /// probes with the mirrored draft. <c>active=false</c> commits the draft
    /// back to the real editor and releases it.
    /// </summary>
    public const string InputMirror = "input.mirror";
    public const string Send = "send";
    public const string Stop = "stop";
    public const string InputRead = "input.read";
    public const string ConfirmUp = "confirm.up";
    public const string ConfirmDown = "confirm.down";
    public const string ConfirmSelect = "confirm.select";
    public const string ConfirmSubmit = "confirm.submit";
    public const string ConfirmStatus = "confirm.status";
    public const string Inspect = "inspect";

    /// <summary>
    /// Standard read-only target action used by mirrors and accessibility
    /// clients to obtain the target application's actual input contents.
    /// </summary>
    public const string InputProbe = "input.probe";

    public static IReadOnlyList<ActionDescriptor> DefaultActions { get; } = Array.AsReadOnly<ActionDescriptor>([
        new(Open, "打开", "启动或恢复目标应用并显示在前台"),
        new(Close, "关闭窗口", "正常关闭目标窗口，不强杀进程"),
        new(Input, "输入内容", "追加文本；replace=true 覆盖"),
        new(InputFocus, "聚焦输入框", "聚焦但不修改内容"),
        new(InputWatch, "监听输入", "等待文本变化并返回完整快照"),
        new(InputReplace, "覆盖输入", "完整替换；空文本表示全部删除"),
        new(InputEdit, "光标编辑", "光标处插入，或 delete=backward/forward 删除"),
        new(InputMirror, "镜像编辑", "active=true 由大屏接管草稿；active=false 提交并交还输入框"),
        new(InputRead, "读取输入", "读取完整输入文本"),
        new(InputProbe, "输入探针", "读取文本、单调递增版本及光标位置"),
        new(Backspace, "删除字符", "删除光标前的一个字符"),
        new(CursorMove, "移动光标", "direction=up/down/left/right"),
        new(Send, "发送", "发送当前输入内容"),
        new(Stop, "停止任务", "停止当前生成任务"),
        new(ConfirmUp, "上一个选项", "向上切换确认选项"),
        new(ConfirmDown, "下一个选项", "向下切换确认选项"),
        new(ConfirmSelect, "选择当前项", "选择当前确认项"),
        new(ConfirmSubmit, "提交确认", "提交当前确认卡片"),
        new(ConfirmStatus, "确认信息", "读取当前确认卡片和选项"),
        new(Status, "状态", "返回 TargetPluginStatus 运行状态"),
        new(Inspect, "诊断", "读取目标控件诊断信息")
    ]);
}
public static class PluginKinds
{
    public const string Target = "target";
    public const string Remote = "remote";
}
public static class PluginFolders
{
    public const string Targets = "targets";
    public const string Remotes = "remotes";
}
public record PluginDescriptor(string Id, string Name, string Version, IReadOnlyList<ActionDescriptor> Actions, string Kind = PluginKinds.Target);
public interface IHarnessPlugin : IDisposable
{
    PluginDescriptor Descriptor { get; }
    CommandResult Execute(string action, IReadOnlyDictionary<string, string> arguments);
}
public interface IAsyncHarnessPlugin
{
    Task<CommandResult> ExecuteAsync(string action, IReadOnlyDictionary<string, string> arguments, CancellationToken ct);
}
// Remote plugins are devices/services, not application-control Harnesses.
// Neither contract inherits from the other or from a common plugin interface.
public interface IRemotePlugin : IDisposable
{
    PluginDescriptor Descriptor { get; }
    CommandResult Execute(string action, IReadOnlyDictionary<string, string> arguments);
}
public interface IAsyncRemotePlugin
{
    Task<CommandResult> ExecuteAsync(string action, IReadOnlyDictionary<string, string> arguments, CancellationToken ct);
}
public interface IHostedRemotePlugin
{
    void Start(IRemotePluginHostContext context);
    void Stop();
}
public interface IRemotePluginHostContext
{
    string? SelectedTargetPluginId { get; }
    IReadOnlyList<PluginDescriptor> Plugins { get; }
    Task<CommandResult> ExecuteAsync(string pluginId, string action, Dictionary<string, string>? arguments = null,
        CancellationToken ct = default);
    void Log(string source, Exception exception);
}
public static class Wire
{
    public const int Version = 2;
    public const int MaxFrameBytes = 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string UserScope = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{Environment.UserDomainName}|{Environment.UserName}")))[..16];
    public static string PipeName => $"MiRemoteControl.{UserScope}.v{Version}";
    public static string HidTapPipeName => $"MiRemoteControl.HidTap.{UserScope}.v{Version}";

    // v1 prototype: four-byte little-endian byte length, followed by UTF-8 JSON.
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaxFrameBytes) throw new InvalidDataException("Message too large.");
        byte[] header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxFrameBytes) throw new InvalidDataException("Invalid message size.");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty message.");
    }
}
