using System.Text;
using System.Text.Json;
using SherpaOnnx;
using Whisper.net;

namespace MiRemoteControl.Plugin.XiaomiRemote;

public sealed record WhisperModelInfo(string Id, string Name, string FileName, string Path, string Engine, string Format,
    long SizeBytes, DateTimeOffset ModifiedAt, bool Compatible, string? CompatibilityMessage, bool Selected, bool Loaded);
public sealed record WhisperModelChangeResult(bool Success, string Code, string Message, WhisperModelInfo? Model = null);
public sealed record WhisperStatus(bool Ready, bool Loading, string Model, string State, string? LastText, string? LastError,
    string ModelDirectory, string? SelectedModel, string? LoadedModel);

public sealed class WhisperVoiceService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _catalogSync = new();
    private readonly string _settingsPath;
    private WhisperFactory? _factory;
    private OfflineRecognizer? _onnxRecognizer;
    private string? _loadedEngine;
    private IReadOnlyList<WhisperModelInfo> _modelCache = [];
    private IReadOnlyDictionary<string, ModelDefinition> _definitionCache = new Dictionary<string, ModelDefinition>(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _modelCacheExpiresAt;
    private bool _loading;
    private string _state = "未添加本地语音模型";
    private string? _lastText;
    private string? _lastError;
    private string? _selectedModelId;
    private string? _loadedModelId;
    private long _loadedModelSize;
    private DateTimeOffset _loadedModelModifiedAt;
    private string _modelDisplayName = "未选择模型";

    public string ModelDirectory { get; }
    public WhisperStatus Status => new(_factory is not null || _onnxRecognizer is not null, _loading, _modelDisplayName, _state, _lastText, _lastError,
        ModelDirectory, _selectedModelId, _loadedModelId);

    public WhisperVoiceService(string? modelDirectory = null, string? settingsPath = null)
    {
        ModelDirectory = modelDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiRemoteControl", "models");
        _settingsPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiRemoteControl", "config", "voice-model.json");
        _selectedModelId = LoadSelectedModel();
    }

    public IReadOnlyList<WhisperModelInfo> Models(bool force = false)
    {
        lock (_catalogSync)
        {
            var now = DateTimeOffset.Now;
            if (!force && now < _modelCacheExpiresAt) return _modelCache;
            Directory.CreateDirectory(ModelDirectory);
            var definitions = new List<ModelDefinition>();
            try
            {
                foreach (var path in Directory.EnumerateFiles(ModelDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    var fileName = Path.GetFileName(path);
                    if (fileName.EndsWith(".download", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith(".new", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        var modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc);
                        var compatible = IsWhisperGgmlFile(path);
                        var model = new WhisperModelInfo(fileName, fileName, fileName, info.FullName,
                            compatible ? "whisper.cpp" : "unknown", compatible ? "GGML" : FileFormat(fileName), info.Length,
                            modifiedAt, compatible, compatible ? null : "未找到可加载该文件的语音引擎。",
                            string.Equals(fileName, _selectedModelId, StringComparison.OrdinalIgnoreCase),
                            string.Equals(fileName, _loadedModelId, StringComparison.OrdinalIgnoreCase) &&
                            info.Length == _loadedModelSize && modifiedAt == _loadedModelModifiedAt);
                        definitions.Add(new(model, model.Engine, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["model"] = info.FullName
                        }));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                foreach (var directory in Directory.EnumerateDirectories(ModelDirectory, "*", SearchOption.TopDirectoryOnly))
                    if (TryCreateOnnxDefinition(directory) is { } definition) definitions.Add(definition);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var unique = definitions.GroupBy(item => item.Info.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
            _definitionCache = unique.ToDictionary(item => item.Info.Id, StringComparer.OrdinalIgnoreCase);
            _modelCache = unique.Select(item => item.Info).OrderByDescending(model => model.Selected).ThenByDescending(model => model.Compatible)
                .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            _modelCacheExpiresAt = now.AddSeconds(1);
            return _modelCache;
        }
    }

    public async Task InitializeAsync()
    {
        if (_factory is not null || _onnxRecognizer is not null) return;
        await _gate.WaitAsync();
        try
        {
            if (_factory is not null || _onnxRecognizer is not null) return;
            _loading = true;
            _lastError = null;
            Directory.CreateDirectory(ModelDirectory);
            var models = Models(force: true);
            var selected = models.FirstOrDefault(model => model.Compatible &&
                string.Equals(model.Id, _selectedModelId, StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault(model => model.Compatible);
            if (selected is null)
            {
                _modelDisplayName = "未选择模型";
                _state = "未添加本地语音模型，请打开模型位置并添加模型";
                return;
            }
            LoadInitialModel(selected);
        }
        catch (Exception exception)
        {
            _lastError = $"{exception.GetType().Name}: {exception.Message}";
            _state = "本地语音模型加载失败，使用 Windows 识别器";
            VoiceDiagnostics.Log($"WHISPER init-error {_lastError}");
        }
        finally { _loading = false; _gate.Release(); }
    }

    public async Task<WhisperModelChangeResult> SelectModelAsync(string modelId)
    {
        await _gate.WaitAsync();
        try
        {
            var definition = ResolveModel(modelId);
            if (definition is null) return new(false, "ModelNotFound", $"模型不存在：{modelId}");
            var model = definition.Info;
            if (!model.Compatible) return new(false, "UnsupportedModel", model.CompatibilityMessage ?? "没有可加载该模型的语音引擎。");
            if (ModelMatchesLoaded(model))
                return new(true, "Ok", $"当前已在使用 {model.Name}。", model);

            _loading = true;
            _lastError = null;
            _state = $"正在加载模型 {model.Name}…";
            WhisperFactory? candidateWhisper = null;
            OfflineRecognizer? candidateOnnx = null;
            try
            {
                if (definition.Engine == "whisper.cpp") candidateWhisper = WhisperFactory.FromPath(definition.Files["model"]);
                else candidateOnnx = CreateOnnxRecognizer(definition);
                SaveSelectedModel(model.Id);
                var previousWhisper = _factory;
                var previousOnnx = _onnxRecognizer;
                _factory = candidateWhisper;
                _onnxRecognizer = candidateOnnx;
                candidateWhisper = null;
                candidateOnnx = null;
                _selectedModelId = model.Id;
                _loadedModelId = model.Id;
                _loadedEngine = definition.Engine;
                _loadedModelSize = model.SizeBytes;
                _loadedModelModifiedAt = model.ModifiedAt;
                _modelDisplayName = model.Name;
                _state = $"本地语音模型已就绪 · {model.Name}";
                previousWhisper?.Dispose();
                previousOnnx?.Dispose();
                var selected = Models(force: true).First(item => string.Equals(item.Id, model.Id, StringComparison.OrdinalIgnoreCase));
                VoiceDiagnostics.Log($"ASR model-selected id={model.Id} engine={definition.Engine} format={model.Format} bytes={model.SizeBytes} runtime=cpu");
                return new(true, "Ok", $"已切换到语音模型 {model.Name}。", selected);
            }
            catch (Exception exception)
            {
                candidateWhisper?.Dispose();
                candidateOnnx?.Dispose();
                _lastError = $"{exception.GetType().Name}: {exception.Message}";
                _state = _factory is null && _onnxRecognizer is null
                    ? "模型加载失败，使用 Windows 识别器"
                    : $"模型切换失败，继续使用 {_modelDisplayName}";
                VoiceDiagnostics.Log($"ASR model-select-error id={model.Id} engine={definition.Engine} error={_lastError}");
                return new(false, "ModelLoadFailed", $"模型加载失败：{exception.Message}");
            }
        }
        finally { _loading = false; _gate.Release(); }
    }

    public async Task<string?> RecognizePcmAsync(byte[] pcm)
    {
        if (_factory is null && _onnxRecognizer is null) await InitializeAsync();
        if ((_factory is null && _onnxRecognizer is null) || pcm.Length == 0) return null;
        await _gate.WaitAsync();
        try
        {
            _lastText = null;
            _lastError = null;
            _state = $"{_modelDisplayName} 正在识别…";
            var started = DateTimeOffset.Now;
            if (_factory is not null)
            {
                using var processor = _factory.CreateBuilder().WithLanguage("zh").Build();
                using var wav = CreateWave(pcm);
                var text = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(wav)) text.Append(segment.Text);
                _lastText = text.ToString().Trim();
            }
            else
            {
                var recognizer = _onnxRecognizer!;
                var engine = _loadedEngine;
                _lastText = await Task.Run(() => RecognizeOnnx(recognizer, engine, pcm));
            }
            _state = string.IsNullOrWhiteSpace(_lastText) ? "未识别出文字" : "语音识别完成";
            VoiceDiagnostics.Log($"ASR result model={_loadedModelId} engine={_loadedEngine} elapsedMs={(DateTimeOffset.Now - started).TotalMilliseconds:F0} text={_lastText ?? "<none>"}");
            return _lastText;
        }
        catch (Exception exception)
        {
            _lastError = $"{exception.GetType().Name}: {exception.Message}";
            _state = "语音识别失败";
            VoiceDiagnostics.Log($"ASR recognize-error model={_loadedModelId} engine={_loadedEngine} {_lastError}");
            return null;
        }
        finally { _gate.Release(); }
    }

    public static bool IsWhisperGgmlFile(string path)
    {
        try
        {
            Span<byte> magic = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Read(magic) == magic.Length && magic.SequenceEqual("lmgg"u8);
        }
        catch { return false; }
    }

    private ModelDefinition? ResolveModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || !string.Equals(modelId, Path.GetFileName(modelId), StringComparison.Ordinal)) return null;
        Models(force: true);
        return _definitionCache.GetValueOrDefault(modelId);
    }

    private ModelDefinition? TryCreateOnnxDefinition(string directory)
    {
        try
        {
            var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var allFiles = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
            var allDirectories = Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).ToArray();
            var hints = string.Join(' ', allDirectories.Select(Path.GetFileName).Prepend(name));
            var convFrontend = FindOnnxRole(allFiles, "conv_frontend");
            var encoder = FindOnnxRole(allFiles, "encoder");
            var decoder = FindOnnxRole(allFiles, "decoder");
            var joiner = FindOnnxRole(allFiles, "joiner");
            var tokens = FindTokens(allFiles);
            var tokenizer = allDirectories.FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), "tokenizer", StringComparison.OrdinalIgnoreCase));
            var looksLikeQwen = HasHint(hints, "qwen3-asr") || HasHint(hints, "qwen3_asr") || convFrontend is not null;
            if (looksLikeQwen)
            {
                var missing = new List<string>();
                if (convFrontend is null) missing.Add("conv_frontend.onnx");
                if (encoder is null) missing.Add("encoder(.int8).onnx");
                if (decoder is null) missing.Add("decoder(.int8).onnx");
                if (tokenizer is null) missing.Add("tokenizer/");
                var compatible = missing.Count == 0;
                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (convFrontend is not null) files["convFrontend"] = convFrontend;
                if (encoder is not null) files["encoder"] = encoder;
                if (decoder is not null) files["decoder"] = decoder;
                if (tokenizer is not null) files["tokenizer"] = tokenizer;
                var format = IsInt8(encoder) ? "Qwen3-ASR ONNX INT8" : "Qwen3-ASR ONNX";
                return CreateDirectoryDefinition(directory, name, "sherpa-onnx.qwen3-asr", format, compatible,
                    compatible ? null : $"Qwen3-ASR 模型不完整，缺少：{string.Join(", ", missing)}", files);
            }

            if (HasHint(hints, "whisper"))
                return CreateEncoderDecoderDefinition(directory, name, "sherpa-onnx.whisper", "Whisper ONNX",
                    encoder, decoder, tokens);

            var looksLikeTransducer = joiner is not null || HasHint(hints, "transducer") ||
                                      HasHint(hints, "zipformer") && !HasHint(hints, "ctc");
            if (looksLikeTransducer)
            {
                var missing = MissingFiles((encoder, "encoder(.int8).onnx"), (decoder, "decoder(.int8).onnx"),
                    (joiner, "joiner(.int8).onnx"), (tokens, "tokens.txt"));
                var files = ExistingFiles(("encoder", encoder), ("decoder", decoder), ("joiner", joiner), ("tokens", tokens));
                var nemo = HasHint(hints, "nemo") || HasHint(hints, "parakeet");
                var format = nemo ? "NeMo Transducer ONNX" : "Transducer ONNX";
                if (IsInt8(encoder) || IsInt8(decoder) || IsInt8(joiner)) format += " INT8";
                return CreateDirectoryDefinition(directory, name,
                    nemo ? "sherpa-onnx.nemo-transducer" : "sherpa-onnx.transducer",
                    format, missing.Count == 0,
                    missing.Count == 0 ? null : $"Transducer 模型不完整，缺少：{string.Join(", ", missing)}", files);
            }

            var model = FindModelOnnx(allFiles);
            if (HasHint(hints, "sense-voice") || HasHint(hints, "sensevoice"))
                return CreateSingleOnnxDefinition(directory, name, "sherpa-onnx.sense-voice", "SenseVoice ONNX", model, tokens);
            if (HasHint(hints, "paraformer"))
                return CreateSingleOnnxDefinition(directory, name, "sherpa-onnx.paraformer", "Paraformer ONNX", model, tokens);
            if (HasHint(hints, "zipformer") && HasHint(hints, "ctc"))
                return CreateSingleOnnxDefinition(directory, name, "sherpa-onnx.zipformer-ctc", "Zipformer CTC ONNX", model, tokens);
            if (HasHint(hints, "wenet"))
                return CreateSingleOnnxDefinition(directory, name, "sherpa-onnx.wenet-ctc", "WeNet CTC ONNX", model, tokens);
            if ((HasHint(hints, "nemo") || HasHint(hints, "parakeet")) && HasHint(hints, "ctc"))
                return CreateSingleOnnxDefinition(directory, name, "sherpa-onnx.nemo-ctc", "NeMo CTC ONNX", model, tokens);
            if (HasHint(hints, "tdnn"))
                return CreateSingleOnnxDefinition(directory, name, "sherpa-onnx.tdnn", "TDNN ONNX", model, tokens);
            if (HasHint(hints, "telespeech"))
                return CreateSingleOnnxDefinition(directory, name, "sherpa-onnx.telespeech-ctc", "TeleSpeech CTC ONNX", model, tokens);
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private ModelDefinition CreateEncoderDecoderDefinition(string directory, string name, string engine, string format,
        string? encoder, string? decoder, string? tokens)
    {
        var missing = MissingFiles((encoder, "encoder(.int8).onnx"), (decoder, "decoder(.int8).onnx"), (tokens, "tokens.txt"));
        var files = ExistingFiles(("encoder", encoder), ("decoder", decoder), ("tokens", tokens));
        if (IsInt8(encoder) || IsInt8(decoder)) format += " INT8";
        return CreateDirectoryDefinition(directory, name, engine, format, missing.Count == 0,
            missing.Count == 0 ? null : $"{format.Replace(" ONNX", "", StringComparison.Ordinal)} 模型不完整，缺少：{string.Join(", ", missing)}", files);
    }

    private ModelDefinition CreateSingleOnnxDefinition(string directory, string name, string engine, string format,
        string? model, string? tokens)
    {
        var missing = MissingFiles((model, "model(.int8).onnx"), (tokens, "tokens.txt"));
        var files = ExistingFiles(("model", model), ("tokens", tokens));
        if (IsInt8(model)) format += " INT8";
        return CreateDirectoryDefinition(directory, name, engine, format, missing.Count == 0,
            missing.Count == 0 ? null : $"{format.Replace(" ONNX", "", StringComparison.Ordinal)} 模型不完整，缺少：{string.Join(", ", missing)}", files);
    }

    private static Dictionary<string, string> ExistingFiles(params (string Key, string? Path)[] entries)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, path) in entries)
            if (path is not null) result[key] = path;
        return result;
    }

    private static List<string> MissingFiles(params (string? Path, string Label)[] entries) =>
        entries.Where(entry => entry.Path is null).Select(entry => entry.Label).ToList();

    private static bool HasHint(string value, string hint) => value.Contains(hint, StringComparison.OrdinalIgnoreCase);
    private static bool IsInt8(string? path) => path?.Contains("int8", StringComparison.OrdinalIgnoreCase) == true;

    private static string? FindOnnxRole(IEnumerable<string> files, string role) => files
        .Where(path => path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) &&
                       Path.GetFileNameWithoutExtension(path).Contains(role, StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => RoleFileScore(Path.GetFileName(path), role))
        .ThenBy(path => path.Length)
        .FirstOrDefault();

    private static int RoleFileScore(string fileName, string role)
    {
        if (fileName.Equals($"{role}.int8.onnx", StringComparison.OrdinalIgnoreCase)) return 0;
        if (fileName.Equals($"{role}.onnx", StringComparison.OrdinalIgnoreCase)) return 1;
        if (fileName.EndsWith($"-{role}.int8.onnx", StringComparison.OrdinalIgnoreCase)) return 2;
        if (fileName.EndsWith($"-{role}.onnx", StringComparison.OrdinalIgnoreCase)) return 3;
        return IsInt8(fileName) ? 4 : 5;
    }

    private static string? FindTokens(IEnumerable<string> files) => files
        .Where(path => Path.GetFileName(path).Equals("tokens.txt", StringComparison.OrdinalIgnoreCase) ||
                       Path.GetFileName(path).EndsWith("-tokens.txt", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => Path.GetFileName(path).Equals("tokens.txt", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(path => path.Length)
        .FirstOrDefault();

    private static string? FindModelOnnx(IEnumerable<string> files)
    {
        var candidates = files.Where(path => path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileNameWithoutExtension(path).Contains("encoder", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileNameWithoutExtension(path).Contains("decoder", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileNameWithoutExtension(path).Contains("joiner", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileNameWithoutExtension(path).Contains("conv_frontend", StringComparison.OrdinalIgnoreCase)).ToArray();
        return candidates
            .OrderBy(path => Path.GetFileName(path).Equals("model.int8.onnx", StringComparison.OrdinalIgnoreCase) ? 0
                : Path.GetFileName(path).Equals("model.onnx", StringComparison.OrdinalIgnoreCase) ? 1
                : IsInt8(path) ? 2 : 3)
            .ThenBy(path => path.Length)
            .FirstOrDefault();
    }

    private ModelDefinition CreateDirectoryDefinition(string directory, string name, string engine, string format,
        bool compatible, string? compatibilityMessage, Dictionary<string, string> files)
    {
        var modelFiles = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path)).ToArray();
        var size = modelFiles.Sum(file => file.Length);
        var modifiedAt = modelFiles.Length == 0 ? new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory))
            : modelFiles.Max(file => new DateTimeOffset(file.LastWriteTimeUtc));
        var selected = string.Equals(name, _selectedModelId, StringComparison.OrdinalIgnoreCase);
        var loaded = string.Equals(name, _loadedModelId, StringComparison.OrdinalIgnoreCase) &&
                     size == _loadedModelSize && modifiedAt == _loadedModelModifiedAt;
        var info = new WhisperModelInfo(name, name, name, Path.GetFullPath(directory), engine, format, size, modifiedAt,
            compatible, compatibilityMessage, selected, loaded);
        return new(info, engine, files);
    }

    private static string FileFormat(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(extension) ? "未知格式" : extension.TrimStart('.').ToUpperInvariant();
    }

    private static OfflineRecognizer CreateOnnxRecognizer(ModelDefinition definition)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        config.ModelConfig.Provider = "cpu";
        if (definition.Engine == "sherpa-onnx.qwen3-asr")
        {
            config.FeatConfig.FeatureDim = 128;
            config.ModelConfig.Qwen3Asr.ConvFrontend = definition.Files["convFrontend"];
            config.ModelConfig.Qwen3Asr.Encoder = definition.Files["encoder"];
            config.ModelConfig.Qwen3Asr.Decoder = definition.Files["decoder"];
            config.ModelConfig.Qwen3Asr.Tokenizer = definition.Files["tokenizer"];
            config.ModelConfig.Qwen3Asr.Hotwords = "";
            config.ModelConfig.Tokens = "";
        }
        else if (definition.Engine == "sherpa-onnx.sense-voice")
        {
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.SenseVoice.Model = definition.Files["model"];
            config.ModelConfig.SenseVoice.Language = "auto";
            config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
            config.ModelConfig.Tokens = definition.Files["tokens"];
        }
        else if (definition.Engine == "sherpa-onnx.whisper")
        {
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Whisper.Encoder = definition.Files["encoder"];
            config.ModelConfig.Whisper.Decoder = definition.Files["decoder"];
            config.ModelConfig.Whisper.Language = "";
            config.ModelConfig.Whisper.Task = "transcribe";
            config.ModelConfig.Whisper.TailPaddings = -1;
            config.ModelConfig.Tokens = definition.Files["tokens"];
        }
        else if (definition.Engine is "sherpa-onnx.transducer" or "sherpa-onnx.nemo-transducer")
        {
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Transducer.Encoder = definition.Files["encoder"];
            config.ModelConfig.Transducer.Decoder = definition.Files["decoder"];
            config.ModelConfig.Transducer.Joiner = definition.Files["joiner"];
            config.ModelConfig.ModelType = definition.Engine == "sherpa-onnx.nemo-transducer"
                ? "nemo_transducer"
                : "transducer";
            config.ModelConfig.Tokens = definition.Files["tokens"];
        }
        else if (definition.Engine == "sherpa-onnx.paraformer")
        {
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Paraformer.Model = definition.Files["model"];
            config.ModelConfig.Tokens = definition.Files["tokens"];
        }
        else if (definition.Engine == "sherpa-onnx.nemo-ctc")
        {
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.NeMoCtc.Model = definition.Files["model"];
            config.ModelConfig.Tokens = definition.Files["tokens"];
        }
        else if (definition.Engine == "sherpa-onnx.zipformer-ctc")
        {
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.ZipformerCtc.Model = definition.Files["model"];
            config.ModelConfig.Tokens = definition.Files["tokens"];
        }
        else if (definition.Engine == "sherpa-onnx.wenet-ctc")
        {
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.WenetCtc.Model = definition.Files["model"];
            config.ModelConfig.Tokens = definition.Files["tokens"];
        }
        else if (definition.Engine == "sherpa-onnx.tdnn")
        {
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Tdnn.Model = definition.Files["model"];
            config.ModelConfig.Tokens = definition.Files["tokens"];
        }
        else if (definition.Engine == "sherpa-onnx.telespeech-ctc")
        {
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.TeleSpeechCtc = definition.Files["model"];
            config.ModelConfig.Tokens = definition.Files["tokens"];
        }
        else throw new NotSupportedException($"不支持的语音引擎：{definition.Engine}");
        return new OfflineRecognizer(config);
    }

    private static string RecognizeOnnx(OfflineRecognizer recognizer, string? engine, byte[] pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2)) / 32768f;
        using var stream = recognizer.CreateStream();
        if (engine == "sherpa-onnx.qwen3-asr") stream.SetOption("language", "Chinese");
        stream.AcceptWaveform(16000, samples);
        recognizer.Decode(stream);
        return stream.Result.Text.Trim();
    }

    private void LoadInitialModel(WhisperModelInfo model)
    {
        _state = $"正在加载模型 {model.Name}…";
        var definition = ResolveModel(model.Id) ?? throw new InvalidDataException($"模型不存在：{model.Id}");
        if (definition.Engine == "whisper.cpp") _factory = WhisperFactory.FromPath(definition.Files["model"]);
        else _onnxRecognizer = CreateOnnxRecognizer(definition);
        _selectedModelId = model.Id;
        _loadedModelId = model.Id;
        _loadedEngine = definition.Engine;
        _loadedModelSize = model.SizeBytes;
        _loadedModelModifiedAt = model.ModifiedAt;
        _modelDisplayName = model.Name;
        try { SaveSelectedModel(model.Id); }
        catch (Exception exception) { VoiceDiagnostics.Log($"WHISPER settings-save-error {exception.Message}"); }
        Models(force: true);
        _state = $"本地语音模型已就绪 · {model.Name}";
        VoiceDiagnostics.Log($"ASR ready model={model.Id} engine={definition.Engine} format={model.Format} bytes={model.SizeBytes} runtime=cpu");
    }

    private string? LoadSelectedModel()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return null;
            return JsonSerializer.Deserialize<ModelSettings>(File.ReadAllText(_settingsPath))?.SelectedModel;
        }
        catch { return null; }
    }

    private void SaveSelectedModel(string modelId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporary = _settingsPath + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new ModelSettings(modelId)));
        File.Move(temporary, _settingsPath, true);
    }

    private bool ModelMatchesLoaded(WhisperModelInfo model) => (_factory is not null || _onnxRecognizer is not null) &&
        string.Equals(model.Id, _loadedModelId, StringComparison.OrdinalIgnoreCase) &&
        model.SizeBytes == _loadedModelSize && model.ModifiedAt == _loadedModelModifiedAt;

    private static MemoryStream CreateWave(byte[] pcm)
    {
        var stream = new MemoryStream(44 + pcm.Length);
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + pcm.Length); writer.Write("WAVE"u8);
            writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
        }
        stream.Position = 0;
        return stream;
    }

    public void Dispose() { _factory?.Dispose(); _onnxRecognizer?.Dispose(); _gate.Dispose(); }
    private sealed record ModelSettings(string? SelectedModel);
    private sealed record ModelDefinition(WhisperModelInfo Info, string Engine, IReadOnlyDictionary<string, string> Files);
}
