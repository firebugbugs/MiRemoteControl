using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MiRemoteControl.Desktop.Infrastructure;

namespace MiRemoteControl.Desktop;

/// <summary>
/// Remote-plugin counterpart of <see cref="PluginManagerWindow"/>. The one
/// behavioral difference: selecting the active remote plugin happens here
/// (live, via core remote.driver.select) instead of on the main window.
/// </summary>
public partial class RemotePluginManagerWindow : Window
{
    private static readonly MarketplaceSource[] Marketplace =
    [
        new("mrc.remote.xiaomi", new Uri("https://download.cheems.cn/v1/manifests/plugins/mrc.remote.xiaomi/stable.json"))
    ];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _remotePluginDirectory;
    private readonly Func<string, Task<(bool Success, string Message)>> _selectDriver;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, RemotePlugin> _remotePlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CloudBinding> _cloudBindings = new(StringComparer.OrdinalIgnoreCase);
    private string _installedSignature = "";
    private string _activePluginId;

    public RemotePluginManagerWindow() : this(
        Path.Combine(AppContext.BaseDirectory, "plugins", "remotes"), null, _ => Task.FromResult((false, "占位构造不可用。")))
    {
    }

    public RemotePluginManagerWindow(
        string remotePluginDirectory,
        string? activePluginId,
        Func<string, Task<(bool Success, string Message)>> selectDriver)
    {
        _remotePluginDirectory = Path.GetFullPath(remotePluginDirectory);
        _activePluginId = activePluginId ?? "";
        _selectDriver = selectDriver;
        InitializeComponent();
        Directory.CreateDirectory(_remotePluginDirectory);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshUi();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        RefreshLocalPlugins(force: true);
        ShowCloudMessage("正在读取插件更新信息…");
        Opened += async (_, _) => await LoadRemotePluginsAsync();
    }

    private async Task LoadRemotePluginsAsync()
    {
        CloudPluginsPanel.Children.Clear();
        _remotePlugins.Clear();
        _cloudBindings.Clear();
        var errors = new List<string>();
        foreach (var source in Marketplace)
        {
            try
            {
                using var response = await Http.GetAsync(source.ManifestUrl);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = document.RootElement;
                if (!root.TryGetProperty("available", out var available) || !available.GetBoolean()) continue;

                var id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                var name = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                var version = root.TryGetProperty("version", out var versionValue) ? versionValue.GetString() : null;
                var urlText = root.TryGetProperty("downloadUrl", out var urlValue) ? urlValue.GetString() : null;
                var size = root.TryGetProperty("sizeBytes", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
                var sha256 = root.TryGetProperty("sha256", out var hashValue) ? hashValue.GetString() : null;
                if (!string.Equals(id, source.ExpectedId, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(name) ||
                    !Uri.TryCreate(urlText, UriKind.Absolute, out var downloadUrl) ||
                    downloadUrl.Scheme != Uri.UriSchemeHttps ||
                    !downloadUrl.Host.Equals(source.ManifestUrl.Host, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("云端插件清单不完整。");

                var remote = new RemotePlugin(id!, name!, version!, downloadUrl, size, sha256, await TryLoadIconAsync(root, source));
                _remotePlugins[id!] = remote;
                BuildCloudCard(remote);
            }
            catch (Exception exception) { errors.Add($"{source.ExpectedId}: {exception.Message}"); }
        }
        if (_remotePlugins.Count == 0)
            ShowCloudMessage(errors.Count == 0 ? "当前暂无可用插件。" : $"读取更新信息失败：{string.Join("；", errors)}", showRefresh: true);
    }

    private async Task<byte[]?> TryLoadIconAsync(JsonElement manifestRoot, MarketplaceSource source)
    {
        // Same rules PluginCatalog enforces on package icons (PNG, ≤2 MB),
        // plus the same-host restriction applied to downloadUrl. Any failure
        // keeps the card usable with the letter fallback.
        if (manifestRoot.TryGetProperty("iconUrl", out var iconUrlValue) &&
            iconUrlValue.GetString() is { Length: > 0 } iconUrlText &&
            Uri.TryCreate(iconUrlText, UriKind.Absolute, out var iconUrl) &&
            iconUrl.Scheme == Uri.UriSchemeHttps &&
            iconUrl.Host.Equals(source.ManifestUrl.Host, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var icon = await Http.GetByteArrayAsync(iconUrl);
                if (icon is { Length: > 8 and <= 2 * 1024 * 1024 } &&
                    icon[0] == 0x89 && icon[1] == 0x50 && icon[2] == 0x4E && icon[3] == 0x47)
                    return icon;
            }
            catch { }
        }
        return null;
    }

    private void RefreshUi()
    {
        RefreshLocalPlugins(force: false);
        foreach (var binding in _cloudBindings.Values) RefreshCloudBinding(binding);
    }

    private void RefreshLocalPlugins(bool force)
    {
        var installedPlugins = FindInstalledPlugins();
        var signature = string.Join('|', installedPlugins.Select(installed =>
            $"{installed.Path}:{installed.Id}:{installed.Version}:{File.GetLastWriteTimeUtc(installed.Path).Ticks}")) + "|" + _activePluginId;
        if (!force && string.Equals(signature, _installedSignature, StringComparison.Ordinal)) return;
        _installedSignature = signature;
        LocalPluginsPanel.Children.Clear();

        if (installedPlugins.Count == 0)
        {
            LocalPluginsPanel.Children.Add(new TextBlock { Text = "未安装遥控器插件", Foreground = Brush("Plugin.Muted") });
            return;
        }

        foreach (var installed in installedPlugins)
        {
            var use = ActionButton();
            var active = installed.Id.Equals(_activePluginId, StringComparison.OrdinalIgnoreCase);
            use.Content = active ? "使用中" : "使用";
            use.IsEnabled = !active;
            use.Click += async (_, _) =>
            {
                use.IsEnabled = false;
                use.Content = "切换中…";
                var (success, message) = await _selectDriver(installed.Id);
                if (success)
                {
                    _activePluginId = installed.Id;
                }
                else
                {
                    use.IsEnabled = true;
                    use.Content = "使用";
                    await ShowNoticeAsync("切换失败", message);
                }
                _installedSignature = "";
                RefreshLocalPlugins(force: true);
                if (_cloudBindings.TryGetValue(installed.Id, out var binding)) RefreshCloudBinding(binding);
            };

            var remove = ActionButton(8);
            remove.Content = "卸载";
            // The hosted remote plugin holds open handles into its extracted
            // cache; removing the package of the running driver would leave
            // the session without a remote until restart.
            remove.IsEnabled = !active;
            if (active) ToolTip.SetTip(remove, "使用中的遥控器插件不能卸载");
            remove.Click += async (_, _) =>
            {
                if (!await ConfirmAsync("卸载插件", $"确定卸载 {installed.Name}？\n软件重启后该插件不再加载。", "确认卸载")) return;
                try
                {
                    var full = Path.GetFullPath(installed.Path);
                    if (!string.Equals(Directory.GetParent(full)?.FullName, _remotePluginDirectory, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("插件路径不安全。");
                    File.Delete(full);
                    if (_remotePlugins.TryGetValue(installed.Id, out var remote))
                        PersistentDownloadManager.Forget(CreateDescriptor(remote));
                    _installedSignature = "";
                    RefreshLocalPlugins(force: true);
                    if (_cloudBindings.TryGetValue(installed.Id, out var binding)) RefreshCloudBinding(binding);
                }
                catch (Exception exception)
                {
                    await ShowNoticeAsync("卸载失败", exception.Message);
                }
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("42,*,Auto,Auto") };
            grid.Children.Add(PluginIcon(installed.IconPng, installed.Name));
            var text = PluginText(installed.Name, $"{installed.Id} · v{installed.Version}{(active ? " · 当前使用" : "")}");
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(use, 2);
            grid.Children.Add(use);
            Grid.SetColumn(remove, 3);
            grid.Children.Add(remove);
            var card = Card();
            card.Child = grid;
            LocalPluginsPanel.Children.Add(card);
        }
    }

    private void BuildCloudCard(RemotePlugin remote)
    {
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
        _cloudBindings[remote.Id] = binding;

        binding.Download.Click += (_, _) => PersistentDownloadManager.StartOrResume(descriptor);
        binding.Pause.Click += (_, _) =>
        {
            if (PersistentDownloadManager.GetOrRestore(descriptor) is { } job)
                PersistentDownloadManager.TogglePause(job);
        };
        binding.Cancel.Click += async (_, _) => await CancelDownloadAsync(binding);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("42,*,Auto,Auto,Auto") };
        header.Children.Add(PluginIcon(remote.IconPng, remote.Name));
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
            "remote-plugins",
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

                ValidatePluginPackage(package, remote.Id, remote.Version);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_remotePluginDirectory);
                var target = Path.Combine(_remotePluginDirectory, remote.Id + ".mrcplugin");
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

    private static void ValidatePluginPackage(string packagePath, string expectedId, string expectedVersion)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var manifests = archive.Entries.Where(entry =>
            string.Equals(entry.FullName.Replace('\\', '/').TrimStart('/'), "plugin.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1) throw new InvalidDataException("插件包必须包含一个根目录 plugin.json。");
        using var stream = manifests[0].Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id) || !string.Equals(id.GetString(), expectedId, StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("apiVersion", out var apiVersion) || apiVersion.GetInt32() != 2)
            throw new InvalidDataException("插件包与更新清单不匹配。");
        var version = root.TryGetProperty("version", out var versionValue) ? versionValue.GetString() : null;
        if (!string.Equals(version, expectedVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件包版本与更新清单不一致。");
    }

    private void RefreshCloudBinding(CloudBinding binding)
    {
        var installed = FindInstalledPlugins().FirstOrDefault(plugin =>
            plugin.Id.Equals(binding.Remote.Id, StringComparison.OrdinalIgnoreCase));
        var job = PersistentDownloadManager.GetOrRestore(binding.Descriptor);
        if (job is null)
        {
            var updateAvailable = installed is null || IsNewer(binding.Remote.Version, installed.Version);
            binding.Download.Content = installed is null ? "下载" : updateAvailable ? "更新" : "已是最新";
            binding.Download.IsEnabled = updateAvailable;
            binding.Pause.IsVisible = false;
            binding.Cancel.IsVisible = false;
            binding.Progress.IsVisible = false;
            binding.Status.Text = installed is null
                ? "尚未安装，可下载后使用。"
                : updateAvailable
                    ? $"已安装 v{installed.Version}，可更新到 v{binding.Remote.Version}，安装后重启软件生效。"
                    : "本地已是最新版本。";
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
            binding.Status.Text = $"v{installed.Version} 已安装，重启软件后在本地列表中选择使用。";
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
        if (!await ConfirmAsync("取消下载", $"确定取消 {binding.Remote.Name} 下载？\n已经下载的临时文件会被删除。", "取消下载")) return;
        await PersistentDownloadManager.CancelAsync(job);
        RefreshCloudBinding(binding);
    }

    private IReadOnlyList<InstalledPlugin> FindInstalledPlugins()
    {
        var plugins = new List<InstalledPlugin>();
        try
        {
            if (!Directory.Exists(_remotePluginDirectory)) return plugins;
            foreach (var file in Directory.EnumerateFiles(_remotePluginDirectory, "*", SearchOption.TopDirectoryOnly))
                if (TryReadPlugin(file, out var plugin)) plugins.Add(plugin!);
        }
        catch { }
        return plugins.OrderBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool TryReadPlugin(string path, out InstalledPlugin? plugin)
    {
        plugin = null;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var manifest = archive.Entries.SingleOrDefault(entry =>
                string.Equals(entry.FullName.Replace('\\', '/').TrimStart('/'), "plugin.json", StringComparison.OrdinalIgnoreCase));
            if (manifest is null) return false;
            using var stream = manifest.Open();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) return false;
            var name = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            var version = root.TryGetProperty("version", out var value) ? value.GetString() : null;
            var icon = ReadIconEntry(archive, root);
            plugin = new InstalledPlugin(path, id, string.IsNullOrWhiteSpace(name) ? id : name, version ?? "未知", icon);
            return true;
        }
        catch { return false; }
    }

    private static byte[]? ReadIconEntry(ZipArchive archive, JsonElement manifestRoot)
    {
        if (!manifestRoot.TryGetProperty("icon", out var iconValue) ||
            iconValue.GetString() is not { Length: > 0 } iconName) return null;
        // Same containment rule the host enforces; here it only guards against
        // a manifest pointing somewhere nonsensical inside its own package.
        var relative = iconName.Replace('\\', '/').TrimStart('/');
        var entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.FullName.Replace('\\', '/').TrimStart('/'), relative, StringComparison.OrdinalIgnoreCase));
        if (entry is not { Length: > 0 }) return null;
        using var iconStream = entry.Open();
        using var buffer = new MemoryStream();
        iconStream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private void ShowCloudMessage(string message, bool showRefresh = false)
    {
        _cloudBindings.Clear();
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
            await LoadRemotePluginsAsync();
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

    private Control PluginIcon(byte[]? iconPng, string name) =>
        PluginIconView.Create(PluginIconView.Decode(iconPng), name);

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

    private sealed record InstalledPlugin(string Path, string Id, string Name, string Version, byte[]? IconPng);
    private sealed record RemotePlugin(string Id, string Name, string Version, Uri DownloadUrl, long SizeBytes, string? Sha256, byte[]? IconPng = null);
    private sealed record MarketplaceSource(string ExpectedId, Uri ManifestUrl);

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
