using NAudio.Wave;
using System.Speech.AudioFormat;
using System.Speech.Recognition;

namespace MiRemoteControl.Plugin.XiaomiRemote;

public sealed record VoiceStatus(bool Available, bool Listening, string Recognizer, string? Device, string? LastText, string? LastError);

public sealed class VoiceInputService : IDisposable
{
    private readonly object _sync = new();
    private SpeechRecognitionEngine? _engine;
    private WaveInEvent? _capture;
    private PcmStream? _audio;
    private Stream? _externalAudio;
    private string? _deviceName;
    private string _recognizer = "";
    private string? _lastText;
    private string? _lastError;
    private byte[]? _lastPcm;
    private bool _listening;
    private TaskCompletionSource? _recognizeCompleted;
    public VoiceInputService()
    {
        try
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers()
                .OrderByDescending(r => r.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (installed is null) { _lastError = "Windows 未安装可用的本地语音识别器。"; return; }
            _recognizer = $"{installed.Culture.Name} · {installed.Description}";
            _engine = new SpeechRecognitionEngine(installed);
            _engine.LoadGrammar(new DictationGrammar());
            VoiceDiagnostics.Log($"RECOGNIZER ready {_recognizer}");
            _engine.SpeechRecognized += (_, e) =>
            {
                VoiceDiagnostics.Log($"RECOGNIZED confidence={e.Result.Confidence:F3} text={e.Result.Text}");
                if (e.Result.Confidence >= 0.08f && !string.IsNullOrWhiteSpace(e.Result.Text))
                    _lastText = string.IsNullOrWhiteSpace(_lastText) ? e.Result.Text : _lastText + e.Result.Text;
            };
            _engine.SpeechRecognitionRejected += (_, e) => VoiceDiagnostics.Log($"REJECTED confidence={e.Result?.Confidence:F3} text={e.Result?.Text}");
            _engine.SpeechHypothesized += (_, e) => VoiceDiagnostics.Log($"HYPOTHESIS text={e.Result.Text}");
            _engine.RecognizeCompleted += (_, e) =>
            {
                if (e.Error is not null) _lastError = e.Error.Message;
                VoiceDiagnostics.Log($"RECOGNIZE-COMPLETE cancelled={e.Cancelled} error={e.Error?.Message ?? "<none>"} text={_lastText ?? "<none>"}");
                _recognizeCompleted?.TrySetResult();
            };
        }
        catch (Exception e) { _lastError = e.Message; VoiceDiagnostics.Log($"RECOGNIZER-ERROR {e.GetType().Name}: {e.Message}"); }
    }
    /// <summary>
    /// Audio-device enumeration is a convenience for the voice UI only. Some
    /// Windows audio drivers temporarily reject capability queries while the
    /// device stack is being reconfigured; that must never make the remote
    /// driver appear offline.
    /// </summary>
    public IReadOnlyList<string> Devices()
    {
        try
        {
            return Enumerable.Range(0, WaveIn.DeviceCount)
                .Select(index => WaveIn.GetCapabilities(index).ProductName)
                .ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NAudio.MmException)
        {
            _lastError ??= $"无法读取录音设备：{exception.Message}";
            return [];
        }
    }

    public VoiceStatus Status()
    {
        string? device = _deviceName;
        if (device is null && _capture is not null)
        {
            try { device = WaveIn.GetCapabilities(_capture.DeviceNumber).ProductName; }
            catch (Exception exception) when (exception is InvalidOperationException or NAudio.MmException)
            {
                _lastError ??= $"无法读取当前录音设备：{exception.Message}";
            }
        }
        return new(_engine is not null, _listening, _recognizer, device, _lastText, _lastError);
    }
    public bool Start(int? deviceIndex = null)
    {
        lock (_sync)
        {
            if (_engine is null) return false;
            if (_listening) return true;
            try
            {
                _lastError = null; _lastText = null; _lastPcm = null;
                var device = deviceIndex.GetValueOrDefault(-1);
                if (device >= WaveIn.DeviceCount) throw new ArgumentOutOfRangeException(nameof(deviceIndex));
                _capture = new WaveInEvent { DeviceNumber = device, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
                _deviceName = device >= 0 ? WaveIn.GetCapabilities(device).ProductName : "Windows 默认麦克风";
                _audio = new PcmStream();
                _capture.DataAvailable += (_, e) => _audio.WriteSamples(e.Buffer, e.BytesRecorded);
                _engine.SetInputToAudioStream(_audio, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                _recognizeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _engine.RecognizeAsync(RecognizeMode.Multiple);
                _capture.StartRecording();
                _listening = true;
                return true;
            }
            catch (Exception e) { _lastError = $"{e.GetType().Name}: {e.Message}"; Cleanup(); return false; }
        }
    }
    public bool Start(Stream pcm16KhzMono, string deviceName)
    {
        lock (_sync)
        {
            if (_engine is null) return false;
            if (_listening) return true;
            try
            {
                _lastError = null; _lastText = null; _externalAudio = pcm16KhzMono; _deviceName = deviceName;
                _engine.SetInputToAudioStream(pcm16KhzMono, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                _recognizeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _engine.RecognizeAsync(RecognizeMode.Multiple); _listening = true; return true;
            }
            catch (Exception e) { _lastError = e.Message; Cleanup(); return false; }
        }
    }
    public async Task<string?> RecognizePcmAsync(byte[] pcm16KhzMono, string deviceName)
    {
        if (pcm16KhzMono.Length == 0) return null;
        VoiceDiagnostics.Log($"RECOGNIZE-START device={deviceName} pcmBytes={pcm16KhzMono.Length} durationMs={pcm16KhzMono.Length * 1000L / 32000}");
        Task completed;
        lock (_sync)
        {
            if (_engine is null || _listening) return null;
            try
            {
                _lastError = null;
                _lastText = null;
                _deviceName = deviceName;
                _externalAudio = new MemoryStream(pcm16KhzMono, writable: false);
                _engine.SetInputToAudioStream(_externalAudio,
                    new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                _recognizeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                completed = _recognizeCompleted.Task;
                _listening = true;
                _engine.RecognizeAsync(RecognizeMode.Multiple);
            }
            catch (Exception e)
            {
                _lastError = $"{e.GetType().Name}: {e.Message}";
                VoiceDiagnostics.Log($"RECOGNIZE-ERROR {_lastError}");
                Cleanup();
                return null;
            }
        }

        await completed.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (_sync)
        {
            if (!completed.IsCompleted)
            {
                _lastError = "语音识别超时，未返回结果。";
                VoiceDiagnostics.Log("RECOGNIZE-TIMEOUT");
                try { _engine?.RecognizeAsyncCancel(); } catch { }
            }
            var text = _lastText;
            Cleanup();
            return text;
        }
    }
    public async Task<string?> StopAsync()
    {
        SpeechRecognitionEngine? engine;
        Task? completed;
        lock (_sync)
        {
            if (!_listening) return null;
            engine = _engine;
            completed = _recognizeCompleted?.Task;
            _capture?.StopRecording();
            _audio?.Complete();
            _lastPcm = _audio?.Snapshot();
            try { engine?.RecognizeAsyncStop(); } catch { }
        }
        if (completed is not null) await completed.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (_sync)
        {
            var text = _lastText;
            Cleanup();
            return text;
        }
    }
    public byte[]? TakeLastPcm()
    {
        lock (_sync)
        {
            var pcm = _lastPcm;
            _lastPcm = null;
            return pcm?.ToArray();
        }
    }
    private void Cleanup()
    {
        _listening = false;
        try { _engine?.RecognizeAsyncCancel(); } catch { }
        try { _engine?.SetInputToNull(); } catch { }
        _audio?.Complete(); _capture?.Dispose(); _capture = null; _audio = null; _externalAudio?.Dispose(); _externalAudio = null; _deviceName = null; _recognizeCompleted = null;
    }
    public void Dispose() { lock (_sync) { Cleanup(); _engine?.Dispose(); _engine = null; } }

    private sealed class PcmStream : Stream
    {
        private readonly Queue<byte[]> _chunks = [];
        private readonly AutoResetEvent _available = new(false);
        private int _offset; private bool _complete;
        public void WriteSamples(byte[] data, int length)
        {
            var copy = data[..length];
            lock (_chunks)
            {
                if (!_complete)
                {
                    _chunks.Enqueue(copy);
                    _capture.Write(copy, 0, copy.Length);
                }
            }
            _available.Set();
        }
        public void Complete() { lock (_chunks) _complete = true; _available.Set(); }
        private readonly MemoryStream _capture = new();
        public byte[] Snapshot() { lock (_chunks) return _capture.ToArray(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                lock (_chunks)
                {
                    if (_chunks.Count > 0)
                    {
                        var chunk = _chunks.Peek(); var take = Math.Min(count, chunk.Length - _offset);
                        Array.Copy(chunk, _offset, buffer, offset, take); _offset += take;
                        if (_offset == chunk.Length) { _chunks.Dequeue(); _offset = 0; }
                        return take;
                    }
                    if (_complete) return 0;
                }
                _available.WaitOne(100);
            }
        }
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => long.MaxValue; public override long Position { get; set; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, SeekOrigin.End => Length + offset, _ => Position }; return Position; }
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
