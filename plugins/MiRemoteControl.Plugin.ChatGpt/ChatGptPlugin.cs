using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using MiRemoteControl.Contracts;

namespace MiRemoteControl.Plugin.ChatGpt;

/// <summary>
/// Harness plugin (<c>kind=target</c>) for the ChatGPT desktop app.
/// </summary>
/// <remarks>
/// The app is an Electron/Chromium shell whose composer is a ProseMirror
/// contenteditable. The implementation therefore follows the same rules as the
/// ZCode harness: locate controls by accessible properties instead of
/// coordinates, read the real value back after every write, publish monotonic
/// probe revisions, and never touch the focused editor while a mirror owns the
/// draft.
/// </remarks>
public sealed class ChatGptPlugin : IHarnessPlugin, IAsyncHarnessPlugin
{
    // composer 在不同界面语言下暴露的可访问名（即 placeholder）。
    private static readonly string[] ComposerNames =
    [
        "随心输入", "输入消息", "发送消息", "询问 ChatGPT", "有什么可以帮忙的",
        "Ask anything", "Ask ChatGPT", "Message ChatGPT", "Send a message"
    ];
    // 名称都不匹配时，仍可按该应用实际使用的编辑器类名定位输入框。
    private const string ComposerClassName = "ProseMirror";
    private static readonly string[] SendButtonNames = ["发送", "Send", "发送消息", "Send message", "提交", "Submit"];
    private static readonly string[] StopButtonNames = ["停止", "Stop", "停止生成", "Stop generating", "停止响应", "Stop response"];
    private static readonly string[] ConfirmationListNames = ["需要权限", "Permission required", "需要批准", "Permission request"];
    private static readonly string[] ConfirmationHints = ["使用 Tab / 上下键选择", "Use Tab / arrow keys to choose"];
    private static readonly string[] ConfirmationButtonNames = ["确认", "Confirm", "提交", "Submit", "继续", "Continue", "批准", "Approve", "允许", "Allow"];
    private const int MaxTextLength = 20000;

    // ChatGPT 桌面版有两种分发：MSIX 包（WindowsApps 下带版本号的目录）
    // 与普通安装包。两者都要能识别，且只按完整进程路径判断，不按进程名。
    private const string MsixDirectoryPrefix = "OpenAI.Codex_";
    private static readonly string MsixRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
    private static readonly string MsixExecutablePrefix = Path.Combine(MsixRoot, MsixDirectoryPrefix);
    private static readonly string[] InstalledExecutables =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ChatGPT", "ChatGPT.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ChatGPT", "ChatGPT.exe")
    ];
    private static readonly IReadOnlyDictionary<string, string> NoArguments = new Dictionary<string, string>();

    private static readonly Dictionary<string, string[]> AllowedArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["open"] = ["exe", "window"],
        ["close"] = ["window"],
        ["status"] = ["window"],
        ["inspect"] = ["window"],
        ["input"] = ["text", "replace"],
        ["input.focus"] = [],
        ["input.read"] = [],
        ["input.probe"] = [],
        ["input.watch"] = ["afterRevision", "timeoutMs"],
        ["input.replace"] = ["text", "caret", "caretOnly"],
        ["input.edit"] = ["text", "delete"],
        ["input.mirror"] = ["active", "text", "caret"],
        ["backspace"] = [],
        ["cursor.move"] = ["direction"],
        ["send"] = [],
        ["stop"] = [],
        ["confirm.up"] = [],
        ["confirm.down"] = [],
        ["confirm.select"] = [],
        ["confirm.submit"] = [],
        ["confirm.status"] = []
    };

    private readonly object _inputProbeSync = new();

    /// <summary>
    /// Serializes every UI Automation call in this plugin. Chromium's provider is
    /// not safe under concurrent queries: when the desktop's periodic status poll
    /// and the probe watcher read the same tree while an action was running, it
    /// answered UIA_E_ELEMENTNOTAVAILABLE (0x80040201) and status failed as a
    /// whole. One lock per provider call is cheap next to that.
    /// </summary>
    private static readonly object AutomationSync = new();

    /// <summary>
    /// When the last send was dispatched. The desktop app's big screen and the
    /// remote plugin both answer the confirm key, and the app needs a moment
    /// before it renders its stop button; without this the plugin treated that
    /// gap as "nothing is running" and a repeated confirm press sent the same
    /// draft again.
    /// </summary>
    private DateTimeOffset _lastSendAt = DateTimeOffset.MinValue;

    private static readonly TimeSpan SendDebounce = TimeSpan.FromSeconds(6);
    private string? _mirrorText;
    private int? _mirrorCaret;
    private string? _lastProbedText;
    private int? _lastProbedCaretIndex;
    private string _lastProbeSource = "uia";
    private string? _lastVerifiedInputText;
    private string _lastVerifiedInputSource = "write-verification";
    private readonly HashSet<ulong> _supersededProbeHashes = [];
    private long _inputProbeRevision;
    private int _inputWriteInProgress;
    private CancellationTokenSource? _inputWatcherCancellation;
    private Task? _inputWatcherTask;
    private TaskCompletionSource<long> _inputChanged = NewInputChangedSignal();

    public PluginDescriptor Descriptor { get; } = new("mrc.chatgpt", "ChatGPT 控制", "0.1.0",
        HarnessPluginActions.DefaultActions, PluginKinds.Target);

    public async Task<CommandResult> ExecuteAsync(
        string action,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken ct)
    {
        if (action == HarnessPluginActions.InputWatch)
            return await WatchInputAsync(arguments, ct);
        return await Task.Run(() => Execute(action, arguments), ct);
    }

    public CommandResult Execute(string action, IReadOnlyDictionary<string, string> arguments)
    {
        try
        {
            if (!Descriptor.Actions.Any(item => item.Id == action))
                return CommandResult.Fail("UnknownAction", $"未知 ChatGPT 操作：{action}");
            var allowed = AllowedArguments.TryGetValue(action, out var keys) ? keys : [];
            var unknown = arguments.Keys.FirstOrDefault(key => !allowed.Contains(key, StringComparer.OrdinalIgnoreCase));
            if (unknown is not null)
                return CommandResult.Fail("InvalidArgument", $"未知参数：{unknown}");
            if (action == HarnessPluginActions.InputWatch)
                return CommandResult.Fail("AsyncRequired", "input.watch 必须通过异步插件接口调用。");

            var explicitExecutable = ResolveExplicitExecutable(arguments);
            var isTarget = BuildMatcher(explicitExecutable);
            var windows = NativeWindow.Find(isTarget);
            if (action == "open") return Open(explicitExecutable, isTarget, windows, arguments);
            if (action == "status" && windows.Count == 0)
                return CommandResult.Ok("ChatGPT 未打开。", new TargetPluginStatus(false, false, Details: new { windows }));

            // Several ChatGPT windows may coexist (the main window plus a quick
            // chat window) while the remote's fullscreen mirror owns the
            // foreground, so composer actions must not depend on foreground
            // disambiguation. Pick the window that actually exposes the
            // composer, largest first.
            var target = action == "close"
                ? NativeWindow.Select(windows, arguments)
                : SelectComposerWindow(windows, arguments);
            if (action == "close")
            {
                NativeWindow.Close(target);
                return CommandResult.Ok("已发送标题栏关闭请求；若应用驻留托盘或需要确认，将遵循应用自身行为。", new { state = "CloseRequested" });
            }

            var root = TryRoot(target.Handle);
            if (root is null)
                return CommandResult.Fail("TargetNotReadable", $"无法读取目标窗口的 UIA 根元素（HWND 0x{target.Handle.ToInt64():X}），请稍后重试。");
            if (action == "input.read")
            {
                if (TryReadMirror(out var mirrorRead)) return mirrorRead;
                var composer = FindComposerFast(root);
                if (composer is null)
                    return CommandResult.Fail("ComposerNotFound", "当前页面没有可读取的输入框。");
                return CommandResult.Ok("已读取输入框内容。", new { text = ReadProbeText(composer) });
            }
            if (action == HarnessPluginActions.InputProbe)
            {
                if (TryReadMirror(out var mirrorProbe)) return mirrorProbe;
                // Do not query Chromium's accessibility provider while the same
                // contenteditable is processing a write: those concurrent reads
                // made the document range settle one edit late.
                if (TryReadProbeDuringWrite(out var inFlight)) return inFlight;
                var composer = FindComposerFast(root);
                if (composer is null)
                    return CommandResult.Fail("InputProbeUnavailable", "当前页面没有可探测的输入框。");
                var text = ReadProbeText(composer);
                return PublishInputProbe(text, caretIndex: ReadCaretIndex(composer, text));
            }
            if (action == HarnessPluginActions.InputFocus)
            {
                if (IsMirrorActive()) return MirrorOwnsInput("聚焦真实输入框");
                var composer = FindComposerFast(root);
                if (composer is null)
                    return CommandResult.Fail("ComposerNotFound", "当前页面没有可聚焦的输入框。");
                FocusComposerStable(target, composer, 2);
                return CommandResult.Ok("输入框已聚焦。", new { state = "Focused" });
            }
            if (action == HarnessPluginActions.InputMirror)
            {
                BeginInputWrite();
                try { return SetMirrorMode(target, root, arguments); }
                finally { EndInputWrite(); }
            }
            if (action == HarnessPluginActions.InputReplace)
            {
                // While a mirror owns the draft this is a pure in-memory
                // update: the real editor must not be touched.
                if (IsMirrorActive()) return ReplaceMirrorDraft(target, root, arguments);
                BeginInputWrite();
                try { return ReplaceInput(target, root, arguments); }
                finally { EndInputWrite(); }
            }
            if (action == HarnessPluginActions.InputEdit)
            {
                if (IsMirrorActive()) return MirrorOwnsInput("光标编辑");
                BeginInputWrite();
                try { return EditInputAtCaret(target, root, arguments); }
                finally { EndInputWrite(); }
            }
            if (action == "backspace")
            {
                if (IsMirrorActive()) return MirrorOwnsInput("删除");
                // Key-repeat traffic uses the last complete probe snapshot as
                // its deterministic baseline: Chromium performs the edit while
                // occluded but keeps exposing the longer pre-delete value until
                // the fullscreen mirror closes.
                var (before, caretIndex) = ReadCachedProbe();
                if (before is null)
                {
                    var composer = FindComposerFast(root);
                    if (composer is null)
                        return CommandResult.Fail("ComposerNotFound", "当前页面没有可读取的输入框。");
                    before = ReadProbeText(composer);
                    caretIndex = ReadCaretIndex(composer, before);
                }
                if (!NativeWindow.IsForeground(target)) NativeWindow.Focus(target);
                NativeWindow.Key(target, 0x08);
                var expected = RemoveTextElementBefore(before, caretIndex ?? before.Length, out var expectedCaret);
                PublishInputProbe(expected, authoritative: true, sourceOverride: "backspace-dispatch", caretIndex: expectedCaret);
                return CommandResult.Ok("已删除输入框中的一个字符。", new { state = "Dispatched", text = expected });
            }
            if (action == HarnessPluginActions.CursorMove)
            {
                if (IsMirrorActive()) return MirrorOwnsInput("光标移动");
                if (!arguments.TryGetValue("direction", out var direction) || !TryGetCursorKey(direction, out var key))
                    return CommandResult.Fail("InvalidArgument", "cursor.move 需要 direction=up/down/left/right。");
                var composer = FindComposerFast(root);
                if (composer is null)
                    return CommandResult.Fail("ComposerNotFound", "当前页面没有可控制的输入框。");
                FocusComposerStable(target, composer, 1);
                NativeWindow.Key(target, key);
                return CommandResult.Ok("已移动输入框光标。", new { direction = direction.ToLowerInvariant() });
            }
            if (action == "input")
            {
                // Voice traffic lands in the mirror draft while the big screen
                // owns editing, so it never needs the composer's focus.
                if (IsMirrorActive()) return AppendMirrorDraft(target, root, arguments);
                BeginInputWrite();
                try
                {
                    var composer = FindComposerRequired(root);
                    NativeWindow.Focus(target);
                    return Input(target, composer, arguments);
                }
                finally { EndInputWrite(); }
            }
            if (action == HarnessPluginActions.Send && IsMirrorActive())
            {
                // The mirror owns the draft and the real editor is neither
                // focused nor written while it is active, so sending from here
                // would race the mirror consumer. The big screen leaves first
                // (committing the complete draft) and only then asks to send;
                // a remote confirm key must not dispatch a second, stale send.
                return MirrorOwnsInput("发送");
            }

            // Everything from here on is answered with narrow, type-scoped UIA
            // queries instead of one whole-tree walk. A descendant walk filtered
            // by IsOffscreen is both slow and provider-fragile: in the host
            // process Chromium could throw while enumerating it, which failed
            // status/inspect as a whole. Because the remote's confirm key reads
            // canStop out of status, that single failure silently turned every
            // "stop" press into a "send".
            var stopButtons = FindButtons(root, StopButtonNames);
            var sendButtons = FindButtons(root, SendButtonNames);
            // A send that just happened means a task is already running: the app
            // only renders its stop button a moment later.
            var running = stopButtons.Any(IsEnabled) || RecentlySent();
            if (action == "inspect") return CommandResult.Ok("UIA 诊断", Inspect(root));
            if (action == "status") return CommandResult.Ok("ChatGPT 窗口已连接", new TargetPluginStatus(
                Running: true,
                Focused: windows.Any(NativeWindow.IsForeground),
                CanSend: sendButtons.Any(IsEnabled),
                CanStop: running,
                Details: new
                {
                    windows = windows.Select(w => new { handle = w.Handle.ToInt64(), w.ProcessId, w.Title }),
                    composer = FindComposerFast(root) is not null,
                    confirmation = FindConfirmation(root) is not null,
                    sendButtons = sendButtons.Count,
                    stopButtons = stopButtons.Count
                }));
            NativeWindow.Focus(target);
            if (action.StartsWith("confirm.", StringComparison.Ordinal)) return Confirm(action, target, root);
            if (action != "stop" && action != "backspace" && FindConfirmation(root) is not null)
                return CommandResult.Fail("ConfirmationPending", "当前存在确认卡片，请使用 confirm 命令处理，避免操作被遮挡的输入区域。");
            switch (action)
            {
                case "send": return Send(target, sendButtons);
                case "stop": return Stop(target, root);
                default: return CommandResult.Fail("UnknownAction", action);
            }
        }
        catch (ControlException e) { return CommandResult.Fail(e.Code, e.Message); }
        catch (ElementNotAvailableException) { return CommandResult.Fail("TargetChanged", "页面控件已变化，请重新读取状态。"); }
        catch (Exception e)
        {
            // Keep the failure diagnosable: an empty message made this whole
            // class of UIA failures indistinguishable from a real answer.
            return CommandResult.Fail("AutomationFailed",
                $"{e.GetType().Name} 0x{e.HResult:X8}：{e.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Send and stop
    // ------------------------------------------------------------------

    /// <summary>
    /// Dispatches the target application's own send action. The desktop app
    /// exposes a send button while a draft exists; when that button is not in
    /// the accessibility tree yet, the app's own submit key (Enter) is used —
    /// Shift+Enter stays a newline, so this is never an unconditional Enter.
    /// </summary>
    private CommandResult Send(WindowTarget target, List<AutomationElement> sendButtons)
    {
        if (RecentlySent())
            return CommandResult.Fail("SendDebounced", "刚刚已经发送过一次，已忽略这次重复的发送请求。");
        var candidates = sendButtons.Where(IsEnabled).ToArray();
        if (candidates.Length > 1)
            throw new ControlException("AmbiguousControl", "找到多个可用的发送按钮，无法安全选择。");
        NativeWindow.RequireForeground(target);
        if (candidates.Length == 1) InvokeElement(candidates[0], "发送按钮");
        else NativeWindow.Key(target, 0x0D);
        PublishInputProbe(string.Empty, authoritative: true, sourceOverride: "send");
        lock (_inputProbeSync) _lastSendAt = DateTimeOffset.UtcNow;
        return CommandResult.Ok("已调用 ChatGPT 的发送动作。",
            new { state = "Dispatched", via = candidates.Length == 1 ? "button" : "submit-key" });
    }

    /// <summary>
    /// True while a send is recent enough that the app may still be starting its
    /// response. In that window no stop button exists yet although the task is
    /// already running, so another confirm press must not send again.
    /// </summary>
    private bool RecentlySent()
    {
        lock (_inputProbeSync) return DateTimeOffset.UtcNow - _lastSendAt < SendDebounce;
    }

    /// <summary>
    /// Stops the running task through the app's own stop button. The desktop app
    /// publishes that button (accessible name "停止" / "Stop") only while a
    /// response is streaming — measured on the live app — so its presence is
    /// exactly what status must report as canStop. Nothing is invented here: when
    /// the button is absent the plugin waits briefly and then reports that there
    /// was nothing to stop, instead of pressing keys that could disturb the
    /// composer.
    /// </summary>
    private static CommandResult Stop(WindowTarget target, AutomationElement root)
    {
        var candidates = FindButtons(root, StopButtonNames).Where(IsEnabled).ToArray();
        if (candidates.Length > 1)
            throw new ControlException("AmbiguousControl", "找到多个可用的停止按钮，无法安全选择。");
        for (var attempt = 0; candidates.Length == 0 && attempt < 6; attempt++)
        {
            Thread.Sleep(200);
            candidates = FindButtons(root, StopButtonNames).Where(IsEnabled).ToArray();
        }
        if (candidates.Length == 0)
            return CommandResult.Ok("当前没有需要停止的任务。", new { state = "Idle" });
        NativeWindow.RequireForeground(target);
        InvokeElement(candidates[0], "停止按钮");
        return CommandResult.Ok("已调用 ChatGPT 的停止按钮。", new { state = "StopRequested", via = "button" });
    }

    // ------------------------------------------------------------------
    // Target identity, launch and focus
    // ------------------------------------------------------------------

    private static string? ResolveExplicitExecutable(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("exe", out var path)) return null;
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new ControlException("AppNotInstalled", $"指定的可执行文件不存在：{full}");
        return full;
    }

    private static Func<string, bool> BuildMatcher(string? explicitExecutable) =>
        explicitExecutable is null
            ? IsChatGptExecutable
            : path => string.Equals(Path.GetFullPath(path), explicitExecutable, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Accepts only the real ChatGPT desktop executables: the MSIX package
    /// payload (a version-stamped directory under WindowsApps) or one of the
    /// known installer locations. A same-named process elsewhere is rejected so
    /// the plugin can never drive an unrelated window.
    /// </summary>
    private static bool IsChatGptExecutable(string path)
    {
        if (!string.Equals(Path.GetFileName(path), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWith(MsixExecutablePrefix, StringComparison.OrdinalIgnoreCase)) return true;
        return InstalledExecutables.Any(installed => string.Equals(path, installed, StringComparison.OrdinalIgnoreCase));
    }

    private static CommandResult Open(
        string? explicitExecutable,
        Func<string, bool> isTarget,
        List<WindowTarget> windows,
        IReadOnlyDictionary<string, string> args)
    {
        if (windows.Count == 0)
        {
            StartTarget(explicitExecutable);
            var clock = Stopwatch.StartNew();
            while (windows.Count == 0 && clock.ElapsedMilliseconds < 20000)
            {
                Thread.Sleep(150);
                windows = NativeWindow.Find(isTarget);
            }
            if (windows.Count == 0)
                throw new ControlException("WindowNotFound", "已尝试启动 ChatGPT，但没有出现可见窗口。");
        }
        // Electron may finish restoring/can replace the HWND shortly after it
        // first appears. Re-enumerate and re-activate until the target stays
        // genuinely in the foreground, not just on the taskbar.
        ControlException? failure = null;
        WindowTarget? activated = null;
        for (var attempt = 0; attempt < 5 && (activated is null || !NativeWindow.IsForeground(activated)); attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    Thread.Sleep(150);
                    windows = NativeWindow.Find(isTarget);
                }
                var candidate = NativeWindow.Select(windows, args);
                NativeWindow.Focus(candidate, promoteToTopmost: true);
                Thread.Sleep(200); // let Chromium settle, then confirm the foreground
                activated = candidate;
            }
            catch (ControlException e) { failure = e; }
        }
        if (activated is not null && NativeWindow.IsForeground(activated))
            return CommandResult.Ok("ChatGPT 已在前台显示。", new { handle = activated.Handle.ToInt64(), activated.ProcessId });
        throw failure ?? new ControlException("FocusDenied", "Windows 未允许切到 ChatGPT 前台。");
    }

    private static void StartTarget(string? explicitExecutable)
    {
        if (explicitExecutable is not null)
        {
            // An executable inside an MSIX payload cannot be started directly;
            // activate the packaged app instead.
            if (explicitExecutable.StartsWith(MsixExecutablePrefix, StringComparison.OrdinalIgnoreCase) &&
                FindMsixApplicationUserModelId() is { } packagedId)
            {
                ActivatePackagedApp(packagedId);
                return;
            }
            Process.Start(new ProcessStartInfo(explicitExecutable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(explicitExecutable)!
            });
            return;
        }
        if (FindMsixApplicationUserModelId() is { } applicationUserModelId)
        {
            ActivatePackagedApp(applicationUserModelId);
            return;
        }
        var installed = InstalledExecutables.FirstOrDefault(File.Exists);
        if (installed is not null)
        {
            Process.Start(new ProcessStartInfo(installed)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(installed)!
            });
            return;
        }
        throw new ControlException("AppNotInstalled", "未找到 ChatGPT 桌面版，请使用 --exe 指定 ChatGPT.exe 的路径。");
    }

    private static void ActivatePackagedApp(string applicationUserModelId) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{applicationUserModelId}")
        {
            UseShellExecute = false
        });

    /// <summary>
    /// Derives the MSIX application user model id from the package directory
    /// name (<c>OpenAI.Codex_&lt;version&gt;_x64__&lt;publisherHash&gt;</c>), i.e.
    /// <c>OpenAI.Codex_&lt;publisherHash&gt;!App</c>. The manifest of the shipped
    /// package declares exactly one application with the id <c>App</c>.
    /// </summary>
    private static string? FindMsixApplicationUserModelId()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(MsixRoot, MsixDirectoryPrefix + "*"))
            {
                var segments = Path.GetFileName(directory).Split('_');
                if (segments.Length < 2) continue;
                var familyName = $"{segments[0]}_{segments[^1]}";
                if (!Directory.Exists(Path.Combine(directory, "app"))) continue;
                return $"{familyName}!App";
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or ArgumentException or DirectoryNotFoundException)
        {
            // WindowsApps is ACL-restricted for regular users; the running
            // process path is then the only source of truth.
        }
        return null;
    }

    private static bool TryGetCursorKey(string direction, out ushort key)
    {
        key = direction.Trim().ToLowerInvariant() switch
        {
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            _ => 0
        };
        return key != 0;
    }

    // ------------------------------------------------------------------
    // Input writes
    // ------------------------------------------------------------------

    private CommandResult Input(WindowTarget target, AutomationElement composer, IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("text", out var text)) throw new ControlException("InvalidArgument", "input 需要 --text 或 --file。");
        if (text.Length > MaxTextLength || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r')))
            throw new ControlException("InvalidText", "文本上限 20000 字符，且不允许换行以外的控制字符。");
        var replace = args.TryGetValue("replace", out var replaceValue) && bool.Parse(replaceValue);
        void CheckFocus()
        {
            if (!Automation.Compare(AutomationElement.FocusedElement, composer))
                throw new ControlException("EditorFocusLost", "输入框焦点已改变，停止输入。");
        }
        // An empty contenteditable creates its first editable text node after
        // receiving focus. During that transition the window can briefly lose
        // foreground ownership, so require a sustained focus before the first
        // empty write instead of a single fixed delay.
        var initiallyEmpty = ReadProbeText(composer).Length == 0;
        FocusComposerStable(target, composer, initiallyEmpty ? 4 : 1);
        var before = ReadProbeText(composer);
        for (var focusAttempt = 0; ; focusAttempt++)
        {
            try
            {
                // Voice input must land where the caret already is: only the
                // replace path selects the whole value (Ctrl+A); the append
                // path leaves the caret untouched instead of jumping to the end.
                if (replace) NativeWindow.Key(target, 0x41, 0x11); // Ctrl+A
                break;
            }
            catch (ControlException exception) when (
                exception.Code == "TargetChanged" && focusAttempt < 2)
            {
                // Cursor movement has no content side effect, so it is safe to
                // re-establish focus and retry before any text is dispatched.
                FocusComposerStable(target, composer, initiallyEmpty ? 4 : 2);
            }
        }
        Thread.Sleep(30); // allow Ctrl key-up before Unicode input checks modifiers
        if (replace && text.Length == 0) NativeWindow.Key(target, 0x08);
        else if (text.Length > 0 && (replace || initiallyEmpty)) NativeWindow.PasteText(target, text, CheckFocus);
        else NativeWindow.Text(target, text, CheckFocus);

        var normalized = Normalize(text);
        var expected = replace ? text : before + text;
        string? actual = null;
        var verified = false;
        // Chromium may expose the new rendered draft a little later than the
        // key dispatch, so give the provider a short settling window.
        for (var attempt = 0; attempt < 6 && !verified; attempt++)
        {
            Thread.Sleep(attempt == 0 ? 100 : 50);
            CheckFocus();
            actual = ReadProbeText(composer);
            verified = replace
                ? Normalize(actual) == normalized
                : Normalize(actual).EndsWith(normalized, StringComparison.Ordinal);
        }
        if (!verified)
        {
            // Chromium throttles the accessibility tree while ChatGPT is fully
            // occluded by the TV mirror: SendInput still reaches the focused
            // composer, but UIA keeps returning the exact pre-write frame.
            // Since every key was accepted and focus stayed on this composer,
            // the deterministic post-write value is safe to publish.
            if (actual is not null && Normalize(actual) == Normalize(before))
            {
                PublishInputProbe(expected, authoritative: true, sourceOverride: "write-dispatch");
                return CommandResult.Ok(
                    "内容已完整发送；目标 UIA 暂未刷新，已按写入结果更新探针。",
                    new { characters = text.Length, verified = false, inferred = true });
            }
            return CommandResult.Fail("InputUnverified", "已尝试输入，但回读结果既不是写入前内容，也无法确认完整新文本。请检查当前页面，勿直接重复输入。");
        }
        PublishInputProbe(actual!, authoritative: true);
        return CommandResult.Ok("内容已输入并回读核验，尚未发送。", new { characters = text.Length, verified });
    }

    private CommandResult ReplaceInput(
        WindowTarget target,
        AutomationElement root,
        IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("text", out var text))
            return CommandResult.Fail("InvalidArgument", "input.replace 需要 text；传入空字符串会清空输入框。");
        if (text.Length > MaxTextLength || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            return CommandResult.Fail("InvalidText", "文本上限 20000 字符，且不允许制表、换行以外的控制字符。");

        var composer = FindComposerFast(root);
        if (composer is null)
            return CommandResult.Fail("ComposerNotFound", "当前页面没有可覆盖写入的输入框。");

        var write = Input(target, composer, new Dictionary<string, string>
        {
            ["text"] = text,
            ["replace"] = "true"
        });
        if (!write.Success) return write;
        lock (_inputProbeSync)
        {
            var snapshot = new InputProbeSnapshot(
                true,
                _lastProbedText ?? text,
                _inputProbeRevision,
                DateTimeOffset.UtcNow,
                "replace",
                _lastProbedCaretIndex ?? text.Length);
            return CommandResult.Ok("已覆盖输入框的完整内容。", snapshot);
        }
    }

    private CommandResult EditInputAtCaret(
        WindowTarget target,
        AutomationElement root,
        IReadOnlyDictionary<string, string> args)
    {
        var text = args.TryGetValue("text", out var value) ? value : string.Empty;
        var delete = args.TryGetValue("delete", out var deleteValue)
            ? deleteValue.Trim().ToLowerInvariant()
            : "none";
        if (delete is not ("none" or "backward" or "forward"))
            return CommandResult.Fail("InvalidArgument", "input.edit 的 delete 只能是 none、backward 或 forward。");
        if (text.Length == 0 && delete == "none")
            return CommandResult.Fail("InvalidArgument", "input.edit 至少需要 text，或指定 delete=backward/forward。");
        if (text.Length > MaxTextLength || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            return CommandResult.Fail("InvalidText", "文本上限 20000 字符，且不允许制表、换行以外的控制字符。");

        var composer = FindComposerFast(root);
        if (composer is null)
            return CommandResult.Fail("ComposerNotFound", "当前页面没有可编辑的输入框。");
        FocusComposerStable(target, composer, 1);
        void CheckFocus()
        {
            if (!Automation.Compare(AutomationElement.FocusedElement, composer))
                throw new ControlException("EditorFocusLost", "输入框焦点已改变，停止编辑。");
        }
        if (delete == "backward") NativeWindow.Key(target, 0x08);
        else if (delete == "forward") NativeWindow.Key(target, 0x2E);
        if (text.Length > 0) NativeWindow.Text(target, text, CheckFocus);

        var actual = string.Empty;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            Thread.Sleep(attempt == 0 ? 100 : 50);
            CheckFocus();
            actual = ReadProbeText(composer);
            if (attempt > 0 || text.Length == 0 || actual.Contains(text, StringComparison.Ordinal)) break;
        }
        var result = PublishInputProbe(actual, authoritative: true, sourceOverride: "caret-edit",
            caretIndex: ReadCaretIndex(composer, actual));
        return CommandResult.Ok("已在当前光标位置完成编辑。", result.Data);
    }

    // ------------------------------------------------------------------
    // input.watch (long polling)
    // ------------------------------------------------------------------

    private async Task<CommandResult> WatchInputAsync(
        IReadOnlyDictionary<string, string> args,
        CancellationToken ct)
    {
        if (args.TryGetValue("afterRevision", out var revisionValue) &&
            !long.TryParse(revisionValue, out _))
            return CommandResult.Fail("InvalidArgument", "afterRevision 必须是整数。");
        var afterRevision = args.TryGetValue("afterRevision", out revisionValue)
            ? long.Parse(revisionValue)
            : -1;
        var timeoutMs = 20000;
        if (args.TryGetValue("timeoutMs", out var timeoutValue) &&
            (!int.TryParse(timeoutValue, out timeoutMs) || timeoutMs is < 100 or > 30000))
            return CommandResult.Fail("InvalidArgument", "timeoutMs 必须在 100 到 30000 之间。");

        EnsureInputWatcherStarted();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        while (true)
        {
            Task<long> changed;
            lock (_inputProbeSync)
            {
                if (_lastProbedText is not null && _inputProbeRevision > afterRevision)
                    return CurrentProbeResult("输入框文本已更新。");
                changed = _inputChanged.Task;
            }
            try { await changed.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lock (_inputProbeSync)
                    return _lastProbedText is null
                        ? CommandResult.Fail("InputProbeUnavailable", "当前页面没有可监听的输入框。")
                        : CurrentProbeResult("输入框内容没有变化。");
            }
        }
    }

    private void EnsureInputWatcherStarted()
    {
        lock (_inputProbeSync)
        {
            if (_inputWatcherTask is { IsCompleted: false }) return;
            _inputWatcherCancellation?.Dispose();
            _inputWatcherCancellation = new CancellationTokenSource();
            _inputWatcherTask = Task.Run(() => RunInputWatcherAsync(_inputWatcherCancellation.Token));
        }
    }

    private async Task RunInputWatcherAsync(CancellationToken ct)
    {
        // This observer must keep sampling even when nothing is waiting: it is
        // what wakes the long poll the moment the draft changes. Letting it exit
        // on idle made every input.watch wait for its full timeout, and because
        // the host serializes target calls, the big screen's commit queued behind
        // that wait and the confirm key appeared to do nothing at all.
        while (!ct.IsCancellationRequested)
        {
            var canRead = false;
            lock (_inputProbeSync) canRead = _inputWriteInProgress == 0 && _mirrorText is null;
            if (canRead)
            {
                try
                {
                    var windows = NativeWindow.Find(IsChatGptExecutable);
                    var target = SelectComposerWindow(windows, NoArguments);
                    var composer = TryRoot(target.Handle) is { } watchRoot ? FindComposerFast(watchRoot) : null;
                    if (composer is not null)
                    {
                        var text = ReadProbeText(composer);
                        PublishInputProbe(text, caretIndex: ReadCaretIndex(composer, text));
                    }
                }
                catch
                {
                    // The observer remains alive while ChatGPT starts, closes, or
                    // replaces its Electron window. The next sample retries.
                }
            }
            try { await Task.Delay(80, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private CommandResult CurrentProbeResult(string message)
    {
        var snapshot = new InputProbeSnapshot(
            true,
            _lastProbedText ?? string.Empty,
            _inputProbeRevision,
            DateTimeOffset.UtcNow,
            // Report the source of the last publish instead of a generic
            // "watch": the big screen needs to know whether the caret belongs
            // to an insertion (voice) or to its own navigation.
            _lastProbeSource,
            _lastProbedCaretIndex);
        return CommandResult.Ok(message, snapshot);
    }

    // ------------------------------------------------------------------
    // Probe publication
    // ------------------------------------------------------------------

    private static TaskCompletionSource<long> NewInputChangedSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void FocusComposerStable(WindowTarget target, AutomationElement composer, int stableSamples)
    {
        lock (AutomationSync)
        {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            NativeWindow.Focus(target);
            composer.SetFocus();
            var stable = true;
            for (var sample = 0; sample < stableSamples; sample++)
            {
                Thread.Sleep(60);
                if (!NativeWindow.IsForeground(target) ||
                    !Automation.Compare(AutomationElement.FocusedElement, composer))
                {
                    stable = false;
                    break;
                }
            }
            if (stable) return;
        }
        throw new ControlException("EditorFocusLost", "无法稳定聚焦 ChatGPT 输入框，已取消输入。");
        }
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

    /// <summary>
    /// Reads the composer's actual draft. ProseMirror exposes the placeholder as
    /// the accessible value of an empty editor, prefixed by one provider-owned
    /// newline; that sentinel must never be published as real draft text.
    /// </summary>
    private static string ReadProbeText(AutomationElement composer)
    {
        var text = (ReadText(composer) ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var placeholder = composer.Current.Name;
        if (placeholder.Length > 0 && IsComposerPlaceholder(placeholder) && text == "\n" + placeholder)
            return string.Empty;
        return text.EndsWith('\n') ? text[..^1] : text;
    }

    private static bool IsComposerPlaceholder(string name) =>
        ComposerNames.Any(candidate => candidate.StartsWith(name, StringComparison.OrdinalIgnoreCase));

    private static int? ReadCaretIndex(AutomationElement composer, string text)
    {
        lock (AutomationSync)
        {
        try
        {
            if (!composer.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)) return null;
            var pattern = (TextPattern)patternObject;
            var selection = pattern.GetSelection();
            if (selection.Length == 0) return null;
            var document = pattern.DocumentRange;
            if (selection[0].CompareEndpoints(
                    TextPatternRangeEndpoint.End,
                    document,
                    TextPatternRangeEndpoint.End) == 0)
                return text.Length;

            // Build a temporary range from the start of the document to the
            // active selection end. Its text length is the UTF-16 index used by
            // the mirror and by string insertion.
            var prefix = document.Clone();
            prefix.MoveEndpointByRange(
                TextPatternRangeEndpoint.End,
                selection[0],
                TextPatternRangeEndpoint.End);
            var prefixText = prefix.GetText(-1)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            return Math.Clamp(prefixText.Length, 0, text.Length);
        }
        catch
        {
            // Caret metadata is optional. A provider-specific selection error
            // must never suppress an otherwise valid text snapshot.
            return null;
        }
        }
    }

    private CommandResult PublishInputProbe(
        string text,
        bool authoritative = false,
        string? sourceOverride = null,
        int? caretIndex = null)
    {
        InputProbeSnapshot snapshot;
        TaskCompletionSource<long>? changedSignal = null;
        lock (_inputProbeSync)
        {
            var source = "uia";
            if (authoritative)
            {
                if (_lastProbedText is not null &&
                    !string.Equals(_lastProbedText, text, StringComparison.Ordinal))
                    _supersededProbeHashes.Add(ProbeFingerprint(_lastProbedText));
                _lastVerifiedInputText = text;
                source = sourceOverride ?? "write-verification";
                _lastVerifiedInputSource = source;
                caretIndex ??= text.Length;
            }
            else if (_lastVerifiedInputText is { } verified)
            {
                if (string.Equals(text, verified, StringComparison.Ordinal))
                {
                    // UIA has caught up with the last verified write.
                    _lastVerifiedInputText = null;
                    _supersededProbeHashes.Clear();
                }
                else if (_supersededProbeHashes.Contains(ProbeFingerprint(text)))
                {
                    // This is an exact snapshot superseded by a deterministic
                    // append, delete or clear action. Keep the newer value;
                    // comparing by length/prefix would break deletions.
                    text = verified;
                    source = _lastVerifiedInputSource;
                    caretIndex = _lastProbedCaretIndex ?? verified.Length;
                }
                else
                {
                    // A divergent value is a genuine subsequent edit, including
                    // typing performed outside this plugin.
                    _lastVerifiedInputText = null;
                    _supersededProbeHashes.Clear();
                }
            }
            int? normalizedCaret = caretIndex is null ? null : Math.Clamp(caretIndex.Value, 0, text.Length);
            if (!string.Equals(_lastProbedText, text, StringComparison.Ordinal))
            {
                _lastProbedText = text;
                _inputProbeRevision++;
                changedSignal = _inputChanged;
                _inputChanged = NewInputChangedSignal();
            }
            _lastProbedCaretIndex = normalizedCaret;
            _lastProbeSource = source;
            snapshot = new(true, text, _inputProbeRevision, DateTimeOffset.UtcNow, source, _lastProbedCaretIndex);
        }
        changedSignal?.TrySetResult(snapshot.Revision);
        return CommandResult.Ok("输入探针已采样。", snapshot);
    }

    // ------------------------------------------------------------------
    // TV mirror (big screen) ownership
    // ------------------------------------------------------------------
    // While a mirror is active the draft lives in memory here. The real editor
    // keeps whatever it had, nothing steals the foreground, and the draft is
    // committed in one operation on demand.

    private bool IsMirrorActive()
    {
        lock (_inputProbeSync) return _mirrorText is not null;
    }

    private static CommandResult MirrorOwnsInput(string operation) =>
        CommandResult.Fail("MirrorOwnsInput", $"TV 大屏正在接管编辑，{operation}由大屏处理。");

    private static string? NormalizeMirrorText(string text) =>
        text.Length <= MaxTextLength && !text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
            ? text
            : null;

    private bool TryReadMirror(out CommandResult result)
    {
        lock (_inputProbeSync)
        {
            if (_mirrorText is null)
            {
                result = default!;
                return false;
            }
            // Mirror traffic never reaches Chromium, so answer from memory
            // instead of polling the occluded, throttled provider.
            result = PublishInputProbe(_mirrorText, caretIndex: _mirrorCaret);
            return true;
        }
    }

    /// <summary>
    /// Reads an optional caret position supplied by the mirror so inserted text
    /// (voice) lands where the big screen's caret is, not at the end.
    /// </summary>
    private static int ReadMirrorCaret(IReadOnlyDictionary<string, string> args, int length) =>
        args.TryGetValue("caret", out var caretValue) && int.TryParse(caretValue, out var caret)
            ? Math.Clamp(caret, 0, length)
            : length;

    private CommandResult AppendMirrorDraft(
        WindowTarget target,
        AutomationElement root,
        IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("text", out var text))
            return CommandResult.Fail("InvalidArgument", "input 需要 text。");
        var normalized = NormalizeMirrorText(text);
        if (normalized is null)
            return CommandResult.Fail("InvalidText", "文本上限 20000 字符，且不允许换行以外的控制字符。");
        string draft;
        int caretAfter;
        lock (_inputProbeSync)
        {
            var current = _mirrorText ?? string.Empty;
            // Voice input follows the big screen's caret instead of always
            // appending, so a draft edited in the middle stays coherent.
            var caret = Math.Clamp(_mirrorCaret ?? current.Length, 0, current.Length);
            draft = current.Insert(caret, normalized);
            if (draft.Length > MaxTextLength) return CommandResult.Fail("InvalidText", "TV 大屏草稿上限 20000 字符。");
            caretAfter = caret + normalized.Length;
            _mirrorText = draft;
            _mirrorCaret = caretAfter;
        }
        // The draft is mirrored into the real editor right away, so its content
        // is never stale while the big screen stays open.
        return SyncMirrorToEditor(target, root, draft, caretAfter, "mirror-append",
            "已把文本插入 TV 大屏并同步到输入框。");
    }

    private CommandResult ReplaceMirrorDraft(
        WindowTarget target,
        AutomationElement root,
        IReadOnlyDictionary<string, string> args)
    {
        // Pure navigation on the big screen: only the mirrored caret moves, so
        // neither the draft text nor the real editor is touched.
        if (args.TryGetValue("caretOnly", out var caretOnlyValue) &&
            bool.TryParse(caretOnlyValue, out var caretOnly) && caretOnly)
        {
            string current;
            int caretOnlyIndex;
            lock (_inputProbeSync)
            {
                current = _mirrorText ?? string.Empty;
                caretOnlyIndex = ReadMirrorCaret(args, current.Length);
                _mirrorCaret = caretOnlyIndex;
            }
            var moved = PublishInputProbe(current, authoritative: true, sourceOverride: "mirror-caret", caretIndex: caretOnlyIndex);
            return CommandResult.Ok("已同步 TV 大屏光标位置。", moved.Data);
        }
        if (!args.TryGetValue("text", out var text))
            return CommandResult.Fail("InvalidArgument", "input.replace 需要 text；传入空字符串会清空输入框。");
        var normalized = NormalizeMirrorText(text);
        if (normalized is null)
            return CommandResult.Fail("InvalidText", "文本上限 20000 字符，且不允许制表、换行以外的控制字符。");
        var caret = ReadMirrorCaret(args, normalized.Length);
        bool changed;
        lock (_inputProbeSync)
        {
            changed = !string.Equals(_mirrorText, normalized, StringComparison.Ordinal);
            _mirrorText = normalized;
            _mirrorCaret = caret;
        }
        if (!changed)
        {
            var idle = PublishInputProbe(normalized, authoritative: true, sourceOverride: "mirror-caret", caretIndex: caret);
            return CommandResult.Ok("已同步 TV 大屏光标位置。", idle.Data);
        }
        return SyncMirrorToEditor(target, root, normalized, caret, "mirror-replace",
            "已更新 TV 大屏内容并同步到输入框。");
    }

    /// <summary>
    /// Pushes the mirrored draft into the real editor, then republishes the
    /// probe with the mirror's caret. The focused write is skipped when the
    /// editor already holds exactly that text.
    /// </summary>
    private CommandResult SyncMirrorToEditor(
        WindowTarget target,
        AutomationElement root,
        string draft,
        int caret,
        string source,
        string message)
    {
        var write = CommitMirror(target, root);
        if (!write.Success) return write;
        var result = PublishInputProbe(draft, authoritative: true, sourceOverride: source, caretIndex: caret);
        return CommandResult.Ok(message, result.Data);
    }

    private CommandResult SetMirrorMode(
        WindowTarget target,
        AutomationElement root,
        IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("active", out var activeValue) || !bool.TryParse(activeValue, out var active))
            return CommandResult.Fail("InvalidArgument", "input.mirror 需要 active=true 或 active=false。");
        if (!active) return ReleaseMirror(target, root);

        var seed = args.TryGetValue("text", out var text) ? NormalizeMirrorText(text) : string.Empty;
        if (seed is null)
            return CommandResult.Fail("InvalidText", "镜像文本上限 20000 字符，且不允许换行以外的控制字符。");
        var caret = ReadMirrorCaret(args, seed.Length);
        bool reentered;
        lock (_inputProbeSync)
        {
            reentered = _mirrorText is not null;
            _mirrorText = seed;
            _mirrorCaret = caret;
        }
        var result = PublishInputProbe(seed, authoritative: true, sourceOverride: "mirror-enter", caretIndex: caret);
        return CommandResult.Ok(reentered
            ? "TV 大屏已重新接管输入。"
            : "TV 大屏已接管输入；真实输入框不再被遥控器按键或焦点改动。", result.Data);
    }

    private CommandResult ReleaseMirror(WindowTarget target, AutomationElement root)
    {
        lock (_inputProbeSync)
        {
            if (_mirrorText is null) return CommandResult.Ok("TV 大屏镜像未激活。", null);
        }
        var commit = CommitMirror(target, root);
        if (!commit.Success) return commit;
        lock (_inputProbeSync)
        {
            _mirrorText = null;
            _mirrorCaret = null;
        }
        return CommandResult.Ok("TV 大屏已交还输入框。", commit.Data);
    }

    private CommandResult CommitMirror(WindowTarget target, AutomationElement root)
    {
        string draft;
        int? caret;
        lock (_inputProbeSync)
        {
            draft = _mirrorText ?? string.Empty;
            caret = _mirrorCaret;
        }
        var composer = FindComposerFast(root);
        if (composer is null)
            return CommandResult.Fail("ComposerNotFound", "当前页面没有可写入的输入框。");
        var before = ReadProbeText(composer);
        // Per-change mirroring keeps the editor identical most of the time, so
        // the focused rewrite (and its brief foreground handoff) is skipped.
        if (string.Equals(before, draft, StringComparison.Ordinal))
        {
            var inSync = PublishInputProbe(draft, authoritative: true, sourceOverride: "mirror-in-sync", caretIndex: caret);
            return CommandResult.Ok("输入框已与 TV 大屏一致。", inSync.Data);
        }
        // The real editor still holds an older value while UIA is occluded;
        // remember it so the throttled provider cannot roll the probe back.
        lock (_inputProbeSync) _supersededProbeHashes.Add(ProbeFingerprint(before));

        var write = Input(target, composer, new Dictionary<string, string>
        {
            ["text"] = draft,
            ["replace"] = "true"
        });
        return write.Success ? CommandResult.Ok("已把 TV 大屏内容提交到输入框。", write.Data) : write;
    }

    private void BeginInputWrite()
    {
        lock (_inputProbeSync) _inputWriteInProgress++;
    }

    private void EndInputWrite()
    {
        lock (_inputProbeSync) _inputWriteInProgress--;
    }

    private bool TryReadProbeDuringWrite(out CommandResult result)
    {
        lock (_inputProbeSync)
        {
            if (_inputWriteInProgress == 0)
            {
                result = default!;
                return false;
            }
            if (_lastProbedText is null)
            {
                result = CommandResult.Fail("InputProbeBusy", "输入框正在写入，尚无可复用的探针快照。");
                return true;
            }
            var snapshot = new InputProbeSnapshot(
                true, _lastProbedText, _inputProbeRevision, DateTimeOffset.UtcNow, "write-in-progress", _lastProbedCaretIndex);
            result = CommandResult.Ok("输入框正在写入，返回最近一次完整快照。", snapshot);
            return true;
        }
    }

    private (string? Text, int? CaretIndex) ReadCachedProbe()
    {
        lock (_inputProbeSync) return (_lastProbedText, _lastProbedCaretIndex);
    }

    private static string RemoveTextElementBefore(string text, int caretIndex, out int newCaretIndex)
    {
        caretIndex = Math.Clamp(caretIndex, 0, text.Length);
        if (text.Length == 0 || caretIndex == 0)
        {
            newCaretIndex = caretIndex;
            return text;
        }
        var starts = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        var start = starts.LastOrDefault(value => value < caretIndex);
        newCaretIndex = start;
        return text.Remove(start, caretIndex - start);
    }

    private static ulong ProbeFingerprint(string text)
    {
        // Compactly remember exact superseded drafts without retaining many
        // large copies during a long Back-key repeat.
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var character in text)
        {
            hash ^= character;
            hash *= prime;
        }
        hash ^= (ulong)text.Length;
        return hash * prime;
    }

    // ------------------------------------------------------------------
    // UIA lookups
    // ------------------------------------------------------------------

    // Remembered handle of the window that last exposed the composer: the
    // steady-state read/write path then resolves with a single UIA lookup
    // instead of probing every ChatGPT window each poll.
    private static nint _composerWindow;

    private static WindowTarget SelectComposerWindow(List<WindowTarget> windows, IReadOnlyDictionary<string, string> args)
    {
        var cached = windows.FirstOrDefault(w => w.Handle == _composerWindow);
        var cachedRoot = cached is null ? null : TryRoot(cached.Handle);
        if (cached is not null && cachedRoot is not null && FindComposerFast(cachedRoot) is not null)
            return cached;
        try { return NativeWindow.Select(windows, args); }
        catch (ControlException exception) when (exception.Code == "AmbiguousWindow")
        {
            foreach (var candidate in windows.OrderByDescending(NativeWindow.AreaOf))
            {
                if (TryRoot(candidate.Handle) is { } candidateRoot && FindComposerFast(candidateRoot) is not null)
                {
                    _composerWindow = candidate.Handle;
                    return candidate;
                }
            }
            throw new ControlException("AmbiguousWindow", "存在多个目标窗口，且都找不到输入框，请使用 --window 指定。");
        }
    }

    private static string? ReadText(AutomationElement element)
    {
        lock (AutomationSync)
        {
        // Chromium exposes the composer through ValuePattern even while it is
        // fully covered by the non-activating TV mirror. This is the closest
        // representation of the actual editable value and does not concatenate
        // provider-owned descendant labels.
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            return ((ValuePattern)value).Current.Value;

        // Fall back to rendered text nodes for accessibility providers where
        // ValuePattern is unavailable.
        var rendered = element.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>()
            .Select(child => child.Current.Name)
            .Where(value => !string.IsNullOrEmpty(value))
            .ToArray();
        if (rendered.Length > 0) return string.Concat(rendered);
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text)) return ((TextPattern)text).DocumentRange.GetText(-1);
        return null;
        }
    }

    // Name-only composer lookup used by the fallback scan. ClassName is
    // deliberately not read here: Chromium exposes whole Tailwind class strings
    // as ClassName, and caching those for a large tree is pure overhead.
    private static AutomationElement? FindComposer(List<AutomationElement> elements, bool required)
    {
        var found = elements.Where(e => e.Cached.ControlType == ControlType.Edit && e.Cached.IsEnabled &&
            ComposerNames.Any(name => e.Cached.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (found.Length == 1) return found[0];
        if (required) throw new ControlException(found.Length == 0 ? "ComposerNotFound" : "AmbiguousComposer", "当前页面无法唯一定位 ChatGPT 输入框。");
        return null;
    }

    // The fullscreen TV mirror can make Chromium report the composer as
    // offscreen even though it remains the focused editing control. Never use
    // IsOffscreen as a hard filter here. Prefer the focused match, then the
    // visible match, and finally the largest matching editor when Chromium keeps
    // stale hidden editors in its accessibility tree.
    //
    // Chromium's provider answers the very first query of a control type with
    // UIA_E_ELEMENTNOTAVAILABLE (0x80040201) while it is still building its
    // accessibility tree, so one attempt is not enough: the query is retried and
    // only a sustained failure is reported.
    private static AutomationElement? FindComposerFast(AutomationElement root)
    {
        if (!Monitor.TryEnter(AutomationSync, LockWait)) return null;
        try
        {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var cache = new CacheRequest();
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.ClassNameProperty);
                cache.Add(AutomationElement.IsEnabledProperty);
                cache.Add(AutomationElement.IsOffscreenProperty);
                cache.Add(AutomationElement.HasKeyboardFocusProperty);
                cache.Add(AutomationElement.BoundingRectangleProperty);
                using (cache.Activate())
                {
                    var matches = root.FindAll(TreeScope.Descendants, EditCondition)
                        .Cast<AutomationElement>()
                        .Where(candidate => candidate.Cached.IsEnabled && IsComposer(candidate))
                        .ToArray();
                    if (matches.Length == 0) return null;
                    return matches.FirstOrDefault(candidate => candidate.Cached.HasKeyboardFocus)
                        ?? matches.FirstOrDefault(candidate => !candidate.Cached.IsOffscreen)
                        ?? matches.OrderByDescending(candidate =>
                            candidate.Cached.BoundingRectangle.Width * candidate.Cached.BoundingRectangle.Height).First();
                }
            }
            catch (Exception) when (attempt < 1)
            {
                Thread.Sleep(80);
            }
            catch (Exception)
            {
                return null;
            }
        }
        }
        finally { Monitor.Exit(AutomationSync); }
    }

    /// <summary>
    /// The ChatGPT composer is recognized by its known placeholder names or,
    /// when the UI language is unknown, by the ProseMirror editor class the app
    /// actually uses. Content reads never depend on a placeholder still being
    /// displayed, so a filled composer stays locatable.
    /// </summary>
    private static bool IsComposer(AutomationElement candidate) =>
        ComposerNames.Any(name => candidate.Cached.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(candidate.Cached.ClassName, ComposerClassName, StringComparison.Ordinal);

    private static AutomationElement FindComposerRequired(AutomationElement root)
    {
        for (var attempt = 0; ; attempt++)
        {
            var composer = FindComposerFast(root);
            if (composer is not null) return composer;
            // Accessibility may still be warming up; periodically fall back to a
            // plain Edit lookup in case the provider rejects the combined
            // property condition.
            if (attempt >= 5 &&
                FindComposer(FindByCondition(root, EditCondition), false) is { } scanned) return scanned;
            if (attempt >= 20) throw new ControlException("ComposerNotFound", "当前页面无法唯一定位 ChatGPT 输入框。");
            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// Bounded wait for the UIA lock. A provider call can block inside Chromium
    /// while it rebuilds its tree; without a bound, one such call made every other
    /// action wait behind it, including the big screen's commit.
    /// </summary>
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(2);

    private static readonly Condition EditCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);

    private static readonly Condition ButtonCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);

    private static readonly Condition TextCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);

    private static readonly Condition ListCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List);

    private static readonly Condition ListItemCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);

    private static readonly Condition RadioButtonCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton);

    private static readonly Condition CheckBoxCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox);

    /// <summary>
    /// Resolves a window handle to its UIA root without ever throwing: while the
    /// app rebuilds its window Chromium answers FromHandle with
    /// UIA_E_ELEMENTNOTAVAILABLE, and that single call used to fail an entire
    /// action with an opaque AutomationFailed.
    /// </summary>
    private static AutomationElement? TryRoot(nint handle)
    {
        lock (AutomationSync)
        {
            try { return AutomationElement.FromHandle(handle); }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// Parent lookup through the control view walker, serialized and never
    /// throwing: while Chromium rebuilds its tree the walker answers
    /// UIA_E_ELEMENTNOTAVAILABLE, and that exception used to escape
    /// FindConfirmation and fail the whole status action.
    /// </summary>
    private static AutomationElement? TryParent(AutomationElement? element)
    {
        lock (AutomationSync)
        {
            if (element is null) return null;
            try { return TreeWalker.ControlViewWalker.GetParent(element); }
            catch (Exception) { return null; }
        }
    }

    private static ControlType? TryControlType(AutomationElement element)
    {
        lock (AutomationSync)
        {
            try { return element.Current.ControlType; }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// One narrow, type-scoped descendant query with the properties this plugin
    /// reads cached, retried while the provider is still building its tree. Three
    /// rules keep it working from the background host process, all taken from the
    /// live app: never walk the whole tree filtered by IsOffscreen; never hand the
    /// provider a condition that also matches on Name/Or; and always retry, since
    /// Chromium answers the first query of a control type with
    /// UIA_E_ELEMENTNOTAVAILABLE (0x80040201). Name and visibility filtering
    /// therefore happens locally, on cached values, after the query returns.
    /// </summary>
    private static List<AutomationElement> FindByCondition(AutomationElement scope, Condition condition)
    {
        if (!Monitor.TryEnter(AutomationSync, LockWait))
        {
            // Never queue behind a stuck provider call: an empty result degrades
            // this one query instead of freezing the whole action chain.
            return [];
        }
        try
        {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var cache = new CacheRequest();
                foreach (var property in new[]
                         {
                             AutomationElement.NameProperty, AutomationElement.ControlTypeProperty,
                             AutomationElement.AutomationIdProperty, AutomationElement.IsEnabledProperty,
                             AutomationElement.IsOffscreenProperty, AutomationElement.HasKeyboardFocusProperty
                         })
                    cache.Add(property);
                using (cache.Activate())
                    return scope.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>().ToList();
            }
            catch (Exception) when (attempt < 1)
            {
                Thread.Sleep(80);
            }
            catch (Exception)
            {
                // A sustained provider-side failure stays local to this query:
                // status and the remote's confirm key still need an answer.
                return [];
            }
        }
        }
        finally { Monitor.Exit(AutomationSync); }
    }

    private static bool IsEnabled(AutomationElement element) => SafeEnabled(element);

    /// <summary>
    /// Property reads go through these guards because a cached value can still be
    /// an error marker: Chromium stores UIA_E_ELEMENTNOTAVAILABLE for properties it
    /// failed to read, and touching one throws from inside the provider — which
    /// used to fail status as a whole. Every guard is serialized on the same lock
    /// as the query that produced the element.
    /// </summary>
    private static string SafeName(AutomationElement element)
    {
        lock (AutomationSync)
        {
            try { return element.Cached.Name; }
            catch (Exception) { return string.Empty; }
        }
    }

    private static string SafeAutomationId(AutomationElement element)
    {
        lock (AutomationSync)
        {
            try { return element.Cached.AutomationId; }
            catch (Exception) { return string.Empty; }
        }
    }

    private static bool SafeEnabled(AutomationElement element)
    {
        lock (AutomationSync)
        {
            try { return element.Cached.IsEnabled; }
            catch (Exception) { return false; }
        }
    }

    private static bool SafeOffscreen(AutomationElement element)
    {
        lock (AutomationSync)
        {
            try { return element.Cached.IsOffscreen; }
            catch (Exception) { return true; }
        }
    }

    private static bool SafeFocused(AutomationElement element)
    {
        lock (AutomationSync)
        {
            try { return element.Cached.HasKeyboardFocus; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Visible buttons whose accessible name is exactly one of the candidates.</summary>
    private static List<AutomationElement> FindButtons(AutomationElement root, string[] names) =>
        FindByCondition(root, ButtonCondition)
            .Where(e => !SafeOffscreen(e) && names.Contains(SafeName(e), StringComparer.OrdinalIgnoreCase))
            .ToList();

    private static void InvokeElement(AutomationElement element, string what)
    {
        lock (AutomationSync)
        {
            if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                throw new ControlException("InvokeUnsupported", $"{what}不支持 InvokePattern。");
            ((InvokePattern)pattern).Invoke();
        }
    }

    /// <summary>
    /// Type-scoped control dump for diagnostics. Each query fails on its own, so
    /// one unreadable control type can no longer hide the rest of the report.
    /// </summary>
    private static object[] Inspect(AutomationElement root)
    {
        var results = new List<object>();
        foreach (var type in new[]
                 {
                     ControlType.Button, ControlType.Edit, ControlType.List,
                     ControlType.ListItem, ControlType.Document
                 })
        {
            try
            {
                foreach (var element in FindByCondition(root,
                             new PropertyCondition(AutomationElement.ControlTypeProperty, type)).Take(300))
                    results.Add(Summarize(element));
            }
            catch (Exception e)
            {
                results.Add(new
                {
                    name = $"<{type.ProgrammaticName} 查询失败>",
                    type = e.GetType().Name,
                    id = $"0x{e.HResult:X8}",
                    enabled = false,
                    focused = false,
                    selected = false,
                    error = e.Message
                });
            }
        }
        return results.ToArray();
    }

    // ------------------------------------------------------------------
    // Confirmation cards
    // ------------------------------------------------------------------

    private static AutomationElement? FindConfirmation(AutomationElement root)
    {
        var lists = FindByCondition(root, ListCondition)
            .Where(e => ConfirmationListNames.Contains(SafeName(e), StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (lists.Length > 1) throw new ControlException("AmbiguousConfirmation", "有多个确认区域。");
        if (lists.Length == 1) return TryParent(lists[0]);
        var hints = FindByCondition(root, TextCondition)
            .Where(e => ConfirmationHints.Any(hint => SafeName(e).StartsWith(hint, StringComparison.Ordinal)))
            .ToArray();
        if (hints.Length > 1) throw new ControlException("AmbiguousConfirmation", "有多个确认提示。");
        if (hints.Length == 0) return null;
        var parent = TryParent(hints[0]);
        for (int i = 0; parent is not null && i < 6; i++, parent = TryParent(parent))
        {
            var type = TryControlType(parent);
            if (type is null || type == ControlType.Document || type == ControlType.Window) break;
            if (Options(parent).Count > 0) return parent;
        }
        return null;
    }

    private static List<AutomationElement> Options(AutomationElement scope)
    {
        var options = new List<AutomationElement>();
        foreach (var condition in new[] { ListItemCondition, RadioButtonCondition, CheckBoxCondition })
            options.AddRange(FindByCondition(scope, condition).Where(IsEnabled));
        return options;
    }

    private static CommandResult Confirm(string action, WindowTarget target, AutomationElement root)
    {
        var panel = FindConfirmation(root) ?? throw new ControlException("NoConfirmation", "当前页面没有确认信息。");
        var options = Options(panel);
        var buttons = FindButtons(panel, ConfirmationButtonNames);
        if (action == "confirm.status") return CommandResult.Ok("当前确认信息", new { options = options.Select(Summarize), controls = buttons.Select(Summarize) });
        if (action == "confirm.submit")
        {
            var submit = buttons.Where(IsEnabled).ToArray();
            if (submit.Length != 1)
                throw new ControlException("ConfirmUnavailable", submit.Length > 1 ? "找到多个同名按钮，无法安全选择。" : "未找到可用的确认/提交按钮。");
            NativeWindow.RequireForeground(target);
            InvokeElement(submit[0], "确认按钮");
            return CommandResult.Ok("已提交当前确认信息。", new { state = "Dispatched" });
        }
        if (options.Count == 0) throw new ControlException("NoOptions", "确认区域中没有可选择的选项。");
        var current = options.FirstOrDefault(SafeFocused) ?? options.FirstOrDefault(IsSelected) ?? options[0];
        current.SetFocus();
        Thread.Sleep(50);
        if (!Automation.Compare(AutomationElement.FocusedElement, current)) throw new ControlException("OptionFocusLost", "无法聚焦确认选项。");
        NativeWindow.Key(target, action switch { "confirm.up" => 0x26, "confirm.down" => 0x28, "confirm.select" => 0x0D, _ => throw new ControlException("UnknownAction", action) });
        Thread.Sleep(100);
        return CommandResult.Ok(action == "confirm.select" ? "已选择当前选项；问答卡片可能还需要 confirm submit。" : "已切换确认选项。",
            new { focused = AutomationElement.FocusedElement.Current.Name, state = "Dispatched" });
    }

    private static bool IsSelected(AutomationElement element)
    {
        lock (AutomationSync)
        {
            try { return element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) && ((SelectionItemPattern)pattern).Current.IsSelected; }
            catch (Exception) { return false; }
        }
    }

    private static object Summarize(AutomationElement e) => new
    {
        name = SafeName(e),
        type = TryControlType(e)?.ProgrammaticName ?? string.Empty,
        id = SafeAutomationId(e),
        enabled = SafeEnabled(e),
        focused = SafeFocused(e),
        selected = IsSelected(e)
    };

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_inputProbeSync)
        {
            _mirrorText = null;
            _mirrorCaret = null;
            cancellation = _inputWatcherCancellation;
            _inputWatcherCancellation = null;
        }
        cancellation?.Cancel();
        cancellation?.Dispose();
    }
}
