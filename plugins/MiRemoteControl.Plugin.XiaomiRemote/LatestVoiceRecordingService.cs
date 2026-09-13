using NAudio;
using NAudio.Wave;

namespace MiRemoteControl.Plugin.XiaomiRemote;

public sealed record LatestVoiceRecordingStatus(
    bool Available,
    bool Playing,
    bool Paused,
    string PlaybackState,
    string FilePath,
    long PcmBytes,
    double DurationSeconds,
    DateTimeOffset? SavedAt,
    string Message,
    string? LastError);

/// <summary>
/// Persists and plays only the most recently completed 16 kHz mono PCM capture.
/// Playback belongs to the host so it keeps working while the desktop window is hidden.
/// </summary>
public sealed class LatestVoiceRecordingService : IDisposable
{
    private static readonly WaveFormat RecordingFormat = new(16000, 16, 1);
    private readonly object _sync = new();
    private readonly string _filePath;
    private WaveOutEvent? _output;
    private WaveFileReader? _reader;
    private long _pcmBytes;
    private double _durationSeconds;
    private DateTimeOffset? _savedAt;
    private string _message = "暂无上一条语音";
    private string? _lastError;

    public LatestVoiceRecordingService(string? directory = null)
    {
        var recordingDirectory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiRemoteControl", "recordings");
        _filePath = Path.Combine(recordingDirectory, "latest-voice.wav");
        ReadExistingRecordingMetadata();
    }

    public LatestVoiceRecordingStatus Status
    {
        get
        {
            lock (_sync)
            {
                var state = _output?.PlaybackState ?? PlaybackState.Stopped;
                var available = File.Exists(_filePath) && _pcmBytes > 0;
                return new LatestVoiceRecordingStatus(
                    available,
                    state == PlaybackState.Playing,
                    state == PlaybackState.Paused,
                    state.ToString(),
                    _filePath,
                    _pcmBytes,
                    _durationSeconds,
                    _savedAt,
                    _message,
                    _lastError);
            }
        }
    }

    public bool SavePcm(byte[] pcm16KhzMono)
    {
        ArgumentNullException.ThrowIfNull(pcm16KhzMono);
        lock (_sync)
        {
            StopPlaybackCore();
            _lastError = null;
            if (pcm16KhzMono.Length == 0)
            {
                _message = "本次语音没有音频数据，上一条录音未被覆盖";
                return false;
            }
            if ((pcm16KhzMono.Length & 1) != 0)
            {
                _lastError = "PCM 数据长度不是 16 位采样的整数倍。";
                _message = "保存上一条语音失败";
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = new WaveFileWriter(temporaryPath, RecordingFormat))
                    writer.Write(pcm16KhzMono, 0, pcm16KhzMono.Length);
                File.Move(temporaryPath, _filePath, overwrite: true);
                _pcmBytes = pcm16KhzMono.LongLength;
                _durationSeconds = pcm16KhzMono.LongLength / 32000d;
                _savedAt = DateTimeOffset.Now;
                _message = "已保存上一条语音";
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _lastError = exception.Message;
                _message = "保存上一条语音失败";
                return false;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { /* a stale temp file must not terminate voice recognition */ }
            }
        }
    }

    public bool Play(out string message)
    {
        lock (_sync)
        {
            _lastError = null;
            if (!File.Exists(_filePath) || _pcmBytes <= 0)
            {
                message = _message = "暂无可播放的上一条语音";
                return false;
            }

            try
            {
                if (_output?.PlaybackState == PlaybackState.Playing)
                {
                    message = _message = "上一条语音正在播放";
                    return true;
                }
                if (_output?.PlaybackState == PlaybackState.Paused)
                {
                    _output.Play();
                    message = _message = "继续播放上一条语音";
                    return true;
                }

                StopPlaybackCore();
                _reader = new WaveFileReader(_filePath);
                _output = new WaveOutEvent();
                _output.PlaybackStopped += PlaybackStopped;
                _output.Init(_reader);
                _output.Play();
                message = _message = "正在播放上一条语音";
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or MmException or InvalidDataException or FormatException)
            {
                _lastError = exception.Message;
                StopPlaybackCore();
                message = _message = "播放上一条语音失败：" + exception.Message;
                return false;
            }
        }
    }

    public bool Pause(out string message)
    {
        lock (_sync)
        {
            if (_output?.PlaybackState != PlaybackState.Playing)
            {
                message = _message = "上一条语音当前没有播放";
                return false;
            }
            _output.Pause();
            message = _message = "已暂停上一条语音";
            return true;
        }
    }

    public void StopForNewCapture()
    {
        lock (_sync)
        {
            if (_output is null) return;
            StopPlaybackCore();
            _message = "新语音录制已开始，上一条语音播放已停止";
        }
    }

    public bool Toggle(out string message)
    {
        lock (_sync)
        {
            if (_output?.PlaybackState == PlaybackState.Playing)
                return Pause(out message);
            return Play(out message);
        }
    }

    private void PlaybackStopped(object? sender, StoppedEventArgs args)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _output)) return;
            if (args.Exception is not null)
            {
                _lastError = args.Exception.Message;
                _message = "播放上一条语音失败：" + args.Exception.Message;
            }
            else
            {
                _message = "上一条语音播放完毕";
            }
            DisposePlaybackCore();
        }
    }

    private void ReadExistingRecordingMetadata()
    {
        lock (_sync)
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                using var reader = new WaveFileReader(_filePath);
                _pcmBytes = reader.Length;
                _durationSeconds = reader.TotalTime.TotalSeconds;
                _savedAt = File.GetLastWriteTimeUtc(_filePath);
                _message = _pcmBytes > 0 ? "上一条语音可以播放" : "暂无上一条语音";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _lastError = exception.Message;
                _message = "上一条语音文件不可用";
            }
        }
    }

    private void StopPlaybackCore()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= PlaybackStopped;
            try { _output.Stop(); } catch { }
        }
        DisposePlaybackCore();
    }

    private void DisposePlaybackCore()
    {
        _output?.Dispose();
        _reader?.Dispose();
        _output = null;
        _reader = null;
    }

    public void Dispose()
    {
        lock (_sync) StopPlaybackCore();
    }
}
