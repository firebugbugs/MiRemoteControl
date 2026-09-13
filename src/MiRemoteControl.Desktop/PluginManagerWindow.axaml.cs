using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MiRemoteControl.Desktop;

public partial class PluginManagerWindow : Window
{
    private const string PluginId = "mrc.zcode";
    private const string ManifestUrl = "https://download.cheems.cn/v1/manifests/plugins/mrc.zcode/stable.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _targetPluginDirectory;
    private readonly DispatcherTimer _timer;
    private RemotePlugin? _remotePlugin;
    private CloudBinding? _cloudBinding;
    private string _installedSignature = "";

    public PluginManagerWindow() : this(Path.Combine(AppContext.BaseDirectory, "plugins", "targets"))
    {
    }

    public PluginManagerWindow(string targetPluginDirectory)
    {
        _targetPluginDirectory = Path.GetFullPath(targetPluginDirectory);
        InitializeComponent();
        Directory.CreateDirectory(_targetPluginDirectory);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshUi();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        RefreshLocalPlugins(force: true);
        ShowCloudMessage("正在读取插件更新信息…");
        Opened += async (_, _) => await LoadRemotePluginAsync();
    }

    private async Task LoadRemotePluginAsync()
    {
        try
        {
            using var response = await Http.GetAsync(ManifestUrl);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            if (!root.TryGetProperty("available", out var available) || !available.GetBoolean())
            {
                ShowCloudMessage("ZCode 当前暂无可用更新。", showRefresh: true);
                return;
            }

            var id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            var version = root.TryGetProperty("version", out var versionValue) ? versionValue.GetString() : null;
            var urlText = root.TryGetProperty("downloadUrl", out var urlValue) ? urlValue.GetString() : null;
            var size = root.TryGetProperty("sizeBytes", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
            var sha256 = root.TryGetProperty("sha256", out var hashValue) ? hashValue.GetString() : null;
            if (!string.Equals(id, PluginId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(version) ||
                !Uri.TryCreate(urlText, UriKind.Absolute, out var downloadUrl) ||
                downloadUrl.Scheme != Uri.UriSchemeHttps ||
                !downloadUrl.Host.Equals("download.cheems.cn", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("云端插件清单不完整。");

            _remotePlugin = new RemotePlugin(PluginId, "ZCode 控制", version!, downloadUrl, size, sha256);
            BuildCloudCard(_remotePlugin);
        }
        catch (Exception exception)
        {
            ShowCloudMessage($"读取更新信息失败：{exception.Message}", showRefresh: true);
        }
    }

    private void RefreshUi()
    {
        RefreshLocalPlugins(force: false);
        if (_cloudBinding is not null) RefreshCloudBinding(_cloudBinding);
    }

    private void RefreshLocalPlugins(bool force)
    {
        var installed = FindInstalledPlugin();
        var signature = installed is null ? "none" : $"{installed.Path}|{installed.Version}|{File.GetLastWriteTimeUtc(installed.Path).Ticks}";
        if (!force && string.Equals(signature, _installedSignature, StringComparison.Ordinal)) return;
        _installedSignature = signature;
        LocalPluginsPanel.Children.Clear();

        if (installed is null)
        {
            LocalPluginsPanel.Children.Add(new TextBlock { Text = "未安装 ZCode 插件", Foreground = Brush("Plugin.Muted") });
            return;
        }

        var remove = ActionButton();
        remove.Content = "卸载";
        remove.Click += async (_, _) =>
        {
            if (!await ConfirmAsync("卸载插件", "确定卸载 ZCode 插件？\n当前已经加载的版本会在软件重启后移除。", "确认卸载")) return;
            try
            {
                var full = Path.GetFullPath(installed.Path);
                if (!string.Equals(Directory.GetParent(full)?.FullName, _targetPluginDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("插件路径不安全。");
                File.Delete(full);
                if (_remotePlugin is not null) PersistentDownloadManager.Forget(CreateDescriptor(_remotePlugin));
                _installedSignature = "";
                RefreshLocalPlugins(force: true);
                if (_cloudBinding is not null) RefreshCloudBinding(_cloudBinding);
            }
            catch (Exception exception)
            {
                await ShowNoticeAsync("卸载失败", exception.Message);
            }
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("42,*,Auto") };
        grid.Children.Add(PluginIcon());
        var text = PluginText("ZCode 控制", $"目标端插件 · v{installed.Version}");
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);
        var card = Card();
        card.Child = grid;
        LocalPluginsPanel.Children.Add(card);
    }

    private void BuildCloudCard(RemotePlugin remote)
    {
        CloudPluginsPanel.Children.Clear();
        var descriptor = CreateDescriptor(remote);
        PersistentDownloadManager.GetOrRestore(descriptor);
        var binding = new CloudBinding(remote, descriptor)
        {
            Download = ActionButton(),
            Pause = ActionButton(8, initiallyVisible: false),
            Cancel = ActionButton(8, initiallyVisible: false, content: "取消下载"),
            Status = new TextBlock
            {
                FontSize = 11.5,
                Foreground = Brush("Plugin.Muted"),
                Margin = new Avalonia.Thickness(42, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            },
            Progress = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 7,
                IsVisible = false,
                Margin = new Avalonia.Thickness(42, 10, 0, 0)
            }
        };
        ToolTip.SetTip(binding.Cancel, "停止下载并删除未完成的文件");
        _cloudBinding = binding;

        binding.Download.Click += (_, _) => PersistentDownloadManager.StartOrResume(descriptor);
        binding.Pause.Click += (_, _) =>
        {
            if (PersistentDownloadManager.GetOrRestore(descriptor) is { } job)
                PersistentDownloadManager.TogglePause(job);
        };
        binding.Cancel.Click += async (_, _) => await CancelDownloadAsync(binding);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("42,*,Auto,Auto,Auto") };
        header.Children.Add(PluginIcon());
        var text = PluginText(remote.Name, $"云端稳定版 · v{remote.Version}{(remote.SizeBytes > 0 ? $" · {FormatSize(remote.SizeBytes)}" : "")}");
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        Grid.SetColumn(binding.Download, 2);
        header.Children.Add(binding.Download);
        Grid.SetColumn(binding.Pause, 3);
        header.Children.Add(binding.Pause);
        Grid.SetColumn(binding.Cancel, 4);
        header.Children.Add(binding.Cancel);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(binding.Status);
        stack.Children.Add(binding.Progress);
        var card = Card();
        card.Child = stack;
        CloudPluginsPanel.Children.Add(card);
        RefreshCloudBinding(binding);
    }

    private DownloadDescriptor CreateDescriptor(RemotePlugin remote)
    {
        return new DownloadDescriptor(
            "plugins",
            remote.Id,
            remote.Version,
            remote.Name,
            remote.SizeBytes,
            [new DownloadFileSpec("package.mrcplugin", remote.DownloadUrl)],
            async (workingDirectory, cancellationToken) =>
            {
                var package = Path.Combine(workingDirectory, "package.mrcplugin");
                if (!File.Exists(package)) throw new InvalidDataException("插件包不存在。");
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(remote.Sha256))
                {
                    await using var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                    if (!hash.Equals(remote.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("插件包校验失败。");
                }

                ValidatePluginPackage(package, remote.Version);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_targetPluginDirectory);
                var target = Path.Combine(_targetPluginDirectory, "mrc.zcode.mrcplugin");
                var staging = target + ".new";
                try
                {
                    File.Copy(package, staging, overwrite: true);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(staging, target, overwrite: true);
                }
                finally
                {
                    if (File.Exists(staging)) File.Delete(staging);
                }
            });
    }

    private static void ValidatePluginPackage(string packagePath, string expectedVersion)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var manifests = archive.Entries.Where(entry =>
            string.Equals(entry.FullName.Replace('\\', '/').TrimStart('/'), "plugin.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1) throw new InvalidDataException("插件包必须包含一个根目录 plugin.json。");
        using var stream = manifests[0].Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id) || !string.Equals(id.GetString(), PluginId, StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("apiVersion", out var apiVersion) || apiVersion.GetInt32() != 1)
            throw new InvalidDataException("插件包与 ZCode 插件不匹配。");
        var version = root.TryGetProperty("version", out var versionValue) ? versionValue.GetString() : null;
        if (!string.Equals(version, expectedVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件包版本与更新清单不一致。");
    }

    private void RefreshCloudBinding(CloudBinding binding)
    {
        var installed = FindInstalledPlugin();
        var job = PersistentDownloadManager.GetOrRestore(binding.Descriptor);
        if (job is null)
        {
            var updateAvailable = installed is null || IsNewer(binding.Remote.Version, installed.Version);
            binding.Download.Content = installed is null ? "下载" : updateAvailable ? "更新" : "已是最新";
            binding.Download.IsEnabled = updateAvailable;
            binding.Pause.IsVisible = false;
            binding.Cancel.IsVisible = false;
            binding.Progress.IsVisible = false;
            binding.Status.Text = installed is null ? "尚未安装，可下载后使用。" : updateAvailable ? $"已安装 v{installed.Version}，可更新到 v{binding.Remote.Version}。" : "本地已是最新版本。";
            return;
        }

        var snapshot = job.Snapshot();
        if (snapshot.IsSucceeded && installed is null)
        {
            PersistentDownloadManager.Forget(binding.Descriptor);
            binding.Download.Content = "下载";
            binding.Download.IsEnabled = true;
            binding.Pause.IsVisible = false;
            binding.Cancel.IsVisible = false;
            binding.Progress.IsVisible = false;
            binding.Status.Text = "尚未安装，可下载后使用。";
            return;
        }
        if (snapshot.IsSucceeded && installed is not null)
        {
            binding.Download.Content = "已安装";
            binding.Download.IsEnabled = false;
            binding.Pause.IsVisible = false;
            binding.Cancel.IsVisible = false;
            binding.Progress.IsVisible = false;
            binding.Status.Text = $"v{installed.Version} 已安装，重启软件后生效。";
            return;
        }

        var total = snapshot.TotalBytes;
        var received = total > 0 ? Math.Clamp(snapshot.ReceivedBytes, 0, total) : snapshot.ReceivedBytes;
        var percent = total > 0 ? received * 100d / total : 0;
        binding.Progress.Value = percent;
        binding.Progress.IsVisible = true;
        binding.Pause.IsVisible = !snapshot.IsFinished && !snapshot.IsCanceling;
        binding.Cancel.IsVisible = !snapshot.IsSucceeded && !snapshot.IsCanceling;
        binding.Pause.Content = snapshot.IsPaused ? "继续" : "暂停";
        binding.Download.Content = snapshot.IsFinished ? "重试" : "下载中";
        binding.Download.IsEnabled = snapshot.IsFinished && !snapshot.IsCanceling;

        if (snapshot.IsCanceling)
            binding.Status.Text = "正在取消并删除未完成的插件包…";
        else if (snapshot.Error is not null)
            binding.Status.Text = $"下载失败：{snapshot.Error}";
        else
        {
            var totalText = total > 0 ? FormatSize(total) : "大小未知";
            var speed = snapshot.SpeedBytesPerSecond > 0 ? $" · {FormatRate(snapshot.SpeedBytesPerSecond)}" : string.Empty;
            var state = snapshot.IsPaused ? "已暂停，可在软件重启后继续" : snapshot.Status;
            binding.Status.Text = $"{state} · {FormatSize(received)} / {totalText}{(total > 0 ? $" · {percent:0}%" : "")}{speed}";
        }
    }

    private async Task CancelDownloadAsync(CloudBinding binding)
    {
        var job = PersistentDownloadManager.GetOrRestore(binding.Descriptor);
        if (job is null) return;
        if (!await ConfirmAsync("取消下载", "确定取消 ZCode 插件下载？\n已经下载的临时文件会被删除。", "取消下载")) return;
        await PersistentDownloadManager.CancelAsync(job);
        RefreshCloudBinding(binding);
    }

    private InstalledPlugin? FindInstalledPlugin()
    {
        try
        {
            if (!Directory.Exists(_targetPluginDirectory)) return null;
            foreach (var file in Directory.EnumerateFiles(_targetPluginDirectory, "*", SearchOption.TopDirectoryOnly))
                if (TryReadPlugin(file, out var version)) return new InstalledPlugin(file, version ?? "未知");
        }
        catch { }
        return null;
    }

    private static bool TryReadPlugin(string path, out string? version)
    {
        version = null;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var manifest = archive.Entries.SingleOrDefault(entry =>
                string.Equals(entry.FullName.Replace('\\', '/').TrimStart('/'), "plugin.json", StringComparison.OrdinalIgnoreCase));
            if (manifest is null) return false;
            using var stream = manifest.Open();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || !string.Equals(id.GetString(), PluginId, StringComparison.OrdinalIgnoreCase)) return false;
            version = root.TryGetProperty("version", out var value) ? value.GetString() : null;
            return true;
        }
        catch { return false; }
    }

    private void ShowCloudMessage(string message, bool showRefresh = false)
    {
        _cloudBinding = null;
        CloudPluginsPanel.Children.Clear();
        if (!showRefresh)
        {
            CloudPluginsPanel.Children.Add(new TextBlock { Text = message, Foreground = Brush("Plugin.Muted") });
            return;
        }

        var refresh = ActionButton();
        refresh.Content = "重新检查";
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            await LoadRemotePluginAsync();
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = message, Foreground = Brush("Plugin.Muted"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(refresh, 1);
        grid.Children.Add(refresh);
        var card = Card();
        card.Child = grid;
        CloudPluginsPanel.Children.Add(card);
    }

    private static bool IsNewer(string remote, string installed)
    {
        var remoteText = remote.TrimStart('v', 'V');
        var installedText = installed.TrimStart('v', 'V');
        if (Version.TryParse(remoteText, out var remoteVersion) && Version.TryParse(installedText, out var installedVersion))
            return remoteVersion > installedVersion;
        return !string.Equals(remoteText, installedText, StringComparison.OrdinalIgnoreCase);
    }

    private Border PluginIcon() => new()
    {
        Width = 34,
        Height = 34,
        CornerRadius = new Avalonia.CornerRadius(9),
        Background = new SolidColorBrush(Color.Parse("#5A4BCB")),
        Child = new TextBlock { Text = "Z", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
    };

    private StackPanel PluginText(string title, string subtitle)
    {
        var stack = new StackPanel { Margin = new Avalonia.Thickness(0, 0, 12, 0) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brush("Plugin.Text") });
        stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 11.5, Foreground = Brush("Plugin.Muted"), Margin = new Avalonia.Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        return stack;
    }

    private static Button ActionButton(double leftMargin = 0, bool initiallyVisible = true, string? content = null) => new()
    {
        Content = content,
        Classes = { "action" },
        IsVisible = initiallyVisible,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Avalonia.Thickness(leftMargin, 0, 0, 0)
    };

    private Border Card() => new()
    {
        Background = Brush("Plugin.Surface"),
        BorderBrush = Brush("Plugin.Border"),
        BorderThickness = new Avalonia.Thickness(1),
        CornerRadius = new Avalonia.CornerRadius(10),
        Padding = new Avalonia.Thickness(14)
    };

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = CreateDialog(title, message, confirmText, true);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task ShowNoticeAsync(string title, string message) => await CreateDialog(title, message, "确定", false).ShowDialog<bool>(this);

    private Window CreateDialog(string title, string message, string confirmText, bool showCancel)
    {
        var dialog = new Window { Title = title, Width = 420, Height = 190, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var yes = new Button { Content = confirmText, MinWidth = 88 };
        var no = new Button { Content = "返回", MinWidth = 76, IsVisible = showCancel };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(no);
        actions.Children.Add(yes);
        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 20 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        panel.Children.Add(actions);
        dialog.Content = panel;
        return dialog;
    }

    private IBrush? Brush(string key) => Resources.TryGetResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.00} GB" : $"{bytes / (1024d * 1024):0} MB";
    private static string FormatRate(double bytesPerSecond) => bytesPerSecond >= 1024d * 1024 ? $"{bytesPerSecond / (1024d * 1024):0.0} MB/秒" : $"{bytesPerSecond / 1024:0} KB/秒";

    private void TitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();

    private sealed record InstalledPlugin(string Path, string Version);
    private sealed record RemotePlugin(string Id, string Name, string Version, Uri DownloadUrl, long SizeBytes, string? Sha256);

    private sealed class CloudBinding(RemotePlugin remote, DownloadDescriptor descriptor)
    {
        public RemotePlugin Remote { get; } = remote;
        public DownloadDescriptor Descriptor { get; } = descriptor;
        public required Button Download { get; init; }
        public required Button Pause { get; init; }
        public required Button Cancel { get; init; }
        public required TextBlock Status { get; init; }
        public required ProgressBar Progress { get; init; }
    }
}
