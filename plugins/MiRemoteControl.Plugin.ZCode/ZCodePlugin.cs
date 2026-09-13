using System.Diagnostics;
using System.Windows.Automation;
using MiRemoteControl.Contracts;

namespace MiRemoteControl.Plugin.ZCode;

public sealed class ZCodePlugin : IHarnessPlugin
{
    private static readonly string[] ComposerNames = ["向 ZCode 提问", "提出后续修改要求", "继续输入以排队后续修改", "Ask ZCode anything", "Ask for follow-up changes", "Keep typing to queue follow-up changes"];
    private static readonly string[] ConfirmationHints = ["使用 Tab / 上下键选择", "Use Tab / arrow keys to choose"];
    private readonly object _inputProbeSync = new();
    private string? _lastProbedText;
    private string? _lastVerifiedInputText;
    private string _lastVerifiedInputSource = "write-verification";
    private readonly HashSet<ulong> _supersededProbeHashes = [];
    private long _inputProbeRevision;
    private int _inputWriteInProgress;
    public PluginDescriptor Descriptor { get; } = new("mrc.zcode", "ZCode 控制", "0.2.4", [
        new("open", "打开", "启动或恢复 ZCode 并显示在前台"),
        new("close", "关闭窗口", "相当于右上角关闭，不强杀进程"),
        new("input", "输入内容", "向当前页面输入框追加文本；replace=true 替换"),
        new("input.read", "读取输入", "读取当前输入框中的完整文本"),
        new(TargetPluginActions.InputProbe, "输入探针", "读取实际输入框内容及单调递增的内容版本"),
        new("input.remove-tv-artifact", "清理电视键输入", "仅删除输入框末尾由电视键产生的字符"),
        new("backspace", "删除字符", "删除当前输入框光标前的一个字符"),
        new("send", "发送", "点击当前页面发送按钮"),
        new("stop", "停止任务", "点击当前页面停止生成按钮"),
        new("confirm.up", "上一个选项", "在当前确认卡片向上切换"),
        new("confirm.down", "下一个选项", "在当前确认卡片向下切换"),
        new("confirm.select", "选择当前项", "选择当前确认项，权限卡片会确认该选择"),
        new("confirm.submit", "提交确认", "点击卡片的确认/提交/继续按钮"),
        new("confirm.status", "确认信息", "读取当前确认卡片和选项"),
        new("status", "状态", "读取可见窗口及当前可用操作"),
        new("inspect", "诊断", "读取可见 UIA 控件，可能包含页面文本")
    ], PluginKinds.Target);
    public CommandResult Execute(string action, IReadOnlyDictionary<string,string> arguments)
    {
        try
        {
            if (!Descriptor.Actions.Any(a => a.Id == action)) return CommandResult.Fail("UnknownAction", "未知 ZCode 操作。");
            foreach (var key in arguments.Keys)
                if (key is not ("exe" or "window" or "text" or "replace"))
                    return CommandResult.Fail("InvalidArgument", $"未知参数：{key}");
            var exe = ResolveExecutable(arguments);
            var windows = NativeWindow.Find(exe);
            if (action == "open") return Open(exe, windows, arguments);
            if (action == "status" && windows.Count == 0)
                return CommandResult.Ok("ZCode 未打开", new { running = false, focused = false, windows });
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
                // Polled continuously by the fullscreen mirror: a provider-side
                // filtered lookup instead of the full-tree cached walk below.
                // The 3000-element scan per poll kept ZCode busy and delayed
                // voice input by whole turns.
                var composer = FindComposerFast(root);
                if (composer is null)
                    return CommandResult.Fail("ComposerNotFound", "当前页面没有可读取的输入框。");
                return CommandResult.Ok("已读取输入框内容。", new { text = ReadText(composer) ?? string.Empty });
            }
            if (action == TargetPluginActions.InputProbe)
            {
                // Do not query Electron's accessibility provider while the
                // same contenteditable is processing a voice write. Those
                // concurrent reads made DocumentRange settle one edit late.
                if (TryReadProbeDuringWrite(out var inFlight)) return inFlight;
                var composer = FindComposerFast(root);
                if (composer is null)
                    return CommandResult.Fail("InputProbeUnavailable", "当前页面没有可探测的输入框。");
                return PublishInputProbe(ReadProbeText(composer));
            }
            if (action == "input.remove-tv-artifact")
            {
                var composer = FindComposerFast(root);
                if (composer is null)
                    return CommandResult.Fail("ComposerNotFound", "当前页面没有可读取的输入框。");
                var text = ReadText(composer) ?? string.Empty;
                if (text.Length == 0 || text[^1] is not ('`' or '·'))
                    return CommandResult.Ok("输入框末尾没有电视键残留字符。", new { removed = false, text });

                NativeWindow.Focus(target);
                composer.SetFocus();
                Thread.Sleep(30);
                NativeWindow.Key(target, 0x23, 0x11); // Ctrl+End
                NativeWindow.Key(target, 0x08);       // Backspace
                Thread.Sleep(50);
                var cleaned = ReadText(composer) ?? string.Empty;
                var expected = text[..^1];
                if (Normalize(cleaned) != Normalize(expected))
                    return CommandResult.Fail("TvArtifactCleanupUnverified", "已尝试清理电视键字符，但输入框回读结果不一致。");
                PublishInputProbe(cleaned, authoritative: true, sourceOverride: "artifact-cleanup");
                return CommandResult.Ok("已清理电视键残留字符。", new { removed = true, text = cleaned });
            }
            if (action == "backspace")
            {
                // Key-repeat traffic uses the last complete probe snapshot as
                // its deterministic baseline. Chromium does perform the edit
                // while occluded, but its UIA tree keeps exposing the longer
                // pre-delete value until the fullscreen mirror closes.
                var before = ReadCachedProbeText();
                if (before is null)
                {
                    var composer = FindComposerFast(root);
                    if (composer is null)
                        return CommandResult.Fail("ComposerNotFound", "当前页面没有可读取的输入框。");
                    before = ReadProbeText(composer);
                }
                if (!NativeWindow.IsForeground(target)) NativeWindow.Focus(target);
                NativeWindow.Key(target, 0x08);
                var expected = RemoveLastTextElement(before);
                PublishInputProbe(expected, authoritative: true, sourceOverride: "backspace-dispatch");
                return CommandResult.Ok("已删除输入框中的一个字符。", new { state = "Dispatched", text = expected });
            }
            if (action == "input")
            {
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
            // Electron may enable accessibility lazily on the first query.
            List<AutomationElement> elements = [];
            for (int i = 0; i < 20; i++)
            {
                elements = Descendants(root);
                if (elements.Any(e => e.Cached.ControlType == ControlType.Document || e.Cached.ControlType == ControlType.Edit)) break;
                Thread.Sleep(100);
            }
            if (action == "inspect") return CommandResult.Ok("UIA 诊断", elements.Select(Summarize).ToArray());
            if (action == "status") return CommandResult.Ok("ZCode 窗口已连接", new
            {
                running = true,
                focused = windows.Any(NativeWindow.IsForeground),
                windows = windows.Select(w => new { handle = w.Handle.ToInt64(), w.ProcessId, w.Title }),
                composer = FindComposer(elements, false) is not null,
                canSend = Buttons(elements, ["发送", "Send", "加入队列", "Queue"]).Any(e => e.Cached.IsEnabled),
                canStop = Buttons(elements, ["停止生成", "Stop"]).Any(e => e.Cached.IsEnabled),
                confirmation = FindConfirmation(elements) is not null
            });
            NativeWindow.Focus(target);
            if (action.StartsWith("confirm.", StringComparison.Ordinal)) return Confirm(action, target, elements);
            if (action != "stop" && action != "backspace" && FindConfirmation(elements) is not null)
                return CommandResult.Fail("ConfirmationPending", "当前存在确认卡片，请使用 confirm 命令处理，避免操作被遮挡的输入区域。");
            switch (action)
            {
                case "send":
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
    private static string ResolveExecutable(IReadOnlyDictionary<string,string> args)
    {
        var candidates = args.TryGetValue("exe", out var path) ? new[] { Path.GetFullPath(path) } : new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ZCode", "ZCode.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ZCode", "ZCode.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new ControlException("AppNotInstalled", "未找到 ZCode.exe，请使用 --exe 指定安装路径。");
    }
    private static CommandResult Open(string exe, List<WindowTarget> windows, IReadOnlyDictionary<string,string> args)
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
                NativeWindow.Focus(candidate);
                Thread.Sleep(200); // let Electron settle, then confirm it kept the foreground
                activated = candidate;
            }
            catch (ControlException e) { failure = e; }
        }
        if (activated is not null && NativeWindow.IsForeground(activated))
            return CommandResult.Ok("ZCode 已在前台显示。", new { handle = activated.Handle.ToInt64(), activated.ProcessId });
        throw failure ?? new ControlException("FocusDenied", "Windows 未允许切到 ZCode 前台。");
    }
    private CommandResult Input(WindowTarget target, AutomationElement composer, IReadOnlyDictionary<string,string> args)
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
                NativeWindow.Key(target, replace ? (ushort)0x41 : (ushort)0x23, 0x11); // Ctrl+A / Ctrl+End
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
        if (replace && text.Length == 0) NativeWindow.Key(target, 0x08);
        else
        {
            try { NativeWindow.Text(target, text, CheckFocus); }
            catch (ControlException exception) when (
                exception.Code == "TargetChanged" &&
                initiallyEmpty && text.Length <= 32 && !text.Contains('\n') && !text.Contains('\r'))
            {
                // A short first utterance is one atomic SendInput chunk. If
                // Text reports TargetChanged, it failed before that sole chunk
                // was sent, so one focus recovery retry cannot duplicate text.
                FocusComposerStable(target, composer, 4);
                NativeWindow.Key(target, 0x23, 0x11); // Ctrl+End
                NativeWindow.Text(target, text, CheckFocus);
            }
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
    private CommandResult PublishInputProbe(string text, bool authoritative = false, string? sourceOverride = null)
    {
        InputProbeSnapshot snapshot;
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
                }
                else
                {
                    // A longer or divergent value is a genuine subsequent
                    // edit, including typing performed outside this plugin.
                    _lastVerifiedInputText = null;
                    _supersededProbeHashes.Clear();
                }
            }
            if (!string.Equals(_lastProbedText, text, StringComparison.Ordinal))
            {
                _lastProbedText = text;
                _inputProbeRevision++;
            }
            snapshot = new(true, text, _inputProbeRevision, DateTimeOffset.UtcNow, source);
        }
        return CommandResult.Ok("输入探针已采样。", snapshot);
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
                true, _lastProbedText, _inputProbeRevision, DateTimeOffset.UtcNow, "write-in-progress");
            result = CommandResult.Ok("输入框正在写入，返回最近一次完整快照。", snapshot);
            return true;
        }
    }
    private string? ReadCachedProbeText()
    {
        lock (_inputProbeSync) return _lastProbedText;
    }
    private static string RemoveLastTextElement(string text)
    {
        if (text.Length == 0) return text;
        var starts = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        return starts.Length == 0 ? string.Empty : text[..starts[^1]];
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
    private static WindowTarget SelectComposerWindow(List<WindowTarget> windows, IReadOnlyDictionary<string,string> args)
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
        // ZCode's Electron contenteditable exposes the freshly rendered draft
        // through descendant Text nodes immediately, while the enclosing
        // TextPattern.DocumentRange is refreshed one edit later whenever the
        // fullscreen window owns the foreground. Prefer the rendered nodes so
        // probes and post-write verification observe the same current frame.
        var rendered = element.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>()
            .Select(child => child.Current.Name)
            .Where(value => !string.IsNullOrEmpty(value))
            .ToArray();
        if (rendered.Length > 0) return string.Concat(rendered);
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value;
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
    // Same matching rules as FindComposer, but pushes the ControlType/IsOffscreen
    // filtering down to the provider so a poll never materializes the whole tree.
    private static AutomationElement? FindComposerFast(AutomationElement root)
    {
        var cache = new CacheRequest();
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        using (cache.Activate())
        {
            AutomationElement? match = null;
            var edits = root.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
            foreach (AutomationElement candidate in edits)
            {
                if (!candidate.Cached.IsEnabled ||
                    !ComposerNames.Any(name => candidate.Cached.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))) continue;
                if (match is not null) return null;
                match = candidate;
            }
            return match;
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
    public void Dispose() { }
}
