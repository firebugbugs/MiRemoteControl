using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MiRemoteControl.Desktop;

public partial class ModelManagerWindow : Window
{
    private readonly string _modelDirectory;
    private readonly string? _activeModelId;
    private readonly Dictionary<string, CardBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _progressTimer;

    private static readonly CloudModel[] CloudModels =
    [
        new("sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25", "Qwen3-ASR 0.6B INT8", "速度与资源占用更均衡，推荐日常使用", "csukuangfj2/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25", 987_015_347),
        new("sherpa-onnx-qwen3-asr-1.7B-int8", "Qwen3-ASR 1.7B INT8", "准确率更高，需要更多内存与磁盘空间", "thieunv/sherpa-onnx-qwen3-asr-1.7B-int8", 2_404_222_421)
    ];

    private static readonly string[] RequiredFiles =
    [
        "conv_frontend.onnx", "encoder.int8.onnx", "decoder.int8.onnx",
        "tokenizer/merges.txt", "tokenizer/tokenizer_config.json", "tokenizer/vocab.json"
    ];

    public ModelManagerWindow() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiRemoteControl", "models"), null)
    {
    }

    public ModelManagerWindow(string modelDirectory, string? activeModelId)
    {
        _modelDirectory = Path.GetFullPath(modelDirectory);
        _activeModelId = activeModelId;
        InitializeComponent();
        Directory.CreateDirectory(_modelDirectory);

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => RefreshBindings();
        _progressTimer.Start();
        Closed += (_, _) => _progressTimer.Stop();
        RefreshModels();
    }

    private void RefreshModels()
    {
        LocalModelsPanel.Children.Clear();
        foreach (var directory in Directory.EnumerateDirectories(_modelDirectory).OrderBy(Path.GetFileName))
            LocalModelsPanel.Children.Add(CreateLocalCard(directory));
        if (LocalModelsPanel.Children.Count == 0)
            LocalModelsPanel.Children.Add(new TextBlock { Text = "暂无本地模型", Foreground = Brush("Model.Muted") });

        _bindings.Clear();
        CloudModelsPanel.Children.Clear();
        foreach (var model in CloudModels)
            CloudModelsPanel.Children.Add(CreateCloudCard(model));
        RefreshBindings();
    }

    private Control CreateLocalCard(string directory)
    {
        var id = Path.GetFileName(directory);
        var size = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        var active = string.Equals(id, _activeModelId, StringComparison.OrdinalIgnoreCase);
        var remove = new Button
        {
            Content = active ? "使用中" : "删除",
            Classes = { "action" },
            IsEnabled = !active,
            VerticalAlignment = VerticalAlignment.Center
        };
        var card = Card();
        remove.Click += async (_, _) =>
        {
            if (!await ConfirmAsync("删除模型", $"确定删除“{id}”？\n删除后需要重新下载才能使用。", "确认删除")) return;
            try
            {
                var full = Path.GetFullPath(directory);
                if (!string.Equals(Directory.GetParent(full)?.FullName, _modelDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("模型目录不安全。");
                Directory.Delete(full, recursive: true);
                var model = CloudModels.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (model is not null) PersistentDownloadManager.Forget(CreateDescriptor(model));
                RefreshModels();
            }
            catch (Exception exception)
            {
                await ShowNoticeAsync("删除失败", exception.Message);
            }
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(ModelText(id, $"本地模型 · {FormatSize(size)}{(active ? " · 当前使用" : "")}"));
        Grid.SetColumn(remove, 1);
        grid.Children.Add(remove);
        card.Child = grid;
        return card;
    }

    private Control CreateCloudCard(CloudModel model)
    {
        var descriptor = CreateDescriptor(model);
        PersistentDownloadManager.ImportLegacyDownloads(descriptor);
        PersistentDownloadManager.GetOrRestore(descriptor);
        var binding = new CardBinding(model, descriptor)
        {
            Download = ActionButton(),
            Pause = ActionButton(8),
            Cancel = ActionButton(8, "取消下载"),
            Progress = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 7,
                IsVisible = false,
                Margin = new Avalonia.Thickness(0, 10, 0, 0)
            },
            Status = new TextBlock
            {
                FontSize = 11.5,
                Foreground = Brush("Model.Muted"),
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            }
        };
        ToolTip.SetTip(binding.Cancel, "停止下载并删除未完成的文件");
        _bindings[model.Id] = binding;

        binding.Download.Click += (_, _) => PersistentDownloadManager.StartOrResume(descriptor);
        binding.Pause.Click += (_, _) =>
        {
            if (PersistentDownloadManager.GetOrRestore(descriptor) is { } job)
                PersistentDownloadManager.TogglePause(job);
        };
        binding.Cancel.Click += async (_, _) => await CancelDownloadAsync(binding);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        header.Children.Add(ModelText(model.Name, $"云端模型 · {FormatSize(model.SizeBytes)}"));
        Grid.SetColumn(binding.Download, 1);
        header.Children.Add(binding.Download);
        Grid.SetColumn(binding.Pause, 2);
        header.Children.Add(binding.Pause);
        Grid.SetColumn(binding.Cancel, 3);
        header.Children.Add(binding.Cancel);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(binding.Status);
        stack.Children.Add(binding.Progress);
        var card = Card();
        card.Child = stack;
        RefreshBinding(binding);
        return card;
    }

    private DownloadDescriptor CreateDescriptor(CloudModel model)
    {
        var files = RequiredFiles.Select(relative =>
        {
            var escaped = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
            return new DownloadFileSpec(relative,
                new Uri($"https://huggingface.co/{model.Repository}/resolve/main/{escaped}?download=true"));
        }).ToArray();

        return new DownloadDescriptor(
            "models",
            model.Id,
            model.Repository,
            model.Name,
            model.SizeBytes,
            files,
            (workingDirectory, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var missing = RequiredFiles.Where(file => !File.Exists(
                    Path.Combine(workingDirectory, file.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
                if (missing.Length > 0)
                    throw new InvalidDataException($"模型文件不完整：{string.Join(", ", missing)}");

                var final = Path.Combine(_modelDirectory, model.Id);
                if (Directory.Exists(final)) throw new IOException("该模型已经安装。");
                Directory.CreateDirectory(_modelDirectory);
                Directory.Move(workingDirectory, final);
                return Task.CompletedTask;
            });
    }

    private async Task CancelDownloadAsync(CardBinding binding)
    {
        var job = PersistentDownloadManager.GetOrRestore(binding.Descriptor);
        if (job is null) return;
        if (!await ConfirmAsync("取消下载", $"确定取消“{binding.Model.Name}”的下载？\n已经下载的临时文件会被删除。", "取消下载")) return;
        await PersistentDownloadManager.CancelAsync(job);
        RefreshBinding(binding);
    }

    private void RefreshBindings()
    {
        foreach (var binding in _bindings.Values.ToArray()) RefreshBinding(binding);
    }

    private void RefreshBinding(CardBinding binding)
    {
        var model = binding.Model;
        if (Directory.Exists(Path.Combine(_modelDirectory, model.Id)))
        {
            binding.Download.Content = "已安装";
            binding.Download.IsEnabled = false;
            binding.Pause.IsVisible = false;
            binding.Cancel.IsVisible = false;
            binding.Progress.IsVisible = false;
            binding.Status.Text = $"{model.Description} · {FormatSize(model.SizeBytes)}";
            return;
        }

        var job = PersistentDownloadManager.GetOrRestore(binding.Descriptor);
        if (job is null)
        {
            binding.Download.Content = "下载";
            binding.Download.IsEnabled = true;
            binding.Pause.IsVisible = false;
            binding.Cancel.IsVisible = false;
            binding.Progress.IsVisible = false;
            binding.Status.Text = $"{model.Description} · {FormatSize(model.SizeBytes)}";
            return;
        }

        var snapshot = job.Snapshot();
        if (snapshot.IsSucceeded)
        {
            PersistentDownloadManager.Forget(binding.Descriptor);
            binding.Download.Content = "下载";
            binding.Download.IsEnabled = true;
            binding.Pause.IsVisible = false;
            binding.Cancel.IsVisible = false;
            binding.Progress.IsVisible = false;
            binding.Status.Text = $"{model.Description} · {FormatSize(model.SizeBytes)}";
            return;
        }
        var total = snapshot.TotalBytes > 0 ? snapshot.TotalBytes : model.SizeBytes;
        var received = Math.Clamp(snapshot.ReceivedBytes, 0, total);
        var percent = total <= 0 ? 0 : received * 100d / total;
        binding.Progress.Value = percent;
        binding.Progress.IsVisible = !snapshot.IsSucceeded;
        binding.Pause.IsVisible = !snapshot.IsFinished && !snapshot.IsCanceling;
        binding.Cancel.IsVisible = !snapshot.IsSucceeded && !snapshot.IsCanceling;
        binding.Pause.Content = snapshot.IsPaused ? "继续" : "暂停";
        binding.Download.IsEnabled = snapshot.IsFinished && !snapshot.IsSucceeded && !snapshot.IsCanceling;
        binding.Download.Content = snapshot.IsSucceeded ? "已安装" : snapshot.IsFinished ? "重试" : "下载中";

        if (snapshot.IsCanceling)
            binding.Status.Text = "正在取消并清理临时文件…";
        else if (snapshot.IsSucceeded)
            binding.Status.Text = $"安装完成 · {FormatSize(total)} / {FormatSize(total)}";
        else if (snapshot.Error is not null)
            binding.Status.Text = $"下载失败：{snapshot.Error}";
        else
        {
            var speed = snapshot.SpeedBytesPerSecond > 0 ? $" · {FormatRate(snapshot.SpeedBytesPerSecond)}" : string.Empty;
            var state = snapshot.IsPaused ? "已暂停，可在软件重启后继续" : snapshot.Status;
            binding.Status.Text = $"{state} · {FormatSize(received)} / {FormatSize(total)} · {percent:0}%{speed}";
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = CreateDialog(title, message, confirmText, true);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task ShowNoticeAsync(string title, string message) =>
        await CreateDialog(title, message, "确定", false).ShowDialog<bool>(this);

    private Window CreateDialog(string title, string message, string confirmText, bool showCancel)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
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

    private static Button ActionButton(double leftMargin = 0, string? content = null) => new()
    {
        Content = content,
        Classes = { "action" },
        IsVisible = leftMargin == 0,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Avalonia.Thickness(leftMargin, 0, 0, 0)
    };

    private Border Card() => new()
    {
        Background = Brush("Model.Surface"),
        BorderBrush = Brush("Model.Border"),
        BorderThickness = new Avalonia.Thickness(1),
        CornerRadius = new Avalonia.CornerRadius(10),
        Padding = new Avalonia.Thickness(14)
    };

    private StackPanel ModelText(string title, string subtitle)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brush("Model.Text") });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11.5,
            Foreground = Brush("Model.Muted"),
            Margin = new Avalonia.Thickness(0, 4, 12, 0),
            TextWrapping = TextWrapping.Wrap
        });
        return stack;
    }

    private IBrush? Brush(string key) => Resources.TryGetResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.00} GB"
        : $"{bytes / (1024d * 1024):0} MB";
    private static string FormatRate(double bytesPerSecond) => bytesPerSecond >= 1024d * 1024 * 1024
        ? $"{bytesPerSecond / (1024d * 1024 * 1024):0.00} GB/秒"
        : bytesPerSecond >= 1024d * 1024
            ? $"{bytesPerSecond / (1024d * 1024):0.0} MB/秒"
            : bytesPerSecond >= 1024
                ? $"{bytesPerSecond / 1024:0} KB/秒"
                : $"{bytesPerSecond:0} B/秒";

    private void TitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();

    private sealed class CardBinding(CloudModel model, DownloadDescriptor descriptor)
    {
        public CloudModel Model { get; } = model;
        public DownloadDescriptor Descriptor { get; } = descriptor;
        public required Button Download { get; init; }
        public required Button Pause { get; init; }
        public required Button Cancel { get; init; }
        public required ProgressBar Progress { get; init; }
        public required TextBlock Status { get; init; }
    }

    private sealed record CloudModel(string Id, string Name, string Description, string Repository, long SizeBytes);
}
