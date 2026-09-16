using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;

namespace MiRemoteControl.Plugin.XiaomiRemote;

public sealed record AtvvStatus(bool Ready, bool Streaming, string Device, string Message, long AudioPackets,
    long PcmBytes, int Peak, double Rms, string? LastCapture, string? LastError,
    bool TestActive, string TestStage, long ControlPackets, string? LastControl, string LogDirectory,
    int? BatteryLevel, DateTimeOffset? BatteryUpdatedAt, string? BatteryError);

public sealed class AtvvRemoteVoiceService : IDisposable
{
    private static readonly Guid ServiceId = Guid.Parse("AB5E0001-5A21-4F05-BC7D-AF01F617B664");
    private static readonly Guid TxId = Guid.Parse("AB5E0002-5A21-4F05-BC7D-AF01F617B664");
    private static readonly Guid AudioId = Guid.Parse("AB5E0003-5A21-4F05-BC7D-AF01F617B664");
    private static readonly Guid ControlId = Guid.Parse("AB5E0004-5A21-4F05-BC7D-AF01F617B664");
    private static readonly Guid BatteryServiceId = Guid.Parse("0000180F-0000-1000-8000-00805F9B34FB");
    private static readonly Guid BatteryLevelId = Guid.Parse("00002A19-0000-1000-8000-00805F9B34FB");
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _tx;
    private GattCharacteristic? _audio;
    private GattCharacteristic? _control;
    private GattDeviceService? _batteryService;
    private GattCharacteristic? _batteryCharacteristic;
    private BufferedPcmStream? _stream;
    private MemoryStream? _pcmCapture;
    private MemoryStream? _adpcmCapture;
    private string _deviceName = "ATVV 遥控器";
    private string _message = "尚未连接";
    private string? _lastError;
    private int _predictor;
    private int _stepIndex;
    private long _audioPackets;
    private long _pcmBytes;
    private int _peak;
    private double _rms;
    private string? _lastCapture;
    private bool _testActive;
    private string _testStage = "未开始测试";
    private long _controlPackets;
    private string? _lastControl;
    private bool _voiceHeld;
    private bool _closeRequested;
    private byte _startReason;
    private byte _streamId;
    private int _batteryLevel = -1;
    private long _batteryUpdatedAtTicks;
    private string? _batteryError;
    private readonly Action<int, DateTimeOffset>? _batteryUpdated;

    public AtvvRemoteVoiceService(
        int? cachedBatteryLevel = null,
        DateTimeOffset? cachedBatteryUpdatedAt = null,
        Action<int, DateTimeOffset>? batteryUpdated = null)
    {
        _batteryUpdated = batteryUpdated;
        if (cachedBatteryLevel is not (>= 0 and <= 100)) return;
        _batteryLevel = cachedBatteryLevel.Value;
        _batteryUpdatedAtTicks = (cachedBatteryUpdatedAt ?? DateTimeOffset.UtcNow).UtcTicks;
    }

    public event Action<Stream>? AudioStarted;
    public event Action? AudioStopped;
    public event Action<byte[]>? AudioCompleted;
    public AtvvStatus Status
    {
        get
        {
            var batteryLevel = Volatile.Read(ref _batteryLevel);
            var batteryUpdatedAtTicks = Interlocked.Read(ref _batteryUpdatedAtTicks);
            return new(_tx is not null, _stream is not null, _deviceName, _message,
                Interlocked.Read(ref _audioPackets), Interlocked.Read(ref _pcmBytes), _peak, _rms, _lastCapture, _lastError,
                _testActive, _testStage, Interlocked.Read(ref _controlPackets), _lastControl, VoiceDiagnostics.DirectoryPath,
                batteryLevel >= 0 ? batteryLevel : null,
                batteryUpdatedAtTicks > 0 ? new DateTimeOffset(batteryUpdatedAtTicks, TimeSpan.Zero) : null,
                _batteryError);
        }
    }

    public async Task BeginDiagnosticTestAsync()
    {
        _testActive = true;
        _testStage = "准备连接 ATVV";
        Interlocked.Exchange(ref _controlPackets, 0);
        Interlocked.Exchange(ref _audioPackets, 0);
        Interlocked.Exchange(ref _pcmBytes, 0);
        _lastControl = null;
        _lastCapture = null;
        _peak = 0;
        _rms = 0;
        VoiceDiagnostics.Log("========== TEST-BEGIN ==========");
        if (_tx is null) await ConnectAsync();
        _testStage = _tx is null ? "连接失败：请唤醒遥控器后重试" : "已就绪：按住语音键说话 3 秒";
    }

    public void EndDiagnosticTest()
    {
        if (_stream is not null) StopStream();
        _testActive = false;
        VoiceDiagnostics.Log($"========== TEST-END stage={_testStage} controls={_controlPackets} audio={_audioPackets} ==========");
    }

    public void ReportRecognition(string? text, string? error)
    {
        _testStage = !string.IsNullOrWhiteSpace(text) ? $"完成：{text}"
            : !string.IsNullOrWhiteSpace(error) ? $"识别失败：{error}" : "已收到音频，但未识别出文字";
    }

    public void NotifyVoiceKey(bool down)
    {
        if (down)
        {
            if (_voiceHeld) return;
            _voiceHeld = true;
            _ = EnsureMicrophoneAsync();
            return;
        }

        _voiceHeld = false;
        if (_stream is not null && _startReason == 0)
        {
            _closeRequested = true;
            _ = Send([0x0D, _streamId]);
            VoiceDiagnostics.Log($"TX MIC_CLOSE streamId={_streamId:X2}");
        }
    }

    private async Task EnsureMicrophoneAsync()
    {
        // Give an HTT-capable remote a chance to start the stream itself. If the
        // press happened during GATT setup, request capture explicitly.
        await Task.Delay(180);
        if (!_voiceHeld || _tx is null || _stream is not null) return;
        _testStage = "语音键已按下，主动请求麦克风";
        VoiceDiagnostics.Log("TX MIC_OPEN capture-mode");
        await Send([0x0C, 0x01]);
    }

    public async Task ConnectAsync()
    {
        // Key auto-repeat can produce dozens of wake events. Never queue BLE
        // reconnects: one in-flight GATT discovery is sufficient.
        if (!await _connectionGate.WaitAsync(0)) return;
        try
        {
            Disconnect();
            _lastError = null;
            _message = "正在查找支持 ATVV 的遥控器…";
            var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
            foreach (var info in devices.OrderByDescending(device => LooksLikeRemote(device.Name)))
            {
                BluetoothLEDevice? candidate = null;
                try
                {
                    candidate = await BluetoothLEDevice.FromIdAsync(info.Id);
                    if (candidate is null) continue;
                    var services = await candidate.GetGattServicesForUuidAsync(ServiceId, BluetoothCacheMode.Uncached);
                    if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) continue;
                    _device = candidate;
                    candidate = null;
                    _service = services.Services[0];
                    _deviceName = string.IsNullOrWhiteSpace(info.Name) ? "ATVV 遥控器" : info.Name;
                    VoiceDiagnostics.Log($"CONNECT device={_deviceName} id={info.Id} discovery=service");
                    break;
                }
                catch (Exception exception)
                {
                    VoiceDiagnostics.Log($"DISCOVERY-SKIP id={info.Id} error={exception.GetType().Name}");
                }
                finally { candidate?.Dispose(); }
            }
            if (_device is null || _service is null)
                throw new InvalidOperationException("没有发现可访问 ATVV 服务的已配对遥控器，请按任意键唤醒后重连。");
            await ConnectBatteryAsync();
            _tx = await GetCharacteristic(TxId);
            _audio = await GetCharacteristic(AudioId);
            _control = await GetCharacteristic(ControlId);
            _audio.ValueChanged += OnAudio;
            _control.ValueChanged += OnControl;
            await EnableNotifications(_audio);
            await EnableNotifications(_control);
            await Send([0x0A, 0x01, 0x00, 0x00, 0x03, 0x03]);
            _lastError = null;
            _message = "ATVV 已连接，按住语音键开始说话";
            VoiceDiagnostics.Log("CONNECT ready; notifications enabled; GET_CAPS sent");
            if (_voiceHeld) _ = EnsureMicrophoneAsync();
        }
        catch (Exception e)
        {
            _lastError = e.Message;
            _message = "遥控器语音不可用";
            VoiceDiagnostics.Log($"CONNECT-ERROR {e.GetType().Name}: {e.Message}");
            Disconnect();
        }
        finally { _connectionGate.Release(); }
    }

    private async Task ConnectBatteryAsync()
    {
        _batteryError = null;
        try
        {
            var services = await _device!.GetGattServicesForUuidAsync(BatteryServiceId, BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new InvalidOperationException($"电池服务不可访问（{services.Status}）");

            _batteryService = services.Services[0];
            var characteristics = await _batteryService.GetCharacteristicsForUuidAsync(BatteryLevelId, BluetoothCacheMode.Uncached);
            if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
                throw new InvalidOperationException($"电量特征不可访问（{characteristics.Status}）");

            _batteryCharacteristic = characteristics.Characteristics[0];
            var properties = _batteryCharacteristic.CharacteristicProperties;
            if (properties.HasFlag(GattCharacteristicProperties.Read))
            {
                var reading = await _batteryCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                if (reading.Status == GattCommunicationStatus.Success)
                {
                    CryptographicBuffer.CopyToByteArray(reading.Value, out var bytes);
                    UpdateBatteryLevel(bytes);
                }
                else
                {
                    _batteryError = $"读取电量失败（{reading.Status}）";
                }
            }

            if (properties.HasFlag(GattCharacteristicProperties.Notify))
            {
                _batteryCharacteristic.ValueChanged += OnBatteryChanged;
                var notification = await _batteryCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (notification != GattCommunicationStatus.Success && Volatile.Read(ref _batteryLevel) < 0)
                    _batteryError = $"订阅电量通知失败（{notification}）";
            }

            if (Volatile.Read(ref _batteryLevel) >= 0)
                VoiceDiagnostics.Log($"BATTERY level={Volatile.Read(ref _batteryLevel)}%");
            else if (string.IsNullOrWhiteSpace(_batteryError))
                _batteryError = "遥控器没有提供可读取的电量";
        }
        catch (Exception exception)
        {
            _batteryError = exception.Message;
            VoiceDiagnostics.Log($"BATTERY-ERROR {exception.GetType().Name}: {exception.Message}");
            DisposeBatteryGatt();
        }
    }

    private void OnBatteryChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out var bytes);
        UpdateBatteryLevel(bytes);
    }

    private void UpdateBatteryLevel(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes[0] > 100)
        {
            _batteryError = "遥控器返回了无效电量";
            return;
        }

        var level = (int)bytes[0];
        var updatedAt = DateTimeOffset.UtcNow;
        Volatile.Write(ref _batteryLevel, level);
        Interlocked.Exchange(ref _batteryUpdatedAtTicks, updatedAt.UtcTicks);
        _batteryError = null;
        try { _batteryUpdated?.Invoke(level, updatedAt); }
        catch (Exception exception) { VoiceDiagnostics.Log($"BATTERY-CACHE-ERROR {exception.GetType().Name}: {exception.Message}"); }
    }

    private static bool LooksLikeRemote(string name) =>
        name.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("遥控", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("MI RC", StringComparison.OrdinalIgnoreCase);
    private async Task<GattCharacteristic> GetCharacteristic(Guid id)
    {
        var result = await _service!.GetCharacteristicsForUuidAsync(id, BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
            throw new InvalidOperationException($"遥控器缺少 ATVV 特征 {id}。");
        return result.Characteristics[0];
    }
    private static async Task EnableNotifications(GattCharacteristic characteristic)
    {
        var result = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (result != GattCommunicationStatus.Success) throw new InvalidOperationException($"订阅 ATVV 通知失败（{result}）。");
    }
    private async Task Send(byte[] data)
    {
        if (_tx is null) return;
        var result = await _tx.WriteValueAsync(CryptographicBuffer.CreateFromByteArray(data), GattWriteOption.WriteWithoutResponse);
        if (result != GattCommunicationStatus.Success) _lastError = $"ATVV 写入失败（{result}）";
    }
    private void OnControl(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out var data);
        if (data.Length == 0) return;
        Interlocked.Increment(ref _controlPackets);
        _lastControl = Convert.ToHexString(data);
        // RC003 emits 00 00 idle notifications at 40 Hz. Keep them counted but
        // do not flood the diagnostic log.
        if (data.Length != 2 || data[0] != 0 || data[1] != 0) VoiceDiagnostics.Log($"CONTROL {_lastControl}");
        switch (data[0])
        {
            case 0x0B:
                _message = "ATVV 能力协商完成，等待语音键";
                break;
            case 0x08:
                if (_stream is not null) StopStream(); else _ = Send([0x0C, 0x00]);
                break;
            case 0x04:
                if (data.Length >= 4)
                {
                    _startReason = data[1];
                    _streamId = data[3];
                    _closeRequested = false;
                }
                _testStage = "收到 AUDIO_START，等待音频数据";
                StartStream();
                break;
            case 0x0A:
                // ATVV v1 AUDIO_SYNC: codec, frame-sequence (2 bytes),
                // ADPCM predictor (2 bytes, big endian), step-index.
                if (data.Length >= 7) { _predictor = (short)((data[4] << 8) | data[5]); _stepIndex = Math.Clamp((int)data[6], 0, 88); }
                break;
            case 0x00:
                // On this RC003, 00 00 is a continuous idle notification even
                // outside a voice session. The actual PTT release is 00 02.
                if (_stream is not null && (data.Length == 1 || data[1] != 0 || _closeRequested))
                {
                    StopStream();
                }
                break;
        }
    }
    private void StartStream()
    {
        if (_stream is not null) return;
        // A new ATVV stream starts a new ADPCM encoder session. AUDIO_SYNC can
        // override these values later if the remote sends one.
        _predictor = 0;
        _stepIndex = 0;
        Interlocked.Exchange(ref _audioPackets, 0);
        Interlocked.Exchange(ref _pcmBytes, 0);
        _peak = 0;
        _rms = 0;
        _stream = new BufferedPcmStream();
        _pcmCapture = new MemoryStream();
        _adpcmCapture = new MemoryStream();
        _message = "正在接收遥控器语音…";
        _testStage = "正在接收语音数据";
        VoiceDiagnostics.Log($"CAPTURE-START predictor={_predictor} stepIndex={_stepIndex}");
        AudioStarted?.Invoke(_stream);
    }
    private void StopStream()
    {
        var stream = _stream;
        if (stream is null) return;
        _stream = null;
        stream.Complete();
        var pcm = _pcmCapture?.ToArray() ?? [];
        var adpcm = _adpcmCapture?.ToArray() ?? [];
        _pcmCapture?.Dispose();
        _pcmCapture = null;
        _adpcmCapture?.Dispose();
        _adpcmCapture = null;
        (_peak, _rms) = Measure(pcm);
        Interlocked.Exchange(ref _pcmBytes, pcm.Length);
        _lastCapture = VoiceDiagnostics.SaveCapture(pcm, adpcm);
        _message = "语音接收完成，正在识别";
        _testStage = pcm.Length == 0 ? "失败：收到停止信号但没有音频包" : "音频已保存，正在识别";
        VoiceDiagnostics.Log($"CAPTURE-STOP packets={_audioPackets} adpcmBytes={adpcm.Length} pcmBytes={pcm.Length} peak={_peak} rms={_rms:F1} wav={_lastCapture ?? "<save-failed>"}");
        AudioStopped?.Invoke();
        if (pcm.Length > 0) AudioCompleted?.Invoke(pcm);
    }
    private void OnAudio(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var stream = _stream;
        if (stream is null) return;
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out var data);
        if (data.Length == 0) return;
        _testStage = "正在接收语音数据";
        _adpcmCapture?.Write(data, 0, data.Length);
        // RC003 negotiates ATVV v1: audio notifications are headerless ADPCM
        // frames. Predictor/index arrive separately in AUDIO_SYNC (0x0A).
        var pcm = new byte[data.Length * 4];
        var output = 0;
        for (var index = 0; index < data.Length; index++)
        {
            Decode(data[index] >> 4);
            Decode(data[index] & 0x0F);
        }
        stream.WriteSamples(pcm, output);
        _pcmCapture?.Write(pcm, 0, output);
        Interlocked.Increment(ref _audioPackets);

        void Decode(int nibble)
        {
            var step = StepTable[_stepIndex];
            var difference = step >> 3;
            if ((nibble & 1) != 0) difference += step >> 2;
            if ((nibble & 2) != 0) difference += step >> 1;
            if ((nibble & 4) != 0) difference += step;
            _predictor = Math.Clamp(_predictor + ((nibble & 8) != 0 ? -difference : difference), short.MinValue, short.MaxValue);
            _stepIndex = Math.Clamp(_stepIndex + IndexTable[nibble], 0, 88);
            // Preserve the decoded 16-bit sample. The old 4x gain clipped most
            // speech at +/-32768 and destroyed recognition features.
            pcm[output++] = (byte)_predictor;
            pcm[output++] = (byte)(_predictor >> 8);
        }
    }
    private static (int Peak, double Rms) Measure(byte[] pcm)
    {
        if (pcm.Length < 2) return (0, 0);
        long squares = 0;
        var peak = 0;
        var samples = pcm.Length / 2;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var absolute = sample == short.MinValue ? 32768 : Math.Abs((int)sample);
            peak = Math.Max(peak, absolute);
            squares += (long)sample * sample;
        }
        return (peak, Math.Sqrt((double)squares / samples));
    }
    private static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];
    private static readonly int[] StepTable = [7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767];
    private void Disconnect()
    {
        StopStream();
        if (_audio is not null) _audio.ValueChanged -= OnAudio;
        if (_control is not null) _control.ValueChanged -= OnControl;
        DisposeBatteryGatt();
        _service?.Dispose(); _service = null;
        _device?.Dispose(); _device = null;
        _tx = _audio = _control = null;
    }
    private void DisposeBatteryGatt()
    {
        if (_batteryCharacteristic is not null) _batteryCharacteristic.ValueChanged -= OnBatteryChanged;
        _batteryCharacteristic = null;
        _batteryService?.Dispose();
        _batteryService = null;
    }
    public void Dispose() { Disconnect(); _connectionGate.Dispose(); }
}

public sealed class BufferedPcmStream : Stream
{
    private readonly ConcurrentQueue<byte[]> _chunks = new();
    private readonly AutoResetEvent _available = new(false);
    private byte[]? _current;
    private int _offset;
    private long _position;
    private bool _complete;
    public void WriteSamples(byte[] data, int length) { if (length == 0 || _complete) return; _chunks.Enqueue(data[..length]); _available.Set(); }
    public void Complete() { _complete = true; _available.Set(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            if (_current is not null && _offset < _current.Length)
            {
                var take = Math.Min(count - total, _current.Length - _offset);
                Array.Copy(_current, _offset, buffer, offset + total, take); _offset += take; total += take; _position += take; continue;
            }
            if (_chunks.TryDequeue(out _current)) { _offset = 0; continue; }
            if (_complete) break;
            _available.WaitOne(100);
        }
        return total;
    }
    public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
    public override long Length => long.MaxValue;
    public override long Position { get => _position; set => _position = Math.Max(0, value); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => Length + offset, _ => _position };
        return _position;
    }
    public override void SetLength(long value) { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
