using System.Diagnostics;
using System.IO.Compression;
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
    private readonly GlobalTvKeyMonitor _tvKeyMonitor;
    private readonly Dictionary<string, ToggleButton> _buttons;
    private readonly Dictionary<string, bool> _voiceModelLoadedFlags = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;
    private bool _loading;
    private string _pluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
    private string _targetPluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins", "targets");
    private DateTimeOffset _nextPluginScan;
    private string? _zCodeBundlePath;
    private string? _zCodeBundleVersion;
    private bool _zCodeLoaded;
    private string _selectedPluginId = "mrc.zcode";
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
    private CancellationTokenSource? _remoteEventCancellation;
    private BigScreenTextWindow? _bigScreenTextWindow;
    private string? _bigScreenProbePluginId;
    private long _bigScreenProbeRevision = -1;
    private string? _bigScreenProbeText;
    private CancellationTokenSource? _bigScreenProbeCancellation;
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
        _tvKeyMonitor = new GlobalTvKeyMonitor(HandleGlobalTvKey);
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

        RefreshInstalledPlugins(force: true);
        Opened += OnWindowOpened;
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty) UpdateWindowStateVisuals();
        };
        _timer.Tick += async (_, _) => await LoadState();
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (_started) return;
        _started = true;
        try
        {
            var start = await _client.StartForDesktopAsync(Environment.ProcessId);
            if (!start.Success)
            {
                SetRemoteConnectionState(false, start.Message);
                VoiceTranslationStatus.Text = start.Message;
                SetZCodeLoaded(false);
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
        _tvKeyMonitor.Dispose();
        _enhancedRemoteKeys.Dispose();
        _voicePromptWindow?.Close();
        _voicePromptWindow = null;
        _bigScreenProbeCancellation?.Cancel();
        _bigScreenProbeCancellation?.Dispose();
        _bigScreenProbeCancellation = null;
        _bigScreenTextWindow?.Close();
        _bigScreenTextWindow = null;
    }

    private async Task LoadState()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            RefreshInstalledPlugins(force: false);
            var result = await _client.InvokeAsync("core", "status", timeoutMs: 1000);
            if (!result.Success)
            {
                SetRemoteConnectionState(false, result.Message);
                VoiceTranslationStatus.Text = result.Message;
                ResetRemoteBattery("后台未连接，暂时无法读取遥控器电量");
                SetZCodeLoaded(false);
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
            SetZCodeLoaded(false);
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
        var directoryChanged = false;
        if (state.TryGetProperty("selectedPlugin", out var selectedPlugin) &&
            selectedPlugin.GetString() is { Length: > 0 } selectedId)
        {
            _selectedPluginId = selectedId;
            ZCodePluginSelector.IsChecked = string.Equals(selectedId, "mrc.zcode", StringComparison.OrdinalIgnoreCase);
        }

        if (state.TryGetProperty("pluginDirectory", out var directory) &&
            directory.GetString() is { Length: > 0 } path &&
            !string.Equals(_pluginDirectory, path, StringComparison.OrdinalIgnoreCase))
        {
            _pluginDirectory = path;
            _targetPluginDirectory = Path.Combine(path, "targets");
            directoryChanged = true;
        }
        if (state.TryGetProperty("targetPluginDirectory", out var targetDirectory) &&
            targetDirectory.GetString() is { Length: > 0 } targetPath &&
            !string.Equals(_targetPluginDirectory, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            _targetPluginDirectory = targetPath;
            directoryChanged = true;
        }

        var loaded = false;
        string? loadedVersion = null;
        if (state.TryGetProperty("plugins", out var plugins))
            foreach (var plugin in plugins.EnumerateArray())
                if (string.Equals(plugin.GetProperty("id").GetString(), "mrc.zcode", StringComparison.OrdinalIgnoreCase))
                {
                    loaded = true;
                    loadedVersion = plugin.TryGetProperty("version", out var version) ? version.GetString() : null;
                    break;
                }

        RefreshInstalledPlugins(force: directoryChanged);
        SetZCodeLoaded(loaded, loadedVersion);
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
        RefreshInstalledPlugins(force: true);
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

    private void RefreshInstalledPlugins(bool force)
    {
        var now = DateTimeOffset.Now;
        if (!force && now < _nextPluginScan) return;
        _nextPluginScan = now.AddSeconds(1);

        string? bundlePath = null;
        string? bundleVersion = null;
        try
        {
            if (Directory.Exists(_targetPluginDirectory))
                foreach (var file in Directory.EnumerateFiles(_targetPluginDirectory, "*", SearchOption.TopDirectoryOnly))
                    if (TryReadZCodeBundle(file, out bundleVersion))
                    {
                        bundlePath = file;
                        break;
                    }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var changed = !string.Equals(_zCodeBundlePath, bundlePath, StringComparison.OrdinalIgnoreCase) ||
                      !string.Equals(_zCodeBundleVersion, bundleVersion, StringComparison.OrdinalIgnoreCase);
        _zCodeBundlePath = bundlePath;
        _zCodeBundleVersion = bundleVersion;
        ApplyPluginPresentation(changed);
    }

    private static bool TryReadZCodeBundle(string path, out string? version)
    {
        version = null;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            ZipArchiveEntry? manifest = null;
            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (!string.Equals(normalized, "plugin.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (manifest is not null) return false;
                manifest = entry;
            }
            if (manifest is null) return false;

            using var stream = manifest.Open();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) ||
                !string.Equals(id.GetString(), "mrc.zcode", StringComparison.OrdinalIgnoreCase)) return false;
            if (!root.TryGetProperty("apiVersion", out var apiVersion) || apiVersion.GetInt32() != 1) return false;
            version = root.TryGetProperty("version", out var pluginVersion) ? pluginVersion.GetString() : null;
            return true;
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void SetZCodeLoaded(bool loaded, string? version = null)
    {
        var changed = _zCodeLoaded != loaded;
        _zCodeLoaded = loaded;
        if (!loaded)
        {
            PowerButton.IsChecked = false;
        }
        if (!string.IsNullOrWhiteSpace(version)) _zCodeBundleVersion = version;
        ApplyPluginPresentation(changed);
        if (changed && loaded && IsVisible &&
            string.Equals(_selectedPluginId, "mrc.zcode", StringComparison.OrdinalIgnoreCase))
            _ = RefreshSelectedPluginPowerStateAsync(updateStatusText: false);
    }

    private void ApplyPluginPresentation(bool stateChanged)
    {
        var installed = _zCodeBundlePath is not null && File.Exists(_zCodeBundlePath);
        ZCodePluginCard.IsVisible = installed;
        NoPluginsText.IsVisible = !installed;
        PluginCountText.Text = installed ? _zCodeLoaded ? "1 个已加载" : "1 个已安装" : "暂无插件";

        var available = installed && _zCodeLoaded;
        OpenZCodeButton.IsEnabled = available;
        ReadZCodeStatusButton.IsEnabled = available;
        StopZCodeTaskButton.IsEnabled = available;
        PowerButton.IsEnabled = available && !_pluginPowerBusy &&
                                string.Equals(_selectedPluginId, "mrc.zcode", StringComparison.OrdinalIgnoreCase);
        ZCodeAvailabilityText.Text = available ? "可用" : OperatingSystem.IsWindows() ? "未连接" : "仅 Windows";

        if (!installed) return;
        var isAutomaticStatus = ZCodePluginStatus.Text?.StartsWith("插件已加载", StringComparison.Ordinal) == true ||
                                ZCodePluginStatus.Text?.StartsWith("已安装", StringComparison.Ordinal) == true ||
                                ZCodePluginStatus.Text is "正在检查后台状态" or "后台未连接";
        if (!stateChanged && !isAutomaticStatus) return;
        var versionSuffix = string.IsNullOrWhiteSpace(_zCodeBundleVersion) ? "" : $" · v{_zCodeBundleVersion}";
        ZCodePluginStatus.Text = available
            ? $"插件已加载{versionSuffix}"
            : $"已安装{versionSuffix} · {(OperatingSystem.IsWindows() ? "等待后台连接" : "当前平台不可用")}";
    }

    private void OpenPluginFolder(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_pluginDirectory);
        OpenPath(_pluginDirectory);
    }

    private async void OpenZCode(object? sender, RoutedEventArgs e) => await InvokeZCodeAsync("open", "正在打开…");
    private async void ReadZCodeStatus(object? sender, RoutedEventArgs e) => await InvokeZCodeAsync("status", "正在读取…");
    private async void StopZCodeTask(object? sender, RoutedEventArgs e) => await InvokeZCodeAsync("stop", "正在停止…");

    private async void SelectPlugin(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string pluginId }) return;
        try
        {
            var result = await _client.InvokeAsync("core", "remote.select", new() { ["plugin"] = pluginId }, timeoutMs: 3000);
            if (!result.Success)
            {
                ZCodePluginStatus.Text = result.Message;
                ZCodePluginSelector.IsChecked = string.Equals(_selectedPluginId, "mrc.zcode", StringComparison.OrdinalIgnoreCase);
                return;
            }
            _selectedPluginId = pluginId;
            ZCodePluginSelector.IsChecked = string.Equals(pluginId, "mrc.zcode", StringComparison.OrdinalIgnoreCase);
            ToolTip.SetTip(PowerButton, "启动或关闭当前工作插件：ZCode 控制");
            ApplyPluginPresentation(stateChanged: false);
            await RefreshSelectedPluginPowerStateAsync(updateStatusText: false);
        }
        catch (Exception exception)
        {
            ZCodePluginStatus.Text = exception.Message;
        }
    }

    private async void PowerButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_pluginPowerBusy) return;
        if (!string.Equals(_selectedPluginId, "mrc.zcode", StringComparison.OrdinalIgnoreCase) || !_zCodeLoaded)
        {
            PowerButton.IsChecked = false;
            ZCodePluginStatus.Text = "当前工作插件尚不可用";
            return;
        }

        _pluginPowerBusy = true;
        PowerButton.IsEnabled = false;
        ZCodePluginStatus.Text = "正在执行虚拟电源键…";
        try
        {
            await _client.AllowForegroundAsync();
            var result = await _client.InvokeAsync("core", "remote.press", new() { ["button"] = "Power" });
            ZCodePluginStatus.Text = result.Message;
            FlashPowerButton();
        }
        catch (Exception exception)
        {
            PowerButton.IsChecked = false;
            ZCodePluginStatus.Text = exception.Message;
        }
        finally
        {
            _pluginPowerBusy = false;
            ApplyPluginPresentation(stateChanged: false);
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
        if (!IsVisible || !_zCodeLoaded ||
            !string.Equals(_selectedPluginId, "mrc.zcode", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var result = await _client.InvokeAsync(_selectedPluginId, "status", timeoutMs: 4000);
            if (updateStatusText || !result.Success) ZCodePluginStatus.Text = result.Message;
        }
        catch (Exception exception)
        {
            if (updateStatusText) ZCodePluginStatus.Text = exception.Message;
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

    private bool HandleGlobalEscape()
    {
        if (_bigScreenTextWindow is null) return false;
        Dispatcher.UIThread.Post(CloseBigScreenText);
        return true;
    }

    private void HandleGlobalTvKey() => Dispatcher.UIThread.Post(ToggleBigScreenFromRemote);

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

    private void SetVoiceResultText(string text)
    {
        // The big screen is strictly bound to the ZCode composer; recognition
        // results reach it only through the input box itself.
        VoiceResult.Text = text;
    }

    private void ToggleBigScreenText()
    {
        if (_bigScreenTextWindow is { } existing)
        {
            existing.Close();
            return;
        }

        var window = new BigScreenTextWindow();
        _bigScreenTextWindow = window;
        _bigScreenProbePluginId = _selectedPluginId;
        _bigScreenProbeRevision = -1;
        _bigScreenProbeText = null;
        _bigScreenProbeCancellation?.Cancel();
        _bigScreenProbeCancellation?.Dispose();
        var probeCancellation = new CancellationTokenSource();
        _bigScreenProbeCancellation = probeCancellation;
        window.SetText(null);
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_bigScreenTextWindow, window)) return;
            _bigScreenTextWindow = null;
            _bigScreenProbePluginId = null;
            _bigScreenProbeRevision = -1;
            _bigScreenProbeText = null;
            if (ReferenceEquals(_bigScreenProbeCancellation, probeCancellation))
            {
                _bigScreenProbeCancellation = null;
                probeCancellation.Cancel();
                probeCancellation.Dispose();
            }
        };
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null) window.Position = screen.Bounds.Position;
        window.Show();
        _ = RunBigScreenProbeLoopAsync(window, probeCancellation.Token);
    }

    private void CloseBigScreenText()
    {
        _bigScreenTextWindow?.Close();
    }

    private async void ToggleBigScreenFromRemote()
    {
        if (_bigScreenTextWindow is not null)
        {
            ToggleBigScreenText();
            return;
        }

        if (string.Equals(_selectedPluginId, "mrc.zcode", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Windows exposes the RC003 TV usage as an OEM text key. It
                // cannot be distinguished safely in a global keyboard hook,
                // so remove only the known trailing artifact after the device-
                // specific Raw Input event has identified this as the TV key.
                await Task.Delay(30);
                await _client.InvokeAsync("mrc.zcode", "input.remove-tv-artifact", timeoutMs: 1500);
            }
            catch { /* synchronization below remains the source of truth */ }
        }

        ToggleBigScreenText();
    }

    private async Task RunBigScreenProbeLoopAsync(BigScreenTextWindow window, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ReferenceEquals(_bigScreenTextWindow, window))
        {
            try
            {
                var pluginId = _selectedPluginId;
                var result = await _client.InvokeAsync(
                    pluginId, TargetPluginActions.InputProbe, timeoutMs: 1000, ct: ct);
                if (!ReferenceEquals(_bigScreenTextWindow, window)) return;
                if (result.Success &&
                    result.Data is JsonElement { ValueKind: JsonValueKind.Object } data &&
                    data.TryGetProperty("available", out var available) && available.GetBoolean() &&
                    data.TryGetProperty("text", out var textValue) && textValue.ValueKind == JsonValueKind.String &&
                    data.TryGetProperty("revision", out var revisionValue) && revisionValue.TryGetInt64(out var revision))
                {
                    var text = textValue.GetString() ?? string.Empty;
                    if (!string.Equals(_bigScreenProbePluginId, pluginId, StringComparison.OrdinalIgnoreCase))
                    {
                        _bigScreenProbePluginId = pluginId;
                        _bigScreenProbeRevision = -1;
                        _bigScreenProbeText = null;
                    }
                    // Exactly one request is in flight in this loop, so an old
                    // response can never overwrite a newer snapshot. Comparing
                    // text as well as revision handles a Host/plugin restart.
                    if (revision != _bigScreenProbeRevision ||
                        !string.Equals(text, _bigScreenProbeText, StringComparison.Ordinal))
                    {
                        _bigScreenProbeRevision = revision;
                        _bigScreenProbeText = text;
                        window.SetText(text);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch
            {
                // Keep the last confirmed snapshot and retry independently of
                // the heavier core status refresh.
            }

            try { await Task.Delay(80, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
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

    private async Task InvokeZCodeAsync(string action, string pendingText)
    {
        ZCodePluginStatus.Text = pendingText;
        try
        {
            if (action == "open") await _client.AllowForegroundAsync();
            var result = await _client.InvokeAsync("mrc.zcode", action);
            ZCodePluginStatus.Text = result.Message;
            if (result.Success && action == "open") FlashPowerButton();
        }
        catch (Exception exception)
        {
            ZCodePluginStatus.Text = exception.Message;
        }
    }

    private static void OpenPath(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", path);
        else
            Process.Start("xdg-open", path);
    }

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
