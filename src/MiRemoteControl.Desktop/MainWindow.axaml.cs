using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MiRemoteControl.Client;
using MiRemoteControl.Contracts;
using MiRemoteControl.Desktop.Infrastructure;

namespace MiRemoteControl.Desktop;

public partial class MainWindow : Window
{
    private static readonly IBrush BatteryHealthyBrush = new SolidColorBrush(Color.Parse("#38C956"));
    private static readonly IBrush BatteryMediumBrush = new SolidColorBrush(Color.Parse("#F0AE45"));
    private static readonly IBrush BatteryLowBrush = new SolidColorBrush(Color.Parse("#EB6464"));
    private static readonly HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly CoreClient _client = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly EnhancedRemoteKeysController _enhancedRemoteKeys = new();
    private readonly GlobalEscapeMonitor _escapeMonitor;
    private readonly Dictionary<string, ToggleButton> _buttons;
    private readonly Dictionary<string, bool> _voiceModelLoadedFlags = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;
    private bool _loading;
    private string _pluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
    private string _targetPluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins", "targets");
    private string _remotePluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins", "remotes");
    private string _selectedPluginId = "";
    private string _selectedPluginName = "目标插件";
    private string _selectedRemotePluginId = "";
    private readonly HashSet<string> _loadedTargetPluginIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedTargetActions = new(StringComparer.OrdinalIgnoreCase);
    // Package icons are keyed by host process id + plugin id: a host restart
    // rescans packages, so its icons must not leak into the new session.
    private readonly Dictionary<string, IImage> _pluginIconImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pluginIconFetching = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pluginIconUnavailable = new(StringComparer.OrdinalIgnoreCase);
    private string _hostProcessKey = "";
    private TextBlock? _selectedPluginStatus;
    private string _targetPluginsSignature = "";
    private bool _pluginPowerBusy;
    private DateTimeOffset _lastPowerFeedbackAt;
    private DateTimeOffset _lastHomeEventAt;
    private bool _homeEventsInitialized;
    private bool _homeRemoteHeld;
    private DateTimeOffset _lastTvEventAt;
    private bool _tvEventsInitialized;
    private bool _tvRemoteHeld;
    private DateTimeOffset _lastPowerCloseEventAt;
    private bool _powerCloseEventsInitialized;
    private bool _powerCloseRemoteHeld;
    private DateTimeOffset _lastMenuEventAt;
    private bool _menuEventsInitialized;
    private bool _menuRemoteHeld;
    private bool _pluginSwitchBusy;
    // Working-plugin card order as shown in the main window; the remote Menu
    // and Up/Down keys move through it so "next" matches what the user sees.
    private readonly List<string> _orderedTargetPluginIds = new();
    private readonly Dictionary<string, DateTimeOffset> _cursorEventAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _cursorMovedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cursorRemoteHeld = new(StringComparer.OrdinalIgnoreCase);
    private bool _cursorEventsInitialized;
    private CancellationTokenSource? _remoteEventCancellation;
    private BigScreenTextWindow? _bigScreenTextWindow;
    private string? _bigScreenProbePluginId;
    private long _bigScreenProbeRevision = -1;
    private string? _bigScreenProbeText;
    private int? _bigScreenProbeCaretIndex;
    private CancellationTokenSource? _bigScreenProbeCancellation;
    private CancellationTokenSource? _bigScreenEditCancellation;
    private Task _bigScreenEditTask = Task.CompletedTask;
    private readonly SemaphoreSlim _bigScreenSyncGate = new(1, 1);
    private bool _bigScreenLocalDirty;
    private int _bigScreenEditGeneration;
    private bool _openingBigScreen;
    private DispatcherTimer? _bigScreenBackRepeatTimer;
    private CancellationTokenSource? _bigScreenCaretSyncCancellation;
    private VoicePromptWindow? _voicePromptWindow;
    private int _powerFlashGeneration;
    private string _voiceModelDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiRemoteControl", "models");
    private string? _selectedVoiceModelId;
    private string _voiceModelsSignature = "";
    private bool _updatingVoiceModels;
    private bool _voiceModelSwitching;
    private bool _latestVoicePlaybackBusy;
    private bool _changingSettings;

    public MainWindow()
    {
        InitializeComponent();
        _escapeMonitor = new GlobalEscapeMonitor(HandleGlobalEscape);
        DayNightSwitch.IsChecked = (Application.Current as App)?.IsDarkTheme ?? false;
        StartWithWindowsSwitch.IsChecked = StartupRegistrationService.IsEnabled();
        _buttons = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Voice"] = VoiceButton,
            ["Up"] = UpButton,
            ["Down"] = DownButton,
            ["Left"] = LeftButton,
            ["Right"] = RightButton,
            ["Ok"] = OkButton,
            ["Back"] = BackButton,
            ["Home"] = HomeButton,
            ["Menu"] = MenuButton,
            ["Tv"] = TvButton,
            ["VolumeUp"] = VolumeUpButton,
            ["VolumeDown"] = VolumeDownButton
        };

        Opened += OnWindowOpened;
        // Tunnel the raw arrow/Enter keys before inner controls see them while
        // the studio owns the keyboard focus — see OnStudioKeyDown.
        AddHandler(KeyDownEvent, OnStudioKeyDown, RoutingStrategies.Tunnel);
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty) UpdateWindowStateVisuals();
        };
        _timer.Tick += async (_, _) => await LoadState();
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        // Rider's preview also opens this window; it must not own the real
        // remote service, consume button events, or launch the HID helper.
        if (Design.IsDesignMode || _started) return;
        _started = true;
        try
        {
            var start = await _client.StartForDesktopAsync(Environment.ProcessId);
            if (!start.Success)
            {
                SetRemoteConnectionState(false, start.Message);
                VoiceTranslationStatus.Text = start.Message;
                ClearTargetPluginPanel();
                return;
            }
        }
        catch (Exception exception)
        {
            SetRemoteConnectionState(false, OperatingSystem.IsWindows() ? "后台启动失败" : "当前平台尚无遥控后台");
            VoiceTranslationStatus.Text = exception.Message;
        }
        // Back/VolumeUp/VolumeDown are dropped by the Windows keyboard stack,
        // so the privileged HID tap is the only source for them — start it
        // with the window. Failures are logged to hid-tap.log by the controller.
        await _enhancedRemoteKeys.StartAsync();
        await LoadState();
        _timer.Start();
        _remoteEventCancellation = new CancellationTokenSource();
        _ = RunRemoteEventLoopAsync(_remoteEventCancellation.Token);
    }

    private void ThemeSwitchChanged(object? sender, RoutedEventArgs e)
    {
        var isNight = DayNightSwitch.IsChecked == true;
        if (Application.Current is App app) app.SetTheme(isNight);
    }

    private void StartWithWindowsChanged(object? sender, RoutedEventArgs e)
    {
        if (_changingSettings) return;
        var enabled = StartWithWindowsSwitch.IsChecked == true;
        try
        {
            StartupRegistrationService.SetEnabled(enabled);
            VoiceTranslationStatus.Text = enabled ? "已启用随系统启动" : "已关闭随系统启动";
        }
        catch (Exception exception)
        {
            _changingSettings = true;
            StartWithWindowsSwitch.IsChecked = StartupRegistrationService.IsEnabled();
            _changingSettings = false;
            VoiceTranslationStatus.Text = $"启动项设置失败：{exception.Message}";
        }
    }

    private async void AboutMenuClick(object? sender, RoutedEventArgs e)
    {
        var about = new AboutWindow();
        await about.ShowDialog(this);
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (Application.Current is App { ExitRequested: true }) return;
        e.Cancel = true;
        ShowInTaskbar = false;
        Hide();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _remoteEventCancellation?.Cancel();
        _remoteEventCancellation?.Dispose();
        _remoteEventCancellation = null;
        _escapeMonitor.Dispose();
        _enhancedRemoteKeys.Dispose();
        _voicePromptWindow?.Close();
        _voicePromptWindow = null;
        _bigScreenProbeCancellation?.Cancel();
        _bigScreenProbeCancellation?.Dispose();
        _bigScreenProbeCancellation = null;
        _bigScreenEditCancellation?.Cancel();
        _bigScreenEditCancellation?.Dispose();
        _bigScreenEditCancellation = null;
        _bigScreenTextWindow?.Close();
        _bigScreenTextWindow = null;
    }

    private async Task LoadState()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var result = await _client.InvokeAsync("core", "status", timeoutMs: 1000);
            if (!result.Success)
            {
                SetRemoteConnectionState(false, result.Message);
                VoiceTranslationStatus.Text = result.Message;
                ResetRemoteBattery("后台未连接，暂时无法读取遥控器电量");
                ClearTargetPluginPanel();
                return;
            }

            var state = (JsonElement)result.Data!;
            SetRemoteConnectionState(true);
            var remote = state.GetProperty("remote");
            var pressed = remote.GetProperty("pressedButtons").EnumerateArray()
                .Select(value => value.GetString() ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var lastInput = remote.GetProperty("lastInput");
            string? recentButton = null;
            if (lastInput.ValueKind != JsonValueKind.Null)
            {
                var button = lastInput.GetProperty("button").GetString() ?? "Unknown";
                var occurredAt = lastInput.GetProperty("occurredAt").GetDateTimeOffset();
                if (DateTimeOffset.Now - occurredAt < TimeSpan.FromMilliseconds(320)) recentButton = button;
                if (string.Equals(button, "Power", StringComparison.OrdinalIgnoreCase) && occurredAt > _lastPowerFeedbackAt)
                {
                    _lastPowerFeedbackAt = occurredAt;
                    FlashPowerButton();
                }
            }

            foreach (var pair in _buttons)
                pair.Value.IsChecked = pressed.Contains(pair.Key) ||
                                       string.Equals(recentButton, pair.Key, StringComparison.OrdinalIgnoreCase);

            var atvv = state.GetProperty("remoteVoice");
            UpdateRemoteBattery(atvv);
            var voice = state.GetProperty("voice");
            string? speechState = null;
            if (state.TryGetProperty("speechModel", out var speech) || state.TryGetProperty("whisper", out speech))
            {
                speechState = speech.GetProperty("state").GetString();
                VoiceTranslationStatus.Text = speechState ?? "等待语音";
                if (speech.GetProperty("lastText").GetString() is { Length: > 0 } speechText)
                    SetVoiceResultText(speechText);
            }
            else
            {
                VoiceTranslationStatus.Text = voice.GetProperty("available").GetBoolean()
                    ? voice.GetProperty("listening").GetBoolean() ? "正在识别" : "等待语音"
                    : "语音服务不可用";
                if (voice.GetProperty("lastText").GetString() is { Length: > 0 } text) SetVoiceResultText(text);
            }

            var streaming = atvv.GetProperty("streaming").GetBoolean();
            if (streaming) VoiceTranslationStatus.Text = "正在聆听";
            // The recognition prompt lives in a dedicated corner overlay while
            // the remote streams audio or the recognizer is still running.
            UpdateVoicePrompt(streaming || (speechState?.Contains("正在识别") ?? false));
            UpdateLatestVoicePlayback(state);
            UpdateVoiceModels(state);
            UpdatePluginPanel(state);
        }
        catch (Exception exception)
        {
            SetRemoteConnectionState(false, exception.Message);
            VoiceTranslationStatus.Text = exception.Message;
            ResetRemoteBattery("暂时无法读取遥控器电量");
            ClearTargetPluginPanel();
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateVoicePrompt(bool visible)
    {
        if (!visible)
        {
            _voicePromptWindow?.Hide();
            return;
        }

        if (_voicePromptWindow is null) _voicePromptWindow = new VoicePromptWindow();
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            _voicePromptWindow.Position = new PixelPoint(
                area.X + area.Width - (int)_voicePromptWindow.Width - 24,
                area.Y + area.Height - (int)_voicePromptWindow.Height - 24);
        }
        if (!_voicePromptWindow.IsVisible) _voicePromptWindow.Show();
    }

    private void UpdateRemoteBattery(JsonElement remoteVoice)
    {
        if (!remoteVoice.TryGetProperty("batteryLevel", out var batteryValue) ||
            batteryValue.ValueKind != JsonValueKind.Number ||
            !batteryValue.TryGetInt32(out var level))
        {
            var message = remoteVoice.TryGetProperty("batteryError", out var errorValue) &&
                          errorValue.ValueKind == JsonValueKind.String
                ? errorValue.GetString()
                : null;
            ResetRemoteBattery(string.IsNullOrWhiteSpace(message) ? "唤醒遥控器后将自动读取电量" : message);
            return;
        }

        level = Math.Clamp(level, 0, 100);
        RemoteBatteryText.Text = level.ToString();
        RemoteBatteryFill.Width = 46 * level / 100d;
        RemoteBatteryFill.Background = level <= 20
            ? BatteryLowBrush
            : level <= 40
                ? BatteryMediumBrush
                : BatteryHealthyBrush;

        var updated = remoteVoice.TryGetProperty("batteryUpdatedAt", out var updatedValue) &&
                      updatedValue.ValueKind == JsonValueKind.String &&
                      updatedValue.TryGetDateTimeOffset(out var updatedAt)
            ? $" · 更新于 {FormatBatteryUpdatedAt(updatedAt)}"
            : string.Empty;
        ToolTip.SetTip(RemoteBatteryBadge, $"遥控器剩余电量 {level}%{updated}");
    }

    private static string FormatBatteryUpdatedAt(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("M月d日 HH:mm");
    }

    private void ResetRemoteBattery(string? hint)
    {
        RemoteBatteryText.Text = "--";
        RemoteBatteryFill.Width = 0;
        ToolTip.SetTip(RemoteBatteryBadge, hint ?? "遥控器电量暂不可用");
    }

    private void SetRemoteConnectionState(bool connected, string? hint = null)
    {
        RemoteControlVisual.Opacity = connected ? 1 : 0.6;
        RemoteControlVisual.IsHitTestVisible = connected;
        ToolTip.SetTip(RemoteControlVisual, hint ?? (connected ? "遥控器已连接" : "遥控器未连接"));
    }

    private void UpdateLatestVoicePlayback(JsonElement state)
    {
        if (!state.TryGetProperty("latestVoice", out var latest) || latest.ValueKind != JsonValueKind.Object)
        {
            LatestVoicePlaybackButton.IsEnabled = false;
            LatestVoicePlayIcon.IsVisible = true;
            LatestVoicePauseIcon.IsVisible = false;
            ToolTip.SetTip(LatestVoicePlaybackButton, "暂无上一条语音");
            return;
        }

        var available = latest.TryGetProperty("available", out var availableValue) && availableValue.GetBoolean();
        var playing = latest.TryGetProperty("playing", out var playingValue) && playingValue.GetBoolean();
        var paused = latest.TryGetProperty("paused", out var pausedValue) && pausedValue.GetBoolean();
        LatestVoicePlaybackButton.IsEnabled = available && !_latestVoicePlaybackBusy;
        LatestVoicePlayIcon.IsVisible = !playing;
        LatestVoicePauseIcon.IsVisible = playing;
        ToolTip.SetTip(LatestVoicePlaybackButton,
            !available ? "暂无上一条语音" : playing ? "暂停上一条语音" : paused ? "继续播放上一条语音" : "播放上一条语音");
    }

    private async void ToggleLatestVoicePlayback(object? sender, RoutedEventArgs e)
    {
        if (_latestVoicePlaybackBusy) return;
        _latestVoicePlaybackBusy = true;
        LatestVoicePlaybackButton.IsEnabled = false;
        try
        {
            var result = await _client.InvokeAsync("core", "voice.latest.toggle", timeoutMs: 5000);
            if (!result.Success) VoiceTranslationStatus.Text = result.Message;
        }
        catch (Exception exception)
        {
            VoiceTranslationStatus.Text = exception.Message;
        }
        finally
        {
            _latestVoicePlaybackBusy = false;
            await LoadState();
        }
    }

    private void UpdatePluginPanel(JsonElement state)
    {
        if (state.TryGetProperty("selectedPlugin", out var selectedPlugin) &&
            selectedPlugin.GetString() is { Length: > 0 } selectedId)
            _selectedPluginId = selectedId;

        if (state.TryGetProperty("pluginDirectory", out var directory) &&
            directory.GetString() is { Length: > 0 } path)
        {
            _pluginDirectory = path;
            _targetPluginDirectory = Path.Combine(path, "targets");
            _remotePluginDirectory = Path.Combine(path, "remotes");
        }
        if (state.TryGetProperty("targetPluginDirectory", out var targetDirectory) &&
            targetDirectory.GetString() is { Length: > 0 } targetPath)
            _targetPluginDirectory = targetPath;
        if (state.TryGetProperty("remotePluginDirectory", out var remoteDirectory) &&
            remoteDirectory.GetString() is { Length: > 0 } remotePath)
            _remotePluginDirectory = remotePath;
        if (state.TryGetProperty("selectedRemotePlugin", out var selectedRemote) &&
            selectedRemote.GetString() is { Length: > 0 } selectedRemoteId)
            _selectedRemotePluginId = selectedRemoteId;

        if (state.TryGetProperty("processId", out var processIdValue) &&
            processIdValue.ValueKind == JsonValueKind.Number &&
            processIdValue.TryGetInt32(out var processId))
        {
            var hostKey = processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (hostKey != _hostProcessKey)
            {
                _hostProcessKey = hostKey;
                _pluginIconUnavailable.Clear();
            }
        }

        var targets = new List<TargetPluginView>();
        _loadedTargetPluginIds.Clear();
        _selectedTargetActions.Clear();
        _orderedTargetPluginIds.Clear();
        if (state.TryGetProperty("plugins", out var plugins))
            foreach (var plugin in plugins.EnumerateArray())
            {
                var pluginId = plugin.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(pluginId) ||
                    (plugin.TryGetProperty("kind", out var kind) && kind.GetString() != PluginKinds.Target)) continue;
                _loadedTargetPluginIds.Add(pluginId);
                _orderedTargetPluginIds.Add(pluginId);
                var name = plugin.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? pluginId : pluginId;
                var version = plugin.TryGetProperty("version", out var versionValue) ? versionValue.GetString() ?? "?" : "?";
                var actions = plugin.TryGetProperty("actions", out var actionValues)
                    ? actionValues.EnumerateArray()
                        .Select(action => action.TryGetProperty("id", out var id) ? id.GetString() : null)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Select(id => id!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var iconKey = _hostProcessKey + ":" + pluginId;
                _pluginIconImages.TryGetValue(iconKey, out var icon);
                if (icon is null && !_pluginIconUnavailable.Contains(iconKey) && _pluginIconFetching.Add(iconKey))
                    _ = FetchPluginIconAsync(iconKey, pluginId);
                targets.Add(new TargetPluginView(pluginId, name, version, actions, icon));
                if (string.Equals(pluginId, _selectedPluginId, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedPluginName = name;
                    _selectedTargetActions.UnionWith(actions);
                }
            }

        RenderTargetPluginCards(targets);
    }

    private async Task FetchPluginIconAsync(string cacheKey, string pluginId)
    {
        try
        {
            var result = await _client.InvokeAsync("core", "plugin.icon", new() { ["plugin"] = pluginId }, timeoutMs: 3000);
            if (result.Success &&
                result.Data is JsonElement { ValueKind: JsonValueKind.Object } data &&
                data.TryGetProperty("icon", out var iconValue) &&
                iconValue.ValueKind == JsonValueKind.String)
            {
                if (PluginIconView.Decode(Convert.FromBase64String(iconValue.GetString() ?? "")) is { } image)
                {
                    _pluginIconImages[cacheKey] = image;
                    // The next status poll re-renders the cards with the icon.
                    _targetPluginsSignature = "";
                    return;
                }
            }
            // A definitive "no icon" answer stops retrying; transport failures
            // (host down, timeout) stay retryable on a later poll.
            if (result.Success || result.Code is "PluginNotFound" or "PluginIconMissing")
                _pluginIconUnavailable.Add(cacheKey);
        }
        catch
        {
            // Transport failure — keep the key retryable.
        }
        finally
        {
            _pluginIconFetching.Remove(cacheKey);
        }
    }

    private void RenderTargetPluginCards(IReadOnlyList<TargetPluginView> targets)
    {
        var signature = _selectedPluginId + "|" + string.Join('|', targets.Select(target =>
            $"{target.Id}:{target.Name}:{target.Version}:{target.Icon is not null}:{string.Join(',', target.Actions.Order())}"));
        if (signature == _targetPluginsSignature) return;
        _targetPluginsSignature = signature;
        _selectedPluginStatus = null;
        TargetPluginCardsPanel.Children.Clear();
        PluginCountText.Text = targets.Count == 0 ? "暂无插件" : $"{targets.Count} 个已加载";
        if (targets.Count == 0)
        {
            TargetPluginCardsPanel.Children.Add(new TextBlock
            {
                Text = "未发现目标插件",
                Foreground = ThemeBrush("Theme.TextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 22)
            });
            PowerButton.IsEnabled = false;
            return;
        }

        foreach (var target in targets)
        {
            var selected = target.Id.Equals(_selectedPluginId, StringComparison.OrdinalIgnoreCase);
            var status = new TextBlock
            {
                Text = $"{target.Id} · v{target.Version}",
                FontSize = 12,
                Foreground = ThemeBrush("Theme.TextMuted"),
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (selected) _selectedPluginStatus = status;
            var icon = PluginIconView.Create(target.Icon, target.Name);
            var labels = new StackPanel { Margin = new Thickness(2, 0, 12, 0) };
            labels.Children.Add(new TextBlock { Text = target.Name, FontSize = 15, FontWeight = FontWeight.SemiBold });
            labels.Children.Add(status);
            var selector = new RadioButton
            {
                GroupName = "WorkingPlugin",
                Tag = target.Id,
                IsChecked = selected,
                Content = "工作插件",
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            selector.Click += SelectPlugin;
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("42,*,Auto") };
            header.Children.Add(icon);
            Grid.SetColumn(labels, 1); header.Children.Add(labels);
            Grid.SetColumn(selector, 2); header.Children.Add(selector);
            var content = new StackPanel();
            content.Children.Add(header);
            var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(42, 14, 0, 0), Spacing = 8 };
            AddTargetActionButton(actionPanel, target, HarnessPluginActions.Open, "打开");
            AddTargetActionButton(actionPanel, target, HarnessPluginActions.Status, "读取状态");
            AddTargetActionButton(actionPanel, target, HarnessPluginActions.Stop, "停止任务");
            if (actionPanel.Children.Count > 0) content.Children.Add(actionPanel);
            TargetPluginCardsPanel.Children.Add(new Border
            {
                Background = ThemeBrush(selected ? "Theme.SurfaceSelected" : "Theme.Surface"),
                BorderBrush = ThemeBrush(selected ? "Theme.AccentBorder" : "Theme.Border"),
                BorderThickness = new Thickness(selected ? 1.2 : 1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(16),
                Child = content
            });
        }
        PowerButton.IsEnabled = SelectedPluginSupportsPower && !_pluginPowerBusy;
        ToolTip.SetTip(PowerButton, $"启动或关闭当前工作插件：{_selectedPluginName}");
    }

    private void AddTargetActionButton(Panel panel, TargetPluginView target, string action, string label)
    {
        if (!target.Actions.Contains(action)) return;
        var button = new Button { Content = label, Tag = new TargetActionRequest(target.Id, action), Classes = { "compact" } };
        button.Click += InvokeTargetAction;
        panel.Children.Add(button);
    }

    private IBrush? ThemeBrush(string key) =>
        Resources.TryGetResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    private void ClearTargetPluginPanel()
    {
        _loadedTargetPluginIds.Clear();
        _selectedTargetActions.Clear();
        _orderedTargetPluginIds.Clear();
        _targetPluginsSignature = "";
        RenderTargetPluginCards([]);
    }

    private void UpdateVoiceModels(JsonElement state)
    {
        if (state.TryGetProperty("speechModel", out var speech) || state.TryGetProperty("whisper", out speech))
        {
            if (speech.TryGetProperty("modelDirectory", out var directory) &&
                directory.GetString() is { Length: > 0 } path) _voiceModelDirectory = path;
            _selectedVoiceModelId = speech.TryGetProperty("selectedModel", out var selected) ? selected.GetString() : null;
        }
        if (!state.TryGetProperty("voiceModels", out var models) || models.ValueKind != JsonValueKind.Array) return;

        var entries = models.EnumerateArray().ToArray();
        var signature = (_selectedVoiceModelId ?? "") + "|" + string.Join("|", entries.Select(model =>
            $"{model.GetProperty("id").GetString()}:{model.GetProperty("sizeBytes").GetInt64()}:" +
            $"{model.GetProperty("modifiedAt").GetDateTimeOffset().Ticks}:{model.GetProperty("compatible").GetBoolean()}:" +
            $"{(model.TryGetProperty("engine", out var engine) ? engine.GetString() : "")}:" +
            $"{(model.TryGetProperty("format", out var format) ? format.GetString() : "")}"));
        if (signature == _voiceModelsSignature) return;
        _voiceModelsSignature = signature;
        _updatingVoiceModels = true;
        try
        {
            _voiceModelLoadedFlags.Clear();
            var items = new List<ComboBoxItem>();
            ComboBoxItem? selectedItem = null;
            var compatibleCount = 0;
            foreach (var model in entries)
            {
                var id = model.GetProperty("id").GetString() ?? "";
                var name = model.GetProperty("name").GetString() ?? id;
                var size = model.GetProperty("sizeBytes").GetInt64();
                var format = model.TryGetProperty("format", out var modelFormat) ? modelFormat.GetString() : null;
                var compatible = model.GetProperty("compatible").GetBoolean();
                var loaded = model.GetProperty("loaded").GetBoolean();
                _voiceModelLoadedFlags[id] = loaded;
                var item = new ComboBoxItem
                {
                    Tag = id,
                    Content = $"{name}{(string.IsNullOrWhiteSpace(format) ? "" : $" · {format}")} · " +
                              $"{FormatFileSize(size)}{(compatible ? "" : " · 不兼容")}",
                    IsEnabled = compatible
                };
                ToolTip.SetTip(item, compatible
                    ? model.GetProperty("path").GetString()
                    : model.TryGetProperty("compatibilityMessage", out var reason)
                        ? reason.GetString()
                        : "模型格式不受支持");
                items.Add(item);
                if (compatible) compatibleCount++;
                if (loaded || string.Equals(id, _selectedVoiceModelId, StringComparison.OrdinalIgnoreCase))
                    selectedItem = item;
            }

            if (entries.Length == 0) items.Add(new ComboBoxItem { Content = "未发现模型文件", IsEnabled = false });
            VoiceModelSelector.ItemsSource = items;
            VoiceModelSelector.SelectedItem = selectedItem ?? items.FirstOrDefault(item => item.IsEnabled);
            VoiceModelSelector.IsEnabled = compatibleCount > 0 && !_voiceModelSwitching;
        }
        finally
        {
            _updatingVoiceModels = false;
        }
    }

    private async void VoiceModelSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingVoiceModels || _voiceModelSwitching ||
            VoiceModelSelector.SelectedItem is not ComboBoxItem { Tag: string modelId, IsEnabled: true } ||
            (string.Equals(modelId, _selectedVoiceModelId, StringComparison.OrdinalIgnoreCase) &&
             _voiceModelLoadedFlags.TryGetValue(modelId, out var loaded) && loaded)) return;

        _voiceModelSwitching = true;
        VoiceModelSelector.IsEnabled = false;
        VoiceTranslationStatus.Text = "正在切换语音模型…";
        try
        {
            var result = await _client.InvokeAsync("core", "voice.model.select", new() { ["model"] = modelId }, timeoutMs: 300000);
            VoiceTranslationStatus.Text = result.Message;
            if (result.Success) _selectedVoiceModelId = modelId;
        }
        catch (Exception exception)
        {
            VoiceTranslationStatus.Text = exception.Message;
        }
        finally
        {
            _voiceModelSwitching = false;
            _voiceModelsSignature = "";
            await LoadState();
        }
    }

    private void OpenVoiceModelFolder(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_voiceModelDirectory);
        OpenPath(_voiceModelDirectory);
    }

    private async void OpenModelManager(object? sender, RoutedEventArgs e)
    {
        var manager = new ModelManagerWindow(_voiceModelDirectory, _selectedVoiceModelId);
        await manager.ShowDialog(this);
        await _client.InvokeAsync("core", "voice.models", timeoutMs: 5000);
        _voiceModelsSignature = "";
        await LoadState();
    }

    private async void OpenPluginManager(object? sender, RoutedEventArgs e)
    {
        var manager = new PluginManagerWindow(_targetPluginDirectory);
        await manager.ShowDialog(this);
        await LoadState();
    }

    private async void OpenRemotePluginManager(object? sender, RoutedEventArgs e)
    {
        var manager = new RemotePluginManagerWindow(
            _remotePluginDirectory,
            _selectedRemotePluginId,
            async pluginId =>
            {
                var result = await _client.InvokeAsync("core", "remote.driver.select", new() { ["plugin"] = pluginId }, timeoutMs: 3000);
                return (result.Success, result.Message);
            });
        await manager.ShowDialog(this);
        await LoadState();
    }

    private async void CheckAppUpdate(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control) control.IsEnabled = false;
        VoiceTranslationStatus.Text = "正在检查更新…";
        var result = await CheckUpdateManifestAsync("https://download.cheems.cn/v1/manifests/app/stable.json");
        VoiceTranslationStatus.Text = result;
        if (sender is Control button) button.IsEnabled = true;
        await ShowUpdateResultAsync(result);
    }

    private static async Task<string> CheckUpdateManifestAsync(string url)
    {
        try
        {
            using var response = await UpdateHttp.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            if (!root.TryGetProperty("available", out var available) || !available.GetBoolean()) return "暂无更新内容。";
            var version = root.TryGetProperty("version", out var value) ? value.GetString() : null;
            return string.IsNullOrWhiteSpace(version) ? "发现可用更新。" : $"发现新版本 {version}。";
        }
        catch (Exception exception) { return $"检查更新失败：{exception.Message}"; }
    }

    private async Task ShowUpdateResultAsync(string message)
    {
        var dialog = new Window
        {
            Title = "检查更新",
            Width = 380,
            Height = 176,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ok = new Button
        {
            Content = "确定",
            MinWidth = 84,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        ok.Click += (_, _) => dialog.Close();
        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 20 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        panel.Children.Add(ok);
        dialog.Content = panel;
        await dialog.ShowDialog(this);
    }

    private static string FormatFileSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.0} GB"
        : $"{bytes / (1024d * 1024):0} MB";

    private void OpenPluginFolder(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_pluginDirectory);
        OpenPath(_pluginDirectory);
    }

    private async void SelectPlugin(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string pluginId }) return;
        try
        {
            var result = await _client.InvokeAsync("core", "remote.select", new() { ["plugin"] = pluginId }, timeoutMs: 3000);
            if (!result.Success)
            {
                SetSelectedPluginStatus(result.Message);
                return;
            }
            _selectedPluginId = pluginId;
            await LoadState();
            ToolTip.SetTip(PowerButton, $"启动或关闭当前工作插件：{_selectedPluginName}");
            await RefreshSelectedPluginPowerStateAsync(updateStatusText: false);
        }
        catch (Exception exception)
        {
            SetSelectedPluginStatus(exception.Message);
        }
    }

    private async void InvokeTargetAction(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: TargetActionRequest request }) return;
        SetSelectedPluginStatus("正在执行…");
        try
        {
            if (request.Action == HarnessPluginActions.Open) await _client.AllowForegroundAsync();
            var result = await _client.InvokeAsync(request.PluginId, request.Action);
            SetSelectedPluginStatus(result.Message);
            if (result.Success && request.Action == HarnessPluginActions.Open) FlashPowerButton();
        }
        catch (Exception exception)
        {
            SetSelectedPluginStatus(exception.Message);
        }
    }

    private void SetSelectedPluginStatus(string message)
    {
        if (_selectedPluginStatus is not null) _selectedPluginStatus.Text = message;
        else VoiceTranslationStatus.Text = message;
    }

    private async void PowerButtonClick(object? sender, RoutedEventArgs e) => await RunStudioPowerActionAsync();

    /// <summary>
    /// Presses the virtual Power key on the studio's own remote mock: the
    /// backend opens the selected plugin's target window (or closes it when
    /// that target already owns the foreground). Used by the on-screen power
    /// button and by the remote's Ok key while the studio is up front.
    /// </summary>
    private async Task RunStudioPowerActionAsync()
    {
        if (_pluginPowerBusy) return;
        if (!SelectedPluginSupportsPower)
        {
            PowerButton.IsChecked = false;
            SetSelectedPluginStatus("当前工作插件尚不可用");
            return;
        }

        _pluginPowerBusy = true;
        PowerButton.IsEnabled = false;
        SetSelectedPluginStatus("正在执行虚拟电源键…");
        try
        {
            await _client.AllowForegroundAsync();
            var result = await _client.InvokeAsync("core", "remote.press", new() { ["button"] = "Power" });
            SetSelectedPluginStatus(result.Message);
            FlashPowerButton();
        }
        catch (Exception exception)
        {
            PowerButton.IsChecked = false;
            SetSelectedPluginStatus(exception.Message);
        }
        finally
        {
            _pluginPowerBusy = false;
            PowerButton.IsEnabled = SelectedPluginSupportsPower;
        }
    }

    private async void RemoteButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string button } control) return;

        if (string.Equals(button, "Tv", StringComparison.OrdinalIgnoreCase))
        {
            control.IsChecked = false;
            ToggleBigScreenText();
            return;
        }

        control.IsEnabled = false;
        try
        {
            await _client.AllowForegroundAsync();
            var result = await _client.InvokeAsync("core", "remote.press", new() { ["button"] = button });
            if (!result.Success) SetRemoteConnectionState(false, result.Message);
        }
        catch (Exception exception)
        {
            SetRemoteConnectionState(false, $"模拟按键失败：{exception.Message}");
            control.IsChecked = false;
        }
        finally
        {
            control.IsEnabled = true;
        }
    }

    private async Task RefreshSelectedPluginPowerStateAsync(bool updateStatusText)
    {
        if (!IsVisible || !SelectedPluginSupportsPower)
        {
            return;
        }

        try
        {
            var result = await _client.InvokeAsync(_selectedPluginId, "status", timeoutMs: 4000);
            if (updateStatusText || !result.Success) SetSelectedPluginStatus(result.Message);
        }
        catch (Exception exception)
        {
            if (updateStatusText) SetSelectedPluginStatus(exception.Message);
        }
    }

    private async void FlashPowerButton()
    {
        var generation = ++_powerFlashGeneration;
        PowerButton.IsChecked = true;
        try { await Task.Delay(180); }
        catch (OperationCanceledException) { }
        if (generation == _powerFlashGeneration) PowerButton.IsChecked = false;
    }

    private void ToggleWindowFromHomeKey()
    {
        if (IsMainWindowForeground())
        {
            ShowInTaskbar = false;
            Hide();
            return;
        }

        if (Application.Current is App app) app.ShowMainWindow();
        else
        {
            ShowInTaskbar = true;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }
    }

    private bool IsMainWindowForeground()
    {
        if (!OperatingSystem.IsWindows()) return IsActive;
        var handle = TryGetPlatformHandle()?.Handle ?? 0;
        return handle != 0 && GetForegroundWindow() == handle;
    }

    private void HandleHomeFromRemote()
    {
        CloseBigScreenText();
        ToggleWindowFromHomeKey();
    }

    // The Menu key cycles the working Harness plugin forward. It only fires
    // while this window owns the foreground: a press without focus merely
    // brings the window forward (the same activation Home performs), so the
    // next press switches. That keeps one keystroke from both stealing focus
    // and silently re-pointing the remote at a different target.
    private void HandleMenuFromRemote()
    {
        if (!IsMainWindowForeground())
        {
            if (Application.Current is App app) app.ShowMainWindow();
            return;
        }
        _ = SwitchHarnessPluginAsync(1);
    }

    private async Task SwitchHarnessPluginAsync(int offset)
    {
        if (_pluginSwitchBusy) return;
        if (_orderedTargetPluginIds.Count == 0)
        {
            SetSelectedPluginStatus("没有可切换的工作插件。");
            return;
        }

        // Wrap around in both directions: past the last card in the panel the
        // order restarts at the first (and vice versa). A selected plugin that
        // is no longer listed restarts at the edge the direction comes from.
        var index = _orderedTargetPluginIds.FindIndex(id =>
            string.Equals(id, _selectedPluginId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = offset >= 0 ? -1 : 0;
        var nextId = _orderedTargetPluginIds[(index + offset + _orderedTargetPluginIds.Count) % _orderedTargetPluginIds.Count];

        _pluginSwitchBusy = true;
        try
        {
            var result = await _client.InvokeAsync("core", "remote.select", new() { ["plugin"] = nextId }, timeoutMs: 3000);
            if (!result.Success)
            {
                SetSelectedPluginStatus(result.Message);
                return;
            }
            _selectedPluginId = nextId;
            await LoadState();
            ToolTip.SetTip(PowerButton, $"启动或关闭当前工作插件：{_selectedPluginName}");
            SetSelectedPluginStatus($"已切换工作插件：{_selectedPluginName}");
            await RefreshSelectedPluginPowerStateAsync(updateStatusText: false);
        }
        catch (Exception exception)
        {
            SetSelectedPluginStatus(exception.Message);
        }
        finally
        {
            _pluginSwitchBusy = false;
        }
    }

    // While the main window owns the foreground, the remote's cursor keys
    // drive the studio itself instead of any target: Up/Down walk the working
    // plugin cards and Ok presses the virtual power key that opens (or, when
    // it is already the foreground target, closes) the selected plugin.
    private void HandleStudioNavigationInputs(string button)
    {
        if (!IsMainWindowForeground()) return;
        if (button is not ("Up" or "Down" or "Ok")) return;
        // A held key keeps reporting down events (~30/s); one physical press
        // must stay one action.
        if (!_cursorRemoteHeld.Add(button)) return;
        switch (button)
        {
            case "Up":
                _ = SwitchHarnessPluginAsync(-1);
                break;
            case "Down":
                _ = SwitchHarnessPluginAsync(1);
                break;
            default:
                _ = RunStudioPowerActionAsync();
                break;
        }
    }

    // The remote's Up/Down/Ok keys reach Windows as ordinary arrow and Enter
    // keystrokes as well. While the studio owns the keyboard focus those raw
    // keystrokes must not also drive inner controls (a bare arrow on the
    // focused voice-model combo would silently change the loaded model), so
    // they are swallowed here; the remote event loop stays the single
    // authority for what these keys do. Text editors and an open combo
    // dropdown keep their native key handling.
    private void OnStudioKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down or Key.Enter)) return;
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (FocusManager?.GetFocusedElement() is InputElement focused)
        {
            if (focused is TextBox) return;
            for (var scope = focused; scope is not null; scope = scope.Parent as InputElement)
                if (scope is ComboBox { IsDropDownOpen: true }) return;
        }
        e.Handled = true;
    }

    private bool HandleGlobalEscape()
    {
        if (_bigScreenTextWindow is null) return false;
        Dispatcher.UIThread.Post(CloseBigScreenText);
        return true;
    }

    private async Task RunRemoteEventLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _client.InvokeAsync("core", "remote.status", timeoutMs: 800, ct: ct);
                if (result.Success && result.Data is JsonElement { ValueKind: JsonValueKind.Object } state &&
                    state.TryGetProperty("remote", out var remote))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        HandleRemoteToggleInputs(remote, "Home", ref _homeEventsInitialized, ref _lastHomeEventAt,
                            ref _homeRemoteHeld, HandleHomeFromRemote);
                        HandleRemoteToggleInputs(remote, "Tv", ref _tvEventsInitialized, ref _lastTvEventAt,
                            ref _tvRemoteHeld, ToggleBigScreenFromRemote);
                        HandleRemoteToggleInputs(remote, "Power", ref _powerCloseEventsInitialized, ref _lastPowerCloseEventAt,
                            ref _powerCloseRemoteHeld, CloseBigScreenText);
                        HandleRemoteToggleInputs(remote, "Menu", ref _menuEventsInitialized, ref _lastMenuEventAt,
                            ref _menuRemoteHeld, HandleMenuFromRemote);
                        HandleRemoteCursorInputs(remote);
                    });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { /* independent background listener retries */ }

            try { await Task.Delay(70, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private static void HandleRemoteToggleInputs(JsonElement remote, string button,
        ref bool eventsInitialized, ref DateTimeOffset lastEventAt, ref bool remoteHeld, Action toggle)
    {
        if (!remote.TryGetProperty("recentInputs", out var recent) || recent.ValueKind != JsonValueKind.Array) return;
        var entries = recent.EnumerateArray()
            .Where(item => string.Equals(item.GetProperty("button").GetString(), button, StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                IsDown = item.GetProperty("isDown").GetBoolean(),
                OccurredAt = item.GetProperty("occurredAt").GetDateTimeOffset()
            })
            .OrderBy(item => item.OccurredAt)
            .ToArray();
        if (entries.Length == 0) return;

        if (!eventsInitialized)
        {
            eventsInitialized = true;
            lastEventAt = entries[^1].OccurredAt;
            remoteHeld = entries[^1].IsDown;
            // Stale history (the queue keeps old entries): just take the
            // baseline and wait for the next press.
            if (DateTimeOffset.Now - entries[^1].OccurredAt >= TimeSpan.FromMilliseconds(600)) return;
            // The first Home sighting may already be the live press being
            // handled right now — the poller only sees Home entries after
            // the user presses Home once. Rewind the baseline so the normal
            // loop below processes that burst instead of silently
            // swallowing the very first press after startup.
            lastEventAt = entries[^1].OccurredAt - TimeSpan.FromMilliseconds(600);
            remoteHeld = false;
        }

        foreach (var entry in entries)
        {
            if (entry.OccurredAt <= lastEventAt) continue;
            lastEventAt = entry.OccurredAt;
            if (!entry.IsDown)
            {
                remoteHeld = false;
                continue;
            }
            if (remoteHeld) continue;
            remoteHeld = true;
            toggle();
        }
    }

    private void HandleRemoteCursorInputs(JsonElement remote)
    {
        if (!remote.TryGetProperty("recentInputs", out var recent) || recent.ValueKind != JsonValueKind.Array) return;
        var entries = recent.EnumerateArray()
                     .Where(item => item.TryGetProperty("button", out var buttonValue) &&
                                    buttonValue.GetString() is "Up" or "Down" or "Left" or "Right" or "Back" or "Ok")
                     .OrderBy(item => item.GetProperty("occurredAt").GetDateTimeOffset())
                     .ToArray();
        if (!_cursorEventsInitialized)
        {
            _cursorEventsInitialized = true;
            foreach (var entry in entries)
            {
                var button = entry.GetProperty("button").GetString()!;
                _cursorEventAt[button] = entry.GetProperty("occurredAt").GetDateTimeOffset();
                if (entry.GetProperty("isDown").GetBoolean()) _cursorRemoteHeld.Add(button);
            }
            return;
        }

        foreach (var entry in entries)
        {
            var button = entry.GetProperty("button").GetString()!;
            var occurredAt = entry.GetProperty("occurredAt").GetDateTimeOffset();
            if (_cursorEventAt.TryGetValue(button, out var lastEventAt) && occurredAt <= lastEventAt) continue;
            _cursorEventAt[button] = occurredAt;

            var isDown = entry.GetProperty("isDown").GetBoolean();
            if (!isDown)
            {
                _cursorRemoteHeld.Remove(button);
                if (button.Equals("Back", StringComparison.OrdinalIgnoreCase)) StopBigScreenBackRepeat();
                continue;
            }
            if (_bigScreenTextWindow is not { } screen)
            {
                // Without the big screen the cursor keys fall back to the
                // studio itself while its window owns the foreground.
                HandleStudioNavigationInputs(button);
                continue;
            }

            if (button.Equals("Back", StringComparison.OrdinalIgnoreCase))
            {
                // Back is the big screen's delete key while it owns editing.
                // The target plugin refuses that key in the same window, so the
                // real draft is never deleted behind the screen. Repeated HID
                // down events fold into one press: the repeat timer handles the
                // hold, with a keyboard-like initial delay.
                if (!_cursorRemoteHeld.Add(button)) continue;
                screen.DeleteBackward();
                StartBigScreenBackRepeat(screen);
                continue;
            }

            if (button.Equals("Ok", StringComparison.OrdinalIgnoreCase))
            {
                // The confirm key while the big screen owns editing: leave the
                // screen first (committing its complete draft), then send. The
                // target plugin refuses `send` during the mirror, so the remote
                // plugin's own press of this key cannot dispatch a stale send.
                if (!_cursorRemoteHeld.Add(button)) continue;
                _ = HandleBigScreenConfirmAsync(screen);
                continue;
            }

            // A held direction key keeps reporting down events (~30/s) — that is
            // the remote's own key repeat, and every report moves the caret. The
            // tiny window only folds duplicated reports of a single press.
            if (_cursorMovedAt.TryGetValue(button, out var lastMove) &&
                occurredAt - lastMove < TimeSpan.FromMilliseconds(20)) continue;
            _cursorMovedAt[button] = occurredAt;
            // If the editor handled this key itself as a keyboard event just
            // now, skip the explicit move so it does not move twice.
            if (DateTimeOffset.UtcNow - screen.LastNavigationAt < TimeSpan.FromMilliseconds(150)) continue;
            screen.MoveCaret(button);
        }
    }

    /// <summary>
    /// The remote's confirm key while the big screen owns editing. The screen
    /// (not the remote plugin) knows the complete draft, so it leaves first —
    /// committing that draft to the real editor — and only then the target is
    /// asked to send. A failed commit keeps the screen open with its text.
    /// </summary>
    private async Task HandleBigScreenConfirmAsync(BigScreenTextWindow window)
    {
        if (!ReferenceEquals(_bigScreenTextWindow, window)) return;
        var pluginId = _bigScreenProbePluginId ?? _selectedPluginId;
        try
        {
            // Order decides how the gesture feels. First push the screen's own
            // edits into the mirrored draft (a purely in-memory update), then close
            // the screen so it disappears at once, and only then commit that draft to
            // the real editor and send. Hiding the window instead of closing it does
            // not work here: the big screen reclaims the foreground on its own, so a
            // hidden window flickers straight back and the user presses again.
            // Closing first is safe because a failed commit leaves the draft in the
            // mirrored fallback, and reopening the screen reads it back through the
            // standard input probe.
            if (!await FlushBigScreenEditsAsync(window)) return;
            window.Close();
            var commit = await _client.InvokeAsync(
                pluginId,
                HarnessPluginActions.InputMirror,
                new() { ["active"] = "false" },
                timeoutMs: 60000);
            if (!commit.Success)
            {
                SetSelectedPluginStatus($"TV 大屏内容提交失败：{commit.Message}（草稿仍在镜像中，按 TV 键可找回）");
                return;
            }
            await SendBigScreenDraftAsync(pluginId);
        }
        catch (Exception exception)
        {
            // This runs from the remote event loop, so a failure must not
            // vanish into an unobserved task.
            SetSelectedPluginStatus($"TV 大屏确认失败：{exception.Message}");
        }
    }

    private async Task SendBigScreenDraftAsync(string pluginId)
    {
        try
        {
            // A running task answers this key with stop, which the remote
            // plugin already dispatched; sending on top of it would be wrong.
            var status = await _client.InvokeAsync(pluginId, HarnessPluginActions.Status, timeoutMs: 4000);
            if (!status.Success ||
                (TryReadBool(status.Data, "canStop", out var canStop) && canStop)) return;
            var send = await _client.InvokeAsync(pluginId, HarnessPluginActions.Send, timeoutMs: 15000);
            SetSelectedPluginStatus(send.Message);
        }
        catch (Exception exception) { SetSelectedPluginStatus(exception.Message); }
    }

    private void StartBigScreenBackRepeat(BigScreenTextWindow window)
    {
        if (_bigScreenBackRepeatTimer is null)
        {
            _bigScreenBackRepeatTimer = new DispatcherTimer();
            _bigScreenBackRepeatTimer.Tick += BigScreenBackRepeatTick;
        }
        // One press deletes exactly one character. Auto-repeat only starts
        // after the delay a held keyboard key would use (Windows uses about
        // half a second), so letting go a little late cannot eat a second
        // character.
        _bigScreenBackRepeatTimer.Interval = TimeSpan.FromMilliseconds(550);
        _bigScreenBackRepeatTimer.Start();
    }

    private void StopBigScreenBackRepeat() => _bigScreenBackRepeatTimer?.Stop();

    private void BigScreenBackRepeatTick(object? sender, EventArgs e)
    {
        if (_bigScreenTextWindow is not { } window || !_cursorRemoteHeld.Contains("Back"))
        {
            StopBigScreenBackRepeat();
            return;
        }
        // Past the initial delay the key repeats at the usual typing speed.
        if (_bigScreenBackRepeatTimer is { } timer && timer.Interval != TimeSpan.FromMilliseconds(80))
            timer.Interval = TimeSpan.FromMilliseconds(80);
        window.DeleteBackward();
    }

    private void SetVoiceResultText(string text)
    {
        // The big screen reads text only through the selected target plugin's
        // standard input probe; recognition results never bypass that contract.
        VoiceResult.Text = text;
    }

    private async void ToggleBigScreenText()
    {
        if (_bigScreenTextWindow is { } existing)
        {
            await FlushAndCloseBigScreenAsync(existing);
            return;
        }
        if (_openingBigScreen) return;

        if (!_selectedTargetActions.Contains(HarnessPluginActions.InputWatch) ||
            !_selectedTargetActions.Contains(HarnessPluginActions.InputReplace) ||
            !_selectedTargetActions.Contains(HarnessPluginActions.InputMirror))
        {
            VoiceTranslationStatus.Text = $"{_selectedPluginName} 未提供大屏输入接管能力，无法打开大屏。";
            return;
        }

        _openingBigScreen = true;
        string initialText = string.Empty;
        long initialRevision = -1;
        int? initialCaretIndex = null;
        var initialAvailable = false;
        try
        {
            // Sample before covering Electron. This avoids opening on a blank
            // placeholder while Chromium's accessibility tree is throttled.
            for (var attempt = 0; attempt < 3 && !initialAvailable; attempt++)
            {
                var initial = await _client.InvokeAsync(
                    _selectedPluginId,
                    HarnessPluginActions.InputWatch,
                    new() { ["afterRevision"] = "-1", ["timeoutMs"] = "1200" },
                    timeoutMs: 1500);
                initialAvailable = TryReadInputProbe(
                    initial, out initialText, out initialRevision, out initialCaretIndex);
                if (!initialAvailable) await Task.Delay(100);
            }

        }
        catch { /* failure is reported below; never open with an empty guess */ }
        finally { _openingBigScreen = false; }
        if (!initialAvailable)
        {
            VoiceTranslationStatus.Text = "未能读取 ZCode 输入框，TV 大屏未打开。";
            return;
        }

        // From here on the big screen owns editing: the target keeps its
        // current value and stops touching (and focusing) the real editor
        // until the draft is committed on send or close.
        var pluginId = _selectedPluginId;
        var mirrorArguments = new Dictionary<string, string>
        {
            ["active"] = "true",
            ["text"] = initialText
        };
        // Seed the mirrored caret as well, so an early voice write lands where
        // the target editor's caret was when the big screen opened.
        if (initialCaretIndex is { } seededCaret)
            mirrorArguments["caret"] = seededCaret.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var mirror = await _client.InvokeAsync(
            pluginId,
            HarnessPluginActions.InputMirror,
            mirrorArguments,
            timeoutMs: 5000);
        if (!mirror.Success)
        {
            VoiceTranslationStatus.Text = $"无法让 TV 大屏接管输入：{mirror.Message}";
            return;
        }

        // Releasing the mirror is a no-op when it is already released, so a
        // window that goes away without the normal close path (Alt+F4, an
        // OS-level close) still hands its draft back.
        async Task ReleaseMirrorAsync()
        {
            try
            {
                await _client.InvokeAsync(
                    pluginId,
                    HarnessPluginActions.InputMirror,
                    new() { ["active"] = "false" },
                    timeoutMs: 60000);
            }
            catch { /* the next open re-takes the mirror anyway */ }
        }

        var window = new BigScreenTextWindow();
        window.TextEdited += text => QueueBigScreenEdit(window, text);
        window.CaretChanged += caret => QueueBigScreenCaretSync(window, caret);
        window.CloseRequested += CloseBigScreenText;
        _bigScreenTextWindow = window;
        _bigScreenProbePluginId = pluginId;
        _bigScreenProbeRevision = initialRevision;
        _bigScreenProbeText = initialText;
        _bigScreenProbeCaretIndex = initialCaretIndex;
        _bigScreenLocalDirty = false;
        _bigScreenProbeCancellation?.Cancel();
        _bigScreenProbeCancellation?.Dispose();
        var probeCancellation = new CancellationTokenSource();
        _bigScreenProbeCancellation = probeCancellation;
        window.ApplySnapshot(initialText, initialCaretIndex);
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_bigScreenTextWindow, window)) return;
            _bigScreenTextWindow = null;
            _bigScreenProbePluginId = null;
            _bigScreenProbeRevision = -1;
            _bigScreenProbeText = null;
            _bigScreenProbeCaretIndex = null;
            _bigScreenLocalDirty = false;
            _bigScreenEditCancellation?.Cancel();
            _bigScreenEditCancellation?.Dispose();
            _bigScreenEditCancellation = null;
            _bigScreenCaretSyncCancellation?.Cancel();
            _bigScreenCaretSyncCancellation?.Dispose();
            _bigScreenCaretSyncCancellation = null;
            if (ReferenceEquals(_bigScreenProbeCancellation, probeCancellation))
            {
                _bigScreenProbeCancellation = null;
                probeCancellation.Cancel();
                probeCancellation.Dispose();
            }
            // A window that goes away without the normal close path (Alt+F4,
            // an OS-level close) must still hand the draft back.
            _ = ReleaseMirrorAsync();
        };
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null) window.Position = screen.Bounds.Position;
        window.Show();
        _ = RunBigScreenWatchLoopAsync(window, probeCancellation.Token);
    }

    private void QueueBigScreenEdit(BigScreenTextWindow window, string text)
    {
        _bigScreenLocalDirty = true;
        var generation = ++_bigScreenEditGeneration;
        _bigScreenEditCancellation?.Cancel();
        _bigScreenEditCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _bigScreenEditCancellation = cancellation;
        // Mirror the whole text into the real editor once typing pauses. This
        // must NOT run while the user is still working: every mirror write
        // briefly takes the foreground, and doing that mid-typing would steal
        // the remote's keys from the big screen. Voice writes go through
        // immediately (the plugin's append path mirrors them itself), so this
        // only covers screen-local typing and deletion.
        _bigScreenEditTask = ReplaceBigScreenTextAsync(window, generation, cancellation, 1500);
    }

    /// <summary>
    /// Navigation on the big screen does not change the text, but the mirror
    /// still needs the caret so a later insertion (voice) lands there.
    /// </summary>
    private void QueueBigScreenCaretSync(BigScreenTextWindow window, int caret)
    {
        _bigScreenCaretSyncCancellation?.Cancel();
        _bigScreenCaretSyncCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _bigScreenCaretSyncCancellation = cancellation;
        _ = SyncBigScreenCaretAsync(window, caret, cancellation);
    }

    private async Task SyncBigScreenCaretAsync(
        BigScreenTextWindow window,
        int caret,
        CancellationTokenSource cancellation)
    {
        var ct = cancellation.Token;
        var gateEntered = false;
        try
        {
            await Task.Delay(120, ct);
            await _bigScreenSyncGate.WaitAsync(ct);
            gateEntered = true;
            if (!ReferenceEquals(_bigScreenTextWindow, window)) return;
            ct.ThrowIfCancellationRequested();
            // Caret-only: the mirror updates its caret and leaves both the
            // draft text and the real editor untouched.
            await _client.InvokeAsync(
                _bigScreenProbePluginId ?? _selectedPluginId,
                HarnessPluginActions.InputReplace,
                new()
                {
                    ["caretOnly"] = "true",
                    ["caret"] = caret.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                timeoutMs: 3000,
                ct: CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch { /* the next navigation retries */ }
        finally
        {
            if (gateEntered) _bigScreenSyncGate.Release();
            if (ReferenceEquals(_bigScreenCaretSyncCancellation, cancellation))
                _bigScreenCaretSyncCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task ReplaceBigScreenTextAsync(
        BigScreenTextWindow window,
        int generation,
        CancellationTokenSource cancellation,
        int delayMilliseconds)
    {
        var ct = cancellation.Token;
        var gateEntered = false;
        try
        {
            // Wait for a real typing pause so a whole word lands in one
            // replacement instead of one IPC round trip per keystroke.
            if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, ct);
            await _bigScreenSyncGate.WaitAsync(ct);
            gateEntered = true;

            if (!ReferenceEquals(_bigScreenTextWindow, window)) return;
            ct.ThrowIfCancellationRequested();
            var localText = window.CurrentText;
            // The target plugin keeps this draft in memory while the mirror is
            // active, so the write is a fast in-process update that never
            // takes the foreground (or the caret) away from the big screen.
            var result = await _client.InvokeAsync(
                _bigScreenProbePluginId ?? _selectedPluginId,
                HarnessPluginActions.InputReplace,
                new()
                {
                    ["text"] = localText,
                    // Keep the mirrored caret on the big screen's own caret so
                    // a later voice write inserts exactly where it is.
                    ["caret"] = window.EditorCaretIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                timeoutMs: 3000,
                // Once ZCode has begun replacing the value it must finish as
                // one atomic operation. A newer TV edit waits on the gate and
                // performs the next complete replacement; canceling this IPC
                // call would only drop the response while the Host kept typing.
                ct: CancellationToken.None);
            if (!result.Success) return;

            if (TryReadInputProbe(result, out var targetText, out var targetRevision, out var targetCaret))
            {
                _bigScreenProbeText = targetText;
                _bigScreenProbeRevision = targetRevision;
                _bigScreenProbeCaretIndex = targetCaret;
            }
            if (generation == _bigScreenEditGeneration &&
                string.Equals(window.CurrentText, localText, StringComparison.Ordinal))
                _bigScreenLocalDirty = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { /* retain local contents; the next edit retries */ }
        finally
        {
            // Restore TV focus before handing the gate to the next writer.
            // A canceled debounce never took target focus and needs no activation.
            if (gateEntered)
            {
                try
                {
                    if (ReferenceEquals(_bigScreenTextWindow, window)) ReclaimBigScreenForeground(window);
                }
                finally { _bigScreenSyncGate.Release(); }
            }
            if (ReferenceEquals(_bigScreenEditCancellation, cancellation))
                _bigScreenEditCancellation = null;
            cancellation.Dispose();
        }
    }

    private async void CloseBigScreenText()
    {
        if (_bigScreenTextWindow is { } window) await FlushAndCloseBigScreenAsync(window);
    }

    private async Task FlushAndCloseBigScreenAsync(BigScreenTextWindow window)
    {
        if (!ReferenceEquals(_bigScreenTextWindow, window)) return;
        if (!await FlushBigScreenEditsAsync(window)) return;
        // Releasing the mirror is what commits the draft: the plugin writes
        // the complete text back in one operation and only then lets the real
        // editor be edited again. Keep the window open when that fails so the
        // text is never silently dropped.
        var commit = await _client.InvokeAsync(
            _bigScreenProbePluginId ?? _selectedPluginId,
            HarnessPluginActions.InputMirror,
            new() { ["active"] = "false" },
            timeoutMs: 60000);
        if (!commit.Success)
        {
            VoiceTranslationStatus.Text = $"TV 大屏内容提交失败：{commit.Message}";
            window.FocusEditor();
            return;
        }
        if (ReferenceEquals(_bigScreenTextWindow, window)) window.Close();
    }

    /// <summary>
    /// Pushes the screen's own edits into the mirrored draft. This is the only part
    /// of a confirm that has to run while the screen is still alive; committing the
    /// draft to ChatGPT can happen after the screen is gone.
    /// </summary>
    private async Task<bool> FlushBigScreenEditsAsync(BigScreenTextWindow window)
    {
        if (!_bigScreenLocalDirty) return true;
        _bigScreenEditCancellation?.Cancel();
        _bigScreenEditCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _bigScreenEditCancellation = cancellation;
        var generation = _bigScreenEditGeneration;
        _bigScreenEditTask = ReplaceBigScreenTextAsync(window, generation, cancellation, 0);
        await _bigScreenEditTask;
        if (_bigScreenLocalDirty)
        {
            VoiceTranslationStatus.Text = "TV 大屏内容尚未同步，已保留窗口，请稍后重试。";
            window.FocusEditor();
            return false;
        }
        return true;
    }

    private void ToggleBigScreenFromRemote()
    {
        if (_bigScreenTextWindow is not null)
        {
            ToggleBigScreenText();
            return;
        }

        ToggleBigScreenText();
    }

    private async Task RunBigScreenWatchLoopAsync(BigScreenTextWindow window, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ReferenceEquals(_bigScreenTextWindow, window))
        {
            try
            {
                var pluginId = _bigScreenProbePluginId;
                if (pluginId is null) return;
                var result = await _client.InvokeAsync(
                    pluginId,
                    HarnessPluginActions.InputWatch,
                    new()
                    {
                        ["afterRevision"] = _bigScreenProbeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["timeoutMs"] = "20000"
                    },
                    timeoutMs: 23000,
                    ct: ct);
                if (!ReferenceEquals(_bigScreenTextWindow, window)) return;
                if (TryReadInputProbe(result, out var text, out var revision, out var caretIndex))
                {
                    if (revision != _bigScreenProbeRevision ||
                        !string.Equals(text, _bigScreenProbeText, StringComparison.Ordinal) ||
                        caretIndex != _bigScreenProbeCaretIndex)
                    {
                        _bigScreenProbeRevision = revision;
                        _bigScreenProbeText = text;
                        _bigScreenProbeCaretIndex = caretIndex;
                        // While TV has an unsaved edit it is the sole authority.
                        // A delayed target notification may update the cached
                        // revision but must never overwrite the TV TextBox.
                        if (!_bigScreenLocalDirty)
                        {
                            // Our own replacements keep the big screen as the
                            // caret authority. A writer that inserted into the
                            // mirrored draft (voice) reports where it put the
                            // text, and the caret must follow it there.
                            var source = result.Data is JsonElement { ValueKind: JsonValueKind.Object } probe &&
                                         probe.TryGetProperty("source", out var sourceValue) &&
                                         sourceValue.ValueKind == JsonValueKind.String
                                ? sourceValue.GetString()
                                : null;
                            window.ApplySnapshot(text, source == "mirror-append" ? caretIndex : null);
                        }
                    }
                }
                // Reclaim only once a change has settled. Doing it on every poll
                // would steal the foreground from a mirror write still in flight
                // (SendInput only reaches the foreground window), which made the
                // next write fail with EditorFocusLost.
                ReclaimBigScreenForeground(window);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch
            {
                // The event subscription is renewed after transient Host or
                // Electron window changes; the last complete value is kept.
            }

            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    /// <summary>
    /// Every mirror write reaches the real editor through SendInput, which only
    /// works while that editor owns the foreground. Windows then refuses a plain
    /// re-activation, so release the foreground lock with a synthetic ALT press
    /// and take the big screen back explicitly.
    /// </summary>
    private static void ReclaimBigScreenForeground(BigScreenTextWindow window)
    {
        if (window.IsActive) return;
        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) return;
        const byte vkMenu = 0x12;
        keybd_event(vkMenu, 0, 0, 0);
        keybd_event(vkMenu, 0, 2, 0); // KEYEVENTF_KEYUP
        SetForegroundWindow(handle);
        window.FocusEditor();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);

    private static bool TryReadInputProbe(
        CommandResult result,
        out string text,
        out long revision,
        out int? caretIndex)
    {
        text = string.Empty;
        revision = -1;
        caretIndex = null;
        if (!result.Success ||
            result.Data is not JsonElement { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("available", out var available) || !available.GetBoolean() ||
            !data.TryGetProperty("text", out var textValue) || textValue.ValueKind != JsonValueKind.String ||
            !data.TryGetProperty("revision", out var revisionValue) || !revisionValue.TryGetInt64(out revision))
            return false;

        text = textValue.GetString() ?? string.Empty;
        if (data.TryGetProperty("caretIndex", out var caretValue) &&
            caretValue.ValueKind == JsonValueKind.Number &&
            caretValue.TryGetInt32(out var parsedCaret))
            caretIndex = Math.Clamp(parsedCaret, 0, text.Length);
        return true;
    }

    private static bool TryReadRunning(object? data, out bool running)
    {
        running = false;
        if (data is not JsonElement { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty("running", out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        running = value.GetBoolean();
        return true;
    }

    private static bool TryReadBool(object? data, string name, out bool value)
    {
        value = false;
        if (data is not JsonElement { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }

    private bool SelectedPluginSupportsPower =>
        _loadedTargetPluginIds.Contains(_selectedPluginId) &&
        _selectedTargetActions.Contains(HarnessPluginActions.Status) &&
        _selectedTargetActions.Contains(HarnessPluginActions.Open) &&
        _selectedTargetActions.Contains(HarnessPluginActions.Close);

    private static void OpenPath(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", path);
        else
            Process.Start("xdg-open", path);
    }

    private sealed record TargetPluginView(string Id, string Name, string Version, HashSet<string> Actions, IImage? Icon = null);
    private sealed record TargetActionRequest(string PluginId, string Action);

    private void MinimizeWindow(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void TitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximizeWindow(sender, e);
            return;
        }

        BeginMoveDrag(e);
    }

    private void ToggleMaximizeWindow(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();

    private void UpdateWindowStateVisuals()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
        WindowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(9);
        TitleBar.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(9, 9, 0, 0);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
