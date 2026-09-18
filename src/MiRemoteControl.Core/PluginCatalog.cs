using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using MiRemoteControl.Contracts;

namespace MiRemoteControl.Core;

public sealed class PluginCatalog : IDisposable
{
    private const int MaxBundleEntries = 2048;
    private const long MaxBundleBytes = 256L * 1024 * 1024;
    private const int MaxIconBytes = 2 * 1024 * 1024;
    private readonly Dictionary<string, LoadedPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sourcePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AssemblyLoadContext> _contexts = [];
    private readonly string _cacheDirectory;

    public List<string> Errors { get; } = [];
    public IReadOnlyList<PluginDescriptor> Descriptors => _plugins.Values.Select(p => p.Descriptor).ToArray();
    public IReadOnlyDictionary<string, string> SourcePaths => _sourcePaths;

    public PluginCatalog(string directory, string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiRemoteControl", "plugin-cache");
        if (!Directory.Exists(directory)) return;

        LoadDirectory(Path.Combine(directory, PluginFolders.Remotes), PluginKinds.Remote);
        LoadDirectory(Path.Combine(directory, PluginFolders.Targets), PluginKinds.Target);
    }

    private void LoadDirectory(string directory, string expectedKind)
    {
        if (!Directory.Exists(directory)) return;

        // A single-file package is a ZIP container with plugin.json at its root.
        // Its filename and extension are intentionally irrelevant.
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!LooksLikeZip(file)) continue;
            TryLoad(() => LoadBundle(file, expectedKind), file);
        }
    }

    private void TryLoad(Action load, string source)
    {
        try { load(); }
        catch (Exception e) { Errors.Add($"{Path.GetFileName(source)}: {e.GetBaseException().Message}"); }
    }

    private void LoadBundle(string bundlePath, string expectedKind)
    {
        using var archive = ZipFile.OpenRead(bundlePath);
        if (archive.Entries.Count is 0 or > MaxBundleEntries)
            throw new InvalidDataException($"插件包条目数必须在 1 到 {MaxBundleEntries} 之间。");
        var manifestEntries = archive.Entries.Where(entry =>
            string.Equals(entry.FullName.Replace('\\', '/'), "plugin.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifestEntries.Length != 1) throw new InvalidDataException("插件包根目录必须包含且只能包含一个 plugin.json。");
        if (archive.Entries.Sum(entry => entry.Length) > MaxBundleBytes)
            throw new InvalidDataException("插件包解压后不能超过 256 MB。");

        string manifestJson;
        using (var reader = new StreamReader(manifestEntries[0].Open())) manifestJson = reader.ReadToEnd();
        using var bundleStream = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(bundleStream)).ToLowerInvariant();
        var extractedRoot = Path.Combine(_cacheDirectory, hash);
        if (!File.Exists(Path.Combine(extractedRoot, "plugin.json"))) ExtractBundle(archive, extractedRoot);
        LoadPlugin(manifestJson, extractedRoot, Path.GetFullPath(bundlePath), expectedKind);
    }

    private void ExtractBundle(ZipArchive archive, string destination)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var staging = Path.Combine(_cacheDirectory, ".extracting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var stagingPrefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        try
        {
            foreach (var entry in archive.Entries)
            {
                var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(staging, relativePath));
                if (!target.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("插件包包含越过根目录的路径。");
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
            if (Directory.Exists(destination)) Directory.Delete(staging, true);
            else Directory.Move(staging, destination);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
    }

    private void LoadPlugin(string manifestJson, string root, string sourcePath, string expectedKind)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestJson, Wire.Json)
            ?? throw new InvalidDataException("Empty manifest.");
        if (manifest.ApiVersion != 2) throw new InvalidDataException("插件契约已分离，需要 apiVersion=2 的插件，请重新构建旧插件。");
        // The package icon is validated before the entry assembly loads so a
        // broken icon fails fast instead of after the plugin has started.
        var icon = ReadIcon(manifest, root, expectedKind);
        var entry = Path.GetFullPath(Path.Combine(root, manifest.EntryAssembly));
        if (!entry.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Entry must be inside the plugin package.");
        if (!File.Exists(entry)) throw new FileNotFoundException("插件入口程序集不存在。", entry);
        if (_plugins.ContainsKey(manifest.Id)) throw new InvalidDataException("Duplicate plugin ID.");
        var context = new PluginLoadContext(entry);
        _contexts.Add(context);
        var assembly = context.LoadFromAssemblyPath(entry);
        var type = assembly.GetType(manifest.EntryType, true)!;
        var plugin = new LoadedPlugin(Activator.CreateInstance(type)
            ?? throw new InvalidDataException("Plugin entry could not be instantiated."), expectedKind);
        if (plugin.Descriptor.Id != manifest.Id) { plugin.Dispose(); throw new InvalidDataException("Plugin ID mismatch."); }
        if (!string.Equals(plugin.Descriptor.Name, manifest.Name, StringComparison.Ordinal) ||
            !string.Equals(plugin.Descriptor.Version, manifest.Version, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plugin.Descriptor.Kind, manifest.Kind, StringComparison.OrdinalIgnoreCase))
        {
            plugin.Dispose();
            throw new InvalidDataException("Plugin manifest metadata does not match its descriptor.");
        }
        if (!string.Equals(plugin.Descriptor.Kind, expectedKind, StringComparison.OrdinalIgnoreCase))
        {
            plugin.Dispose();
            throw new InvalidDataException(
                $"插件类型为 {plugin.Descriptor.Kind}，应放入 {FolderForKind(plugin.Descriptor.Kind)} 目录。");
        }
        if (expectedKind == PluginKinds.Target)
        {
            var missing = HarnessPluginActions.DefaultActions.Select(a => a.Id)
                .Except(plugin.Descriptor.Actions.Select(a => a.Id), StringComparer.OrdinalIgnoreCase).ToArray();
            if (missing.Length > 0)
            {
                plugin.Dispose();
                throw new InvalidDataException("Harness 通用契约缺少动作：" + string.Join(", ", missing));
            }
        }
        _plugins.Add(manifest.Id, plugin);
        _sourcePaths.Add(manifest.Id, Path.GetFullPath(sourcePath));
        if (icon is not null) _icons.Add(manifest.Id, icon);
    }

    /// <summary>
    /// Reads the declared package icon. Target plugins must ship one — it is
    /// what the studio's plugin surfaces display; remote plugins may omit it.
    /// </summary>
    private static byte[]? ReadIcon(Manifest manifest, string root, string expectedKind)
    {
        if (string.IsNullOrWhiteSpace(manifest.Icon))
        {
            if (expectedKind == PluginKinds.Target)
                throw new InvalidDataException("Harness 插件必须在 plugin.json 中声明 icon（包内 PNG 图标）。");
            return null;
        }

        var iconPath = Path.GetFullPath(Path.Combine(root, manifest.Icon));
        if (!iconPath.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Icon must be inside the plugin package.");
        if (!File.Exists(iconPath)) throw new InvalidDataException($"插件图标文件不存在：{manifest.Icon}");
        var icon = File.ReadAllBytes(iconPath);
        if (icon.Length > MaxIconBytes)
            throw new InvalidDataException($"插件图标不能超过 {MaxIconBytes / 1024} KB。");
        // PNG magic: a wrong format (ico/jpg) would otherwise only fail later
        // and far away, inside the UI's decoder.
        if (icon.Length < 8 || icon[0] != 0x89 || icon[1] != 0x50 || icon[2] != 0x4E || icon[3] != 0x47)
            throw new InvalidDataException($"插件图标必须是 PNG 文件：{manifest.Icon}");
        return icon;
    }

    public byte[]? GetIcon(string pluginId) =>
        _icons.TryGetValue(pluginId, out var icon) ? icon : null;

    private static string FolderForKind(string kind) =>
        string.Equals(kind, PluginKinds.Remote, StringComparison.OrdinalIgnoreCase)
            ? PluginFolders.Remotes
            : PluginFolders.Targets;

    private static bool LooksLikeZip(string path)
    {
        try
        {
            Span<byte> signature = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Read(signature) != signature.Length || signature[0] != 0x50 || signature[1] != 0x4B) return false;
            return (signature[2] == 0x03 && signature[3] == 0x04) ||
                (signature[2] == 0x05 && signature[3] == 0x06) ||
                (signature[2] == 0x07 && signature[3] == 0x08);
        }
        catch { return false; }
    }

    public CommandResult Execute(CommandRequest request) => _plugins.TryGetValue(request.Plugin, out var plugin)
        ? plugin.Execute(request.Action, request.Arguments ?? [])
        : CommandResult.Fail("PluginNotFound", $"未找到插件：{request.Plugin}");

    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken ct = default)
    {
        if (!_plugins.TryGetValue(request.Plugin, out var plugin))
            return CommandResult.Fail("PluginNotFound", $"未找到插件：{request.Plugin}");
        return await plugin.ExecuteAsync(request.Action, request.Arguments ?? [], ct);
    }

    public bool StartHosted(string pluginId, IRemotePluginHostContext context, out string? error)
    {
        error = null;
        if (!_plugins.TryGetValue(pluginId, out var plugin))
        {
            error = $"未找到插件：{pluginId}";
            return false;
        }
        if (plugin.RemoteService is not { } hosted)
        {
            error = $"插件 {plugin.Descriptor.Name} 不支持托管生命周期。";
            return false;
        }
        try { hosted.Start(context); return true; }
        catch (Exception exception) { error = exception.GetBaseException().Message; return false; }
    }

    public void StopHosted(string? pluginId)
    {
        if (pluginId is not null && _plugins.TryGetValue(pluginId, out var plugin) && plugin.RemoteService is { } hosted)
        {
            try { hosted.Stop(); } catch { /* shutdown must continue */ }
        }
    }

    public void Dispose()
    {
        foreach (var plugin in _plugins.Values)
        {
            if (plugin.RemoteService is { } hosted) { try { hosted.Stop(); } catch { /* shutdown must continue */ } }
            try { plugin.Dispose(); } catch { /* shutdown must continue */ }
        }
        _plugins.Clear();
        _sourcePaths.Clear();
        _icons.Clear();
    }

    // Internal dispatch adapter only; it is deliberately not a public plugin
    // contract and never makes a remote plugin implement IHarnessPlugin.
    private sealed class LoadedPlugin : IDisposable
    {
        private readonly object _instance;
        public PluginDescriptor Descriptor { get; }
        public IHostedRemotePlugin? RemoteService => _instance is IRemotePlugin ? _instance as IHostedRemotePlugin : null;

        public LoadedPlugin(object instance, string kind)
        {
            _instance = instance;
            if (kind == PluginKinds.Target && instance is IHarnessPlugin harness &&
                instance is not (IRemotePlugin or IAsyncRemotePlugin or IHostedRemotePlugin))
                Descriptor = harness.Descriptor;
            else if (kind == PluginKinds.Remote && instance is IRemotePlugin remote &&
                instance is not (IHarnessPlugin or IAsyncHarnessPlugin))
                Descriptor = remote.Descriptor;
            else
            {
                (instance as IDisposable)?.Dispose();
                throw new InvalidDataException(kind == PluginKinds.Remote
                    ? "Remote entry must implement IRemotePlugin, not IHarnessPlugin."
                    : "Harness entry must implement IHarnessPlugin, not IRemotePlugin.");
            }
        }

        public CommandResult Execute(string action, IReadOnlyDictionary<string, string> arguments) => _instance switch
        {
            IHarnessPlugin harness => harness.Execute(action, arguments),
            IRemotePlugin remote => remote.Execute(action, arguments),
            _ => throw new InvalidOperationException("Unknown plugin contract.")
        };

        public Task<CommandResult> ExecuteAsync(string action, IReadOnlyDictionary<string, string> arguments, CancellationToken ct) => _instance switch
        {
            IHarnessPlugin when _instance is IAsyncHarnessPlugin harness => harness.ExecuteAsync(action, arguments, ct),
            IRemotePlugin when _instance is IAsyncRemotePlugin remote => remote.ExecuteAsync(action, arguments, ct),
            _ => Task.Run(() => Execute(action, arguments), ct)
        };

        public void Dispose() => ((IDisposable)_instance).Dispose();
    }

    private sealed record Manifest(
        string Id,
        string Name,
        string Version,
        string Kind,
        int ApiVersion,
        string EntryAssembly,
        string EntryType,
        string? Icon = null);

    private sealed class PluginLoadContext(string entry) : AssemblyLoadContext
    {
        // CsWinRT keeps process-wide state and throws
        // "Attempt to update previously set global instance" when a second
        // copy initializes. Plugins packaged for the windows TFM carry their
        // own WinRT.Runtime/SDK.NET copies, so loading them per plugin would
        // blow up the first WinRT call after another copy (e.g. the host's
        // framework assemblies) has already initialized. Route them to the
        // host's shared framework copy instead — every host in this solution
        // targets the same windows TFM, so the assemblies always resolve.
        private static readonly HashSet<string> ProcessSharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
        {
            "WinRT.Runtime",
            "Microsoft.Windows.SDK.NET"
        };

        private readonly AssemblyDependencyResolver _resolver = new(entry);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IHarnessPlugin).Assembly.GetName().Name) return typeof(IHarnessPlugin).Assembly;
            if (name.Name is not null && ProcessSharedAssemblies.Contains(name.Name)) return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}
