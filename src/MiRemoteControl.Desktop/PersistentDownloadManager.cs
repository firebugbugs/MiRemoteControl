using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MiRemoteControl.Desktop;

internal sealed record DownloadFileSpec(string RelativePath, Uri Url);

internal sealed record DownloadDescriptor(
    string Kind,
    string Id,
    string Revision,
    string DisplayName,
    long TotalBytes,
    IReadOnlyList<DownloadFileSpec> Files,
    Func<string, CancellationToken, Task> InstallAsync);

internal sealed record DownloadSnapshot(
    long ReceivedBytes,
    long TotalBytes,
    double SpeedBytesPerSecond,
    string Status,
    string? Error,
    bool IsPaused,
    bool IsFinished,
    bool IsSucceeded,
    bool IsCanceling);

internal sealed class PersistentDownloadJob
{
    private readonly object _sync = new();

    internal PersistentDownloadJob(DownloadDescriptor descriptor, string jobDirectory, StoredDownloadState? state)
    {
        Descriptor = descriptor;
        JobDirectory = jobDirectory;
        WorkingDirectory = Path.Combine(jobDirectory, "working");
        StatePath = Path.Combine(jobDirectory, "state.json");
        ReceivedBytes = Math.Max(0, state?.ReceivedBytes ?? 0);
        TotalBytes = state?.TotalBytes > 0 ? state.TotalBytes : descriptor.TotalBytes;
        CurrentFileIndex = Math.Clamp(state?.CurrentFileIndex ?? 0, 0, descriptor.Files.Count);
        Status = state?.Status ?? "准备下载";
        Error = state?.Error;
        AutoResume = state?.AutoResume ?? false;
        if (state?.Paused == true) PauseGate.Pause();
        if (!string.IsNullOrWhiteSpace(Error)) Finished = 1;
    }

    internal DownloadDescriptor Descriptor { get; }
    internal object PersistenceSync { get; } = new();
    internal string JobDirectory { get; }
    internal string WorkingDirectory { get; }
    internal string StatePath { get; }
    internal AsyncPauseGate PauseGate { get; } = new();
    internal CancellationTokenSource Cancellation { get; private set; } = new();
    internal Task? RunningTask { get; set; }
    internal long ReceivedBytes;
    internal long TotalBytes;
    internal double SpeedBytesPerSecond;
    internal int CurrentFileIndex;
    internal int Finished;
    internal int Succeeded;
    internal int CancelRequested;
    internal bool AutoResume;
    internal string Status;
    internal string? Error;

    internal bool IsFinished => Volatile.Read(ref Finished) != 0;
    internal bool IsSucceeded => Volatile.Read(ref Succeeded) != 0;
    internal bool IsCanceling => Volatile.Read(ref CancelRequested) != 0;

    internal void ResetCancellation()
    {
        Cancellation.Dispose();
        Cancellation = new CancellationTokenSource();
    }

    internal DownloadSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new DownloadSnapshot(
                Interlocked.Read(ref ReceivedBytes),
                Interlocked.Read(ref TotalBytes),
                Volatile.Read(ref SpeedBytesPerSecond),
                Status,
                Error,
                PauseGate.IsPaused,
                IsFinished,
                IsSucceeded,
                IsCanceling);
        }
    }

    internal StoredDownloadState StoredState()
    {
        lock (_sync)
        {
            return new StoredDownloadState
            {
                SchemaVersion = 1,
                Kind = Descriptor.Kind,
                Id = Descriptor.Id,
                Revision = Descriptor.Revision,
                ReceivedBytes = Interlocked.Read(ref ReceivedBytes),
                TotalBytes = Interlocked.Read(ref TotalBytes),
                CurrentFileIndex = CurrentFileIndex,
                Status = Status,
                Error = Error,
                Paused = PauseGate.IsPaused,
                AutoResume = AutoResume,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }
}

internal static class PersistentDownloadManager
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, PersistentDownloadJob> Jobs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string DownloadsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiRemoteControl", "downloads");

    internal static PersistentDownloadJob? GetOrRestore(DownloadDescriptor descriptor)
    {
        var key = Key(descriptor);
        if (Jobs.TryGetValue(key, out var current))
        {
            if (string.Equals(current.Descriptor.Revision, descriptor.Revision, StringComparison.OrdinalIgnoreCase))
                return current;
            if (!current.IsFinished) return current;
            Jobs.TryRemove(new KeyValuePair<string, PersistentDownloadJob>(key, current));
        }

        var jobDirectory = GetJobDirectory(descriptor);
        var statePath = Path.Combine(jobDirectory, "state.json");
        if (!Directory.Exists(Path.Combine(jobDirectory, "working"))) return null;

        StoredDownloadState? state;
        try
        {
            state = File.Exists(statePath)
                ? JsonSerializer.Deserialize<StoredDownloadState>(File.ReadAllText(statePath), JsonOptions)
                : null;
        }
        catch
        {
            state = null;
        }

        state ??= new StoredDownloadState
        {
            SchemaVersion = 1,
            Kind = descriptor.Kind,
            Id = descriptor.Id,
            Revision = descriptor.Revision,
            TotalBytes = descriptor.TotalBytes,
            Paused = true,
            AutoResume = false,
            Status = "已恢复未完成下载",
            CurrentFileIndex = InferCurrentFileIndex(descriptor, Path.Combine(jobDirectory, "working"))
        };

        if (state is null || state.SchemaVersion != 1 ||
            !string.Equals(state.Kind, descriptor.Kind, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(state.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase)) return null;

        if (!string.Equals(state.Revision, descriptor.Revision, StringComparison.OrdinalIgnoreCase))
        {
            DeleteJobDirectory(jobDirectory);
            return null;
        }

        var restored = new PersistentDownloadJob(descriptor, jobDirectory, state);
        Interlocked.Exchange(ref restored.ReceivedBytes, ComputeDownloadedBytes(restored));
        if (!Jobs.TryAdd(key, restored)) return Jobs.TryGetValue(key, out current) ? current : null;

        if (state.AutoResume && !state.Paused && string.IsNullOrWhiteSpace(state.Error)) StartInternal(restored);
        return restored;
    }

    internal static PersistentDownloadJob StartOrResume(DownloadDescriptor descriptor)
    {
        var key = Key(descriptor);
        var job = GetOrRestore(descriptor);
        if (job is null)
        {
            var created = new PersistentDownloadJob(descriptor, GetJobDirectory(descriptor), null);
            job = Jobs.GetOrAdd(key, created);
        }

        if (job.IsSucceeded || (job.RunningTask is { IsCompleted: false } && !job.PauseGate.IsPaused)) return job;
        StartInternal(job);
        return job;
    }

    internal static void ImportLegacyDownloads(DownloadDescriptor descriptor)
    {
        var jobDirectory = GetJobDirectory(descriptor);
        var workingDirectory = Path.Combine(jobDirectory, "working");
        if (Directory.Exists(workingDirectory)) return;

        try
        {
            var kindRoot = Path.GetFullPath(Path.Combine(DownloadsRoot, descriptor.Kind));
            if (!Directory.Exists(kindRoot)) return;
            var prefix = descriptor.Id + ".";
            var candidate = Directory.EnumerateDirectories(kindRoot, "*.download", SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                           name.EndsWith(".download", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(DirectorySize)
                .FirstOrDefault();
            if (candidate is null) return;

            Directory.CreateDirectory(jobDirectory);
            Directory.Move(candidate, workingDirectory);
        }
        catch { }
    }

    internal static void TogglePause(PersistentDownloadJob job)
    {
        if (job.IsFinished && string.IsNullOrWhiteSpace(job.Error)) return;

        if (job.PauseGate.IsPaused)
        {
            job.PauseGate.Resume();
            job.AutoResume = true;
            if (job.RunningTask is null || job.RunningTask.IsCompleted) StartInternal(job);
            else Persist(job);
        }
        else
        {
            job.PauseGate.Pause();
            job.AutoResume = false;
            job.Status = "已暂停";
            Volatile.Write(ref job.SpeedBytesPerSecond, 0);
            Persist(job);
        }
    }

    internal static async Task CancelAsync(PersistentDownloadJob job)
    {
        if (job.IsSucceeded) return;
        Interlocked.Exchange(ref job.CancelRequested, 1);
        job.AutoResume = false;
        job.Status = "正在取消…";
        job.PauseGate.Resume();
        job.Cancellation.Cancel();

        if (job.RunningTask is { } task)
        {
            try { await task.ConfigureAwait(true); }
            catch { }
        }

        DeleteJobDirectory(job.JobDirectory);
        DeleteLegacyDirectories(job.Descriptor);
        Jobs.TryRemove(new KeyValuePair<string, PersistentDownloadJob>(Key(job.Descriptor), job));
    }

    internal static void Forget(DownloadDescriptor descriptor)
    {
        if (Jobs.TryRemove(Key(descriptor), out var job) && !job.IsFinished)
        {
            Interlocked.Exchange(ref job.CancelRequested, 1);
            job.PauseGate.Resume();
            job.Cancellation.Cancel();
        }
        DeleteJobDirectory(GetJobDirectory(descriptor));
    }

    private static void StartInternal(PersistentDownloadJob job)
    {
        if (job.RunningTask is { IsCompleted: false })
        {
            job.PauseGate.Resume();
            job.AutoResume = true;
            Persist(job);
            return;
        }

        Directory.CreateDirectory(job.WorkingDirectory);
        job.ResetCancellation();
        Interlocked.Exchange(ref job.CancelRequested, 0);
        Interlocked.Exchange(ref job.Finished, 0);
        Interlocked.Exchange(ref job.Succeeded, 0);
        Volatile.Write(ref job.SpeedBytesPerSecond, 0);
        job.Error = null;
        job.Status = "准备下载";
        job.AutoResume = true;
        job.PauseGate.Resume();
        Interlocked.Exchange(ref job.ReceivedBytes, ComputeDownloadedBytes(job));
        Persist(job);
        job.RunningTask = Task.Run(() => DownloadAsync(job));
    }

    private static async Task DownloadAsync(PersistentDownloadJob job)
    {
        var cancellationToken = job.Cancellation.Token;
        var stopwatch = Stopwatch.StartNew();
        var sessionStartBytes = Interlocked.Read(ref job.ReceivedBytes);
        var lastPersistedAt = Stopwatch.GetTimestamp();

        try
        {
            for (var index = job.CurrentFileIndex; index < job.Descriptor.Files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                job.CurrentFileIndex = index;
                var file = job.Descriptor.Files[index];
                job.Status = $"正在下载 {file.RelativePath}";
                Persist(job);

                var target = SafeWorkingPath(job.WorkingDirectory, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var offset = File.Exists(target) ? new FileInfo(target).Length : 0;
                var downloadResponse = await SendDownloadRequestAsync(file.Url, offset, cancellationToken).ConfigureAwait(false);
                offset = downloadResponse.Offset;
                using (var response = downloadResponse.Response)
                {
                    response.EnsureSuccessStatusCode();
                    var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                    if (offset > 0 && !append)
                    {
                        Interlocked.Add(ref job.ReceivedBytes, -offset);
                        offset = 0;
                    }

                    if (Interlocked.Read(ref job.TotalBytes) <= 0 && job.Descriptor.Files.Count == 1 &&
                        response.Content.Headers.ContentLength is { } contentLength)
                        Interlocked.Exchange(ref job.TotalBytes, contentLength + offset);

                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var output = new FileStream(
                        target,
                        append ? FileMode.Append : FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        1024 * 1024,
                        useAsync: true);
                    var buffer = new byte[1024 * 1024];
                    int count;
                    while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await job.PauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        var received = Interlocked.Add(ref job.ReceivedBytes, count);
                        var elapsed = stopwatch.Elapsed.TotalSeconds;
                        if (elapsed > 0)
                            Volatile.Write(ref job.SpeedBytesPerSecond, Math.Max(0, received - sessionStartBytes) / elapsed);

                        if (Stopwatch.GetElapsedTime(lastPersistedAt) >= TimeSpan.FromSeconds(1))
                        {
                            Persist(job);
                            lastPersistedAt = Stopwatch.GetTimestamp();
                        }
                    }
                }

                job.CurrentFileIndex = index + 1;
                Persist(job);
            }

            cancellationToken.ThrowIfCancellationRequested();
            job.Status = "正在安装…";
            Persist(job);
            await job.Descriptor.InstallAsync(job.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Interlocked.Exchange(ref job.Succeeded, 1);
            Interlocked.Exchange(ref job.Finished, 1);
            Volatile.Write(ref job.SpeedBytesPerSecond, 0);
            job.AutoResume = false;
            job.Error = null;
            job.Status = "安装完成";
            DeleteJobDirectory(job.JobDirectory);
            DeleteLegacyDirectories(job.Descriptor);
        }
        catch (OperationCanceledException) when (job.IsCanceling)
        {
            job.Status = "已取消";
            job.Error = null;
        }
        catch (Exception exception)
        {
            job.AutoResume = false;
            job.Error = exception.Message;
            job.Status = "下载失败";
        }
        finally
        {
            Interlocked.Exchange(ref job.Finished, 1);
            Volatile.Write(ref job.SpeedBytesPerSecond, 0);
            if (job.IsCanceling)
            {
                DeleteJobDirectory(job.JobDirectory);
                Jobs.TryRemove(new KeyValuePair<string, PersistentDownloadJob>(Key(job.Descriptor), job));
            }
            else if (!job.IsSucceeded)
            {
                Persist(job);
            }
        }
    }

    private static async Task<(HttpResponseMessage Response, long Offset)> SendDownloadRequestAsync(
        Uri url, long offset, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        try
        {
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (offset <= 0 || response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
                return (response, offset);

            response.Dispose();
            return (await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false), 0);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static long ComputeDownloadedBytes(PersistentDownloadJob job)
    {
        long total = 0;
        foreach (var file in job.Descriptor.Files)
        {
            try
            {
                var path = SafeWorkingPath(job.WorkingDirectory, file.RelativePath);
                if (File.Exists(path)) total += new FileInfo(path).Length;
            }
            catch { }
        }
        var expected = Interlocked.Read(ref job.TotalBytes);
        return expected > 0 ? Math.Min(total, expected) : total;
    }

    private static int InferCurrentFileIndex(DownloadDescriptor descriptor, string workingDirectory)
    {
        for (var index = descriptor.Files.Count - 1; index >= 0; index--)
        {
            try
            {
                var path = SafeWorkingPath(workingDirectory, descriptor.Files[index].RelativePath);
                if (File.Exists(path) && new FileInfo(path).Length > 0) return index;
            }
            catch { }
        }
        return 0;
    }

    private static void Persist(PersistentDownloadJob job)
    {
        if (job.IsSucceeded || job.IsCanceling) return;
        try
        {
            lock (job.PersistenceSync)
            {
                Directory.CreateDirectory(job.JobDirectory);
                Directory.CreateDirectory(job.WorkingDirectory);
                var temporary = job.StatePath + ".tmp";
                var json = JsonSerializer.Serialize(job.StoredState(), JsonOptions);
                File.WriteAllText(temporary, json);
                File.Move(temporary, job.StatePath, overwrite: true);
            }
        }
        catch { }
    }

    private static string SafeWorkingPath(string workingDirectory, string relativePath)
    {
        var root = Path.GetFullPath(workingDirectory);
        var target = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载文件路径不安全。");
        return target;
    }

    private static string GetJobDirectory(DownloadDescriptor descriptor)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safeId = string.Concat(descriptor.Id.Select(character => invalid.Contains(character) ? '_' : character));
        return Path.GetFullPath(Path.Combine(DownloadsRoot, descriptor.Kind, safeId));
    }

    private static void DeleteJobDirectory(string jobDirectory)
    {
        try
        {
            var root = Path.GetFullPath(DownloadsRoot);
            var target = Path.GetFullPath(jobDirectory);
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
        catch { }
    }

    private static void DeleteLegacyDirectories(DownloadDescriptor descriptor)
    {
        try
        {
            var kindRoot = Path.GetFullPath(Path.Combine(DownloadsRoot, descriptor.Kind));
            if (!Directory.Exists(kindRoot)) return;
            var prefix = descriptor.Id + ".";
            foreach (var directory in Directory.EnumerateDirectories(kindRoot, "*.download", SearchOption.TopDirectoryOnly))
            {
                var full = Path.GetFullPath(directory);
                var name = Path.GetFileName(full);
                if (!full.StartsWith(kindRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".download", StringComparison.OrdinalIgnoreCase)) continue;
                Directory.Delete(full, recursive: true);
            }
        }
        catch { }
    }

    private static long DirectorySize(string directory)
    {
        try { return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length); }
        catch { return 0; }
    }

    private static string Key(DownloadDescriptor descriptor) => $"{descriptor.Kind}:{descriptor.Id}";

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MiRemoteStudio", "1.0"));
        return client;
    }
}

internal sealed class AsyncPauseGate
{
    private readonly object _sync = new();
    private TaskCompletionSource<bool> _resume = CompletedSource();
    private bool _paused;

    internal bool IsPaused
    {
        get { lock (_sync) return _paused; }
    }

    internal void Pause()
    {
        lock (_sync)
        {
            if (_paused) return;
            _paused = true;
            _resume = NewSource();
        }
    }

    internal void Resume()
    {
        lock (_sync)
        {
            if (!_paused) return;
            _paused = false;
            _resume.TrySetResult(true);
        }
    }

    internal async Task WaitAsync(CancellationToken cancellationToken)
    {
        Task wait;
        lock (_sync) wait = _paused ? _resume.Task : Task.CompletedTask;
        await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TaskCompletionSource<bool> NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> CompletedSource()
    {
        var source = NewSource();
        source.TrySetResult(true);
        return source;
    }
}

internal sealed class StoredDownloadState
{
    public int SchemaVersion { get; set; }
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public string Revision { get; set; } = "";
    public long ReceivedBytes { get; set; }
    public long TotalBytes { get; set; }
    public int CurrentFileIndex { get; set; }
    public string Status { get; set; } = "准备下载";
    public string? Error { get; set; }
    public bool Paused { get; set; }
    public bool AutoResume { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
