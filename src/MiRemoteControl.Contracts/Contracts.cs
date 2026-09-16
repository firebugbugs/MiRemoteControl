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
public static class TargetPluginActions
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
    public const string Send = "send";
    public const string Stop = "stop";

    /// <summary>
    /// Standard read-only target action used by mirrors and accessibility
    /// clients to obtain the target application's actual input contents.
    /// </summary>
    public const string InputProbe = "input.probe";
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
public interface IHostedHarnessPlugin
{
    void Start(IPluginHostContext context);
    void Stop();
}
public interface IPluginHostContext
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
