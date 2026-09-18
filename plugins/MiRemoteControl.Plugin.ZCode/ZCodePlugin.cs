using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using MiRemoteControl.Contracts;

namespace MiRemoteControl.Plugin.ZCode;

public sealed class ZCodePlugin : IHarnessPlugin, IAsyncHarnessPlugin
{
    private static readonly string[] ComposerNames = ["向 ZCode 提问", "提出后续修改要求", "继续输入以排队后续修改", "Ask ZCode anything", "Ask for follow-up changes", "Keep typing to queue follow-up changes"];
    private static readonly string[] ConfirmationHints = ["使用 Tab / 上下键选择", "Use Tab / arrow keys to choose"];
    private readonly object _inputProbeSync = new();
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
    public PluginDescriptor Descriptor { get; } = new("mrc.zcode", "ZCode 控制", "0.0.1",
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
            if (!Descriptor.Actions.Any(a => a.Id == action)) return CommandResult.Fail("UnknownAction", "未知 ZCode 操作。");
            foreach (var key in arguments.Keys)
                if (key is not ("exe" or "window" or "text" or "replace" or "direction" or "delete" or "active" or "caret" or "caretOnly" or "afterRevision" or "timeoutMs"))
                    return CommandResult.Fail("InvalidArgument", $"未知参数：{key}");
            if (action == HarnessPluginActions.InputWatch)
                return CommandResult.Fail("AsyncRequired", "input.watch 必须通过异步插件接口调用。");
            var exe = ResolveExecutable(arguments);
            var windows = NativeWindow.Find(exe);
            if (action == "open") return Open(exe, windows, arguments);
            if (action == "status" && windows.Count == 0)
                return CommandResult.Ok("ZCode 未打开", new TargetPluginStatus(false, false, Details: new { windows }));
            // Several ZCode windows may coexist while the remote's fullscreen
            // mirror or the studio window owns the foreground, so composer
            // actions must not depend on foreground disambiguation. Pick the
            // window that actually exposes the composer, largest first.
            var target = action == "close"
                ? NativeWindow.Select(windows, arguments)
                : SelectComposerWindow(windows, arguments);
            if (action == "close")
            {
                NativeWindow.Close(target);
                return CommandResult.Ok("已发送标题栏关闭请求；若应用弹出保存提示或驻留托盘，将遵循应用行为。", new { state = "CloseRequested" });
            }
            var root = AutomationElement.FromHandle(target.Handle);
            if (action == "input.read")
            {
                if (TryReadMirror(out var mirrorRead)) return mirrorRead;
                // Polled continuously by the fullscreen mirror: a provider-side
                // filtered lookup instead of the full-tree cached walk below.
                // The 3000-element scan per poll kept ZCode busy and delayed
                // voice input by whole turns.
                var composer = FindComposerFast(root);
                if (composer is null)
                    return CommandResult.Fail("ComposerNotFound", "当前页面没有可读取的输入框。");
                return CommandResult.Ok("已读取输入框内容。", new { text = ReadText(composer) ?? string.Empty });
            }
            if (action == HarnessPluginActions.InputProbe)
            {
                if (TryReadMirror(out var mirrorProbe)) return mirrorProbe;
                // Do not query Electron's accessibility provider while the
                // same contenteditable is processing a voice write. Those
                // concurrent reads made DocumentRange settle one edit late.
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
                // its deterministic baseline. Chromium does perform the edit
                // while occluded, but its UIA tree keeps exposing the longer
                // pre-delete value until the fullscreen mirror closes.
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
                // Voice traffic types at conversational pace; per-utterance
                // full-tree scans added seconds of latency, so typing actions
                // resolve the composer through the provider-filtered lookup.
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
            // Electron may enable accessibility lazily on the first query.
            List<AutomationElement> elements = [];
            for (int i = 0; i < 20; i++)
            {
                elements = Descendants(root);
                if (elements.Any(e => e.Cached.ControlType == ControlType.Document || e.Cached.ControlType == ControlType.Edit)) break;
                Thread.Sleep(100);
            }
            if (action == "inspect") return CommandResult.Ok("UIA 诊断", elements.Select(Summarize).ToArray());
            if (action == "status") return CommandResult.Ok("ZCode 窗口已连接", new TargetPluginStatus(
                Running: true,
                Focused: windows.Any(NativeWindow.IsForeground),
                CanSend: Buttons(elements, ["发送", "Send", "加入队列", "Queue"]).Any(e => e.Cached.IsEnabled),
                CanStop: Buttons(elements, ["停止生成", "Stop"]).Any(e => e.Cached.IsEnabled),
                Details: new
                {
                    windows = windows.Select(w => new { handle = w.Handle.ToInt64(), w.ProcessId, w.Title }),
                    composer = FindComposer(elements, false) is not null,
                    confirmation = FindConfirmation(elements) is not null
                }));
            NativeWindow.Focus(target);
            if (action.StartsWith("confirm.", StringComparison.Ordinal)) return Confirm(action, target, elements);
            if (action != "stop" && action != "backspace" && FindConfirmation(elements) is not null)
                return CommandResult.Fail("ConfirmationPending", "当前存在确认卡片，请使用 confirm 命令处理，避免操作被遮挡的输入区域。");
            switch (action)
            {
                case "send":
                    // A mirror never reaches this branch: it is rejected above
                    // so only the mirror consumer sends, after it committed.
                    InvokeUnique(target, Buttons(elements, ["发送", "Send", "加入队列", "Queue"]), "SendUnavailable", "当前页面没有可用的发送按钮。");
                    PublishInputProbe(string.Empty, authoritative: true, sourceOverride: "send");
                    return CommandResult.Ok("已调用当前页面发送按钮。", new { state = "Dispatched" });
                case "stop":
                    InvokeUnique(target, Buttons(elements, ["停止生成", "Stop"]), "NoRunningTask", "当前页面没有可停止的运行任务。");
                    return CommandResult.Ok("已调用当前任务停止按钮。", new { state = "StopRequested" });
                default: return CommandResult.Fail("UnknownAction", action);
            }
        }
        catch (ControlException e) { return CommandResult.Fail(e.Code, e.Message); }
        catch (ElementNotAvailableException) { return CommandResult.Fail("TargetChanged", "页面控件已变化，请重新读取状态。"); }
        catch (Exception e) { return CommandResult.Fail("AutomationFailed", e.Message); }
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

    private static string ResolveExecutable(IReadOnlyDictionary<string, string> args)
    {
        var candidates = args.TryGetValue("exe", out var path) ? new[] { Path.GetFullPath(path) } : new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ZCode", "ZCode.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ZCode", "ZCode.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new ControlException("AppNotInstalled", "未找到 ZCode.exe，请使用 --exe 指定安装路径。");
    }
    private static CommandResult Open(string exe, List<WindowTarget> windows, IReadOnlyDictionary<string, string> args)
    {
        if (windows.Count == 0)
        {
            using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            var clock = Stopwatch.StartNew();
            while (windows.Count == 0 && clock.ElapsedMilliseconds < 12000) { Thread.Sleep(150); windows = NativeWindow.Find(exe); }
        }
        // Electron may finish restoring/reparenting its window shortly after
        // it first appears, can replace the HWND entirely during a
        // single-instance handoff, and briefly owns two visible windows.
        // Re-enumerate and re-activate until the target stays genuinely in
        // the foreground, not just on the taskbar.
        ControlException? failure = null;
        WindowTarget? activated = null;
        for (var attempt = 0; attempt < 5 && (activated is null || !NativeWindow.IsForeground(activated)); attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    Thread.Sleep(150);
                    windows = NativeWindow.Find(exe);
                }
                var candidate = NativeWindow.Select(windows, args);
                NativeWindow.Focus(candidate, promoteToTopmost: true);
                Thread.Sleep(200); // let Electron settle, then confirm it kept the foreground
                activated = candidate;
            }
            catch (ControlException e) { failure = e; }
        }
        if (activated is not null && NativeWindow.IsForeground(activated))
            return CommandResult.Ok("ZCode 已在前台显示。", new { handle = activated.Handle.ToInt64(), activated.ProcessId });
        throw failure ?? new ControlException("FocusDenied", "Windows 未允许切到 ZCode 前台。");
    }
    private CommandResult Input(WindowTarget target, AutomationElement composer, IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("text", out var text)) throw new ControlException("InvalidArgument", "input 需要 --text 或 --file。");
        if (text.Length > 20000 || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r')))
            throw new ControlException("InvalidText", "文本上限 20000 字符，且不允许换行以外的控制字符。");
        var replace = args.TryGetValue("replace", out var r) && bool.Parse(r);
        void CheckFocus()
        {
            if (!Automation.Compare(AutomationElement.FocusedElement, composer))
                throw new ControlException("EditorFocusLost", "输入框焦点已改变，停止输入。");
        }
        // An empty Electron contenteditable lazily creates its first editable
        // text node after receiving focus. During that transition the native
        // window can briefly lose foreground ownership, so the old fixed 80ms
        // delay made the first voice write fail with TargetChanged. Require a
        // sustained foreground + composer focus before the first empty write.
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
        else
        {
            try
            {
                if (text.Length > 0 && (replace || initiallyEmpty))
                    NativeWindow.PasteText(target, text, CheckFocus);
                else NativeWindow.Text(target, text, CheckFocus);
            }
            catch (ControlException) { throw; }
        }
        var normalized = Normalize(text);
        var expected = replace ? text : before + text;
        string? actual = null;
        var verified = false;
        // Electron can expose the new rendered draft a little later than the
        // key dispatch. Give the contenteditable provider a short settling
        // window instead of sampling exactly once on the previous frame.
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
            // Chromium throttles the accessibility tree while ZCode is fully
            // occluded by the TV mirror. SendInput still reaches the focused
            // composer, but UIA keeps returning the exact pre-write frame
            // until the overlay closes. Since every key was accepted by
            // SendInput and focus remained on this composer throughout, the
            // deterministic post-write value is safe to publish immediately.
            if (actual is not null && Normalize(actual) == Normalize(before))
            {
                PublishInputProbe(expected, authoritative: true, sourceOverride: "write-dispatch");
                return CommandResult.Ok(
                    "内容已完整发送；目标 UIA 暂未刷新，已按写入结果更新探针。",
                    new { characters = text.Length, verified = false, inferred = true });
            }
            return CommandResult.Fail("InputUnverified", "已尝试输入，但回读结果既不是写入前内容，也无法确认完整新文本。请检查当前页面，勿直接重复输入。");
        }

        // Voice input reaches ZCode through this action. The post-write value
        // is therefore newer than a concurrently sampled UIA document range.
        // Publish it immediately so the fullscreen mirror cannot be rolled
        // back to the preceding utterance by Electron's delayed provider.
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
        if (text.Length > 20000 || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
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
        if (text.Length > 20000 || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
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

        string actual = string.Empty;
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

    private async Task<CommandResult> WatchInputAsync(
        IReadOnlyDictionary<string, string> args,
        CancellationToken ct)
    {
        foreach (var key in args.Keys)
            if (key is not ("exe" or "window" or "afterRevision" or "timeoutMs"))
                return CommandResult.Fail("InvalidArgument", $"未知参数：{key}");
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

        EnsureInputWatcherStarted(args);
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

    private void EnsureInputWatcherStarted(IReadOnlyDictionary<string, string> args)
    {
        lock (_inputProbeSync)
        {
            if (_inputWatcherTask is { IsCompleted: false }) return;
            _inputWatcherCancellation?.Dispose();
            _inputWatcherCancellation = new CancellationTokenSource();
            var options = args
                .Where(pair => pair.Key is "exe" or "window")
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            _inputWatcherTask = Task.Run(() => RunInputWatcherAsync(options, _inputWatcherCancellation.Token));
        }
    }

    private async Task RunInputWatcherAsync(
        IReadOnlyDictionary<string, string> args,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var canRead = false;
            lock (_inputProbeSync) canRead = _inputWriteInProgress == 0 && _mirrorText is null;
            if (canRead)
            {
                try
                {
                    var exe = ResolveExecutable(args);
                    var windows = NativeWindow.Find(exe);
                    var target = SelectComposerWindow(windows, args);
                    var composer = FindComposerFast(AutomationElement.FromHandle(target.Handle));
                    if (composer is not null)
                    {
                        var text = ReadProbeText(composer);
                        PublishInputProbe(text, caretIndex: ReadCaretIndex(composer, text));
                    }
                }
                catch
                {
                    // The observer remains alive while ZCode starts, closes,
                    // or replaces its Electron window. The next sample retries.
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

    private static TaskCompletionSource<long> NewInputChangedSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void FocusComposerStable(WindowTarget target, AutomationElement composer, int stableSamples)
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
        throw new ControlException("EditorFocusLost", "无法稳定聚焦 ZCode 输入框，已取消输入。");
    }
    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
    private static string ReadProbeText(AutomationElement composer)
    {
        var text = ReadText(composer) ?? string.Empty;
        // Electron's TextPattern document range includes one provider-owned
        // terminal line break. Remove exactly that sentinel; if the user
        // actually ended the draft with a newline, the preceding one remains.
        if (text.EndsWith("\r\n", StringComparison.Ordinal)) return text[..^2];
        return text.EndsWith('\n') || text.EndsWith('\r') ? text[..^1] : text;
    }

    private static int? ReadCaretIndex(AutomationElement composer, string text)
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
            // active selection end. Its text length is the UTF-16 index used
            // by the mirror and by string insertion.
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
                    // A longer or divergent value is a genuine subsequent
                    // edit, including typing performed outside this plugin.
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
            // Remembered for long-poll answers: they must report where the last
            // value came from, not a generic "watch".
            _lastProbeSource = source;
            snapshot = new(true, text, _inputProbeRevision, DateTimeOffset.UtcNow, source, _lastProbedCaretIndex);
        }
        changedSignal?.TrySetResult(snapshot.Revision);
        return CommandResult.Ok("输入探针已采样。", snapshot);
    }
    // ---- TV mirror (big screen) ownership ------------------------------
    // While a mirror is active the draft lives in memory here. The real
    // editor keeps whatever it had, nothing steals the foreground, and the
    // draft is committed in one operation on demand.
    private bool IsMirrorActive()
    {
        lock (_inputProbeSync) return _mirrorText is not null;
    }

    private static CommandResult MirrorOwnsInput(string operation) =>
        CommandResult.Fail("MirrorOwnsInput", $"TV 大屏正在接管编辑，{operation}由大屏处理。");

    private static string? NormalizeMirrorText(string text) =>
        text.Length <= 20000 && !text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
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
            // Mirror traffic never reaches Electron, so answer from memory
            // instead of polling the occluded, throttled provider.
            result = PublishInputProbe(_mirrorText, caretIndex: _mirrorCaret);
            return true;
        }
    }

    /// <summary>
    /// Reads an optional caret position supplied by the mirror so inserted
    /// text (voice) lands where the big screen's caret is, not at the end.
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
            if (draft.Length > 20000) return CommandResult.Fail("InvalidText", "TV 大屏草稿上限 20000 字符。");
            caretAfter = caret + normalized.Length;
            _mirrorText = draft;
            _mirrorCaret = caretAfter;
        }
        // Voice lands in the real editor immediately too, so its content is
        // never stale while the big screen stays open.
        return SyncMirrorToEditor(target, root, draft, caretAfter, "mirror-append",
            "已把文本插入 TV 大屏并同步到输入框。");
    }

    private CommandResult ReplaceMirrorDraft(
        WindowTarget target,
        AutomationElement root,
        IReadOnlyDictionary<string, string> args)
    {
        // Pure navigation on the big screen: only the mirrored caret moves,
        // so neither the draft text nor the real editor is touched.
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
        // Caret-only traffic (navigation on the big screen) must not touch the
        // real editor; a real text change is pushed through right away.
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
    // Remembered handle of the window that last exposed the composer: the
    // steady-state read/write path then resolves with a single UIA lookup
    // instead of probing every ZCode window each poll.
    private static nint _composerWindow;
    private static WindowTarget SelectComposerWindow(List<WindowTarget> windows, IReadOnlyDictionary<string, string> args)
    {
        var cached = windows.FirstOrDefault(w => w.Handle == _composerWindow);
        if (cached is not null && FindComposerFast(AutomationElement.FromHandle(cached.Handle)) is not null)
            return cached;
        try { return NativeWindow.Select(windows, args); }
        catch (ControlException exception) when (exception.Code == "AmbiguousWindow")
        {
            foreach (var candidate in windows.OrderByDescending(NativeWindow.AreaOf))
            {
                if (FindComposerFast(AutomationElement.FromHandle(candidate.Handle)) is not null)
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
        // Chromium exposes the composer through ValuePattern even while it is
        // fully covered by the non-activating TV mirror. This is the closest
        // representation of the actual editable value and does not concatenate
        // provider-owned descendant labels.
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            return ((ValuePattern)value).Current.Value;

        // Fall back to rendered text nodes for older Electron accessibility
        // providers where ValuePattern is unavailable.
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
    private static AutomationElement? FindComposer(List<AutomationElement> elements, bool required)
    {
        var found = elements.Where(e => e.Cached.ControlType == ControlType.Edit && e.Cached.IsEnabled &&
            ComposerNames.Any(name => e.Cached.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (found.Length == 1) return found[0];
        if (required) throw new ControlException(found.Length == 0 ? "ComposerNotFound" : "AmbiguousComposer", "当前页面无法唯一定位 ZCode 输入框。");
        return null;
    }
    // The fullscreen TV mirror can make Chromium report the composer as
    // offscreen even though it remains the focused editing control. Never use
    // IsOffscreen as a hard filter here. Prefer the focused match, then the
    // visible match, and finally the largest matching edit when Electron keeps
    // stale hidden editors in its accessibility tree.
    private static AutomationElement? FindComposerFast(AutomationElement root)
    {
        var cache = new CacheRequest();
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.HasKeyboardFocusProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        using (cache.Activate())
        {
            var matches = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>()
                .Where(candidate => candidate.Cached.IsEnabled &&
                    ComposerNames.Any(name => candidate.Cached.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (matches.Length == 0) return null;
            return matches.FirstOrDefault(candidate => candidate.Cached.HasKeyboardFocus)
                ?? matches.FirstOrDefault(candidate => !candidate.Cached.IsOffscreen)
                ?? matches.OrderByDescending(candidate =>
                    candidate.Cached.BoundingRectangle.Width * candidate.Cached.BoundingRectangle.Height).First();
        }
    }
    private static AutomationElement FindComposerRequired(AutomationElement root)
    {
        for (var attempt = 0; ; attempt++)
        {
            var composer = FindComposerFast(root);
            if (composer is not null) return composer;
            // Accessibility may still be warming up; periodically fall back to
            // the cached full walk in case the provider rejects the combined
            // property condition.
            if (attempt >= 5 && FindComposer(Descendants(root), false) is { } scanned) return scanned;
            if (attempt >= 20) throw new ControlException("ComposerNotFound", "当前页面无法唯一定位 ZCode 输入框。");
            Thread.Sleep(100);
        }
    }
    private static List<AutomationElement> Descendants(AutomationElement root)
    {
        var cache = new CacheRequest();
        foreach (var property in new[] { AutomationElement.NameProperty, AutomationElement.ControlTypeProperty,
            AutomationElement.AutomationIdProperty, AutomationElement.IsEnabledProperty, AutomationElement.HasKeyboardFocusProperty }) cache.Add(property);
        using (cache.Activate())
            return root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsOffscreenProperty, false))
                .Cast<AutomationElement>().Take(3000).ToList();
    }
    private static List<AutomationElement> Buttons(List<AutomationElement> elements, string[] names) => elements
        .Where(e => e.Cached.ControlType == ControlType.Button && names.Contains(e.Cached.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    private static void InvokeUnique(WindowTarget target, List<AutomationElement> candidates, string code, string message)
    {
        var enabled = candidates.Where(e => e.Cached.IsEnabled).ToArray();
        if (enabled.Length != 1) throw new ControlException(code, enabled.Length > 1 ? "找到多个同名按钮，无法安全选择。" : message);
        NativeWindow.RequireForeground(target);
        if (!enabled[0].TryGetCurrentPattern(InvokePattern.Pattern, out var p)) throw new ControlException("InvokeUnsupported", "控件不支持 InvokePattern。");
        ((InvokePattern)p).Invoke();
    }
    private static AutomationElement? FindConfirmation(List<AutomationElement> elements)
    {
        // A real permission list has a stable accessible name in ZCode 3.8.1.
        var lists = elements.Where(e => e.Cached.ControlType == ControlType.List &&
            e.Cached.Name is "需要权限" or "Permission required").ToArray();
        if (lists.Length > 1) throw new ControlException("AmbiguousConfirmation", "有多个确认区域。");
        if (lists.Length == 1) return TreeWalker.ControlViewWalker.GetParent(lists[0]);
        var hints = elements.Where(e => e.Cached.ControlType == ControlType.Text &&
            ConfirmationHints.Any(h => e.Cached.Name.StartsWith(h, StringComparison.Ordinal))).ToArray();
        if (hints.Length > 1) throw new ControlException("AmbiguousConfirmation", "有多个确认提示。");
        if (hints.Length == 0) return null;
        var parent = TreeWalker.ControlViewWalker.GetParent(hints[0]);
        for (int i = 0; parent is not null && i < 6; i++, parent = TreeWalker.ControlViewWalker.GetParent(parent))
        {
            if (parent.Current.ControlType == ControlType.Document || parent.Current.ControlType == ControlType.Window) break;
            if (Options(Descendants(parent)).Count > 0) return parent;
        }
        return null;
    }
    private static List<AutomationElement> Options(List<AutomationElement> elements) => elements.Where(e =>
        e.Cached.IsEnabled && (e.Cached.ControlType == ControlType.ListItem || e.Cached.ControlType == ControlType.RadioButton || e.Cached.ControlType == ControlType.CheckBox)).ToList();
    private static CommandResult Confirm(string action, WindowTarget target, List<AutomationElement> elements)
    {
        var panel = FindConfirmation(elements) ?? throw new ControlException("NoConfirmation", "当前页面没有确认信息。");
        var children = Descendants(panel);
        var options = Options(children);
        if (action == "confirm.status") return CommandResult.Ok("当前确认信息", new { options = options.Select(Summarize), controls = Buttons(children, ["确认", "Confirm", "提交", "Submit", "继续", "Continue"]).Select(Summarize) });
        if (action == "confirm.submit")
        {
            InvokeUnique(target, Buttons(children, ["确认", "Confirm", "提交", "Submit", "继续", "Continue"]), "ConfirmUnavailable", "未找到可用的确认/提交按钮。");
            return CommandResult.Ok("已提交当前确认信息。", new { state = "Dispatched" });
        }
        if (options.Count == 0) throw new ControlException("NoOptions", "确认区域中没有可选择的选项。");
        var current = options.FirstOrDefault(e => e.Cached.HasKeyboardFocus) ?? options.FirstOrDefault(IsSelected) ?? options[0];
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
        try { return element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) && ((SelectionItemPattern)p).Current.IsSelected; }
        catch (ElementNotAvailableException) { return false; }
    }
    private static object Summarize(AutomationElement e) => new { name = e.Cached.Name, type = e.Cached.ControlType.ProgrammaticName, id = e.Cached.AutomationId, enabled = e.Cached.IsEnabled, focused = e.Cached.HasKeyboardFocus, selected = IsSelected(e) };
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
