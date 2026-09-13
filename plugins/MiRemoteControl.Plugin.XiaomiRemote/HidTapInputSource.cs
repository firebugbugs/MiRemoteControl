using System.IO.Pipes;
using MiRemoteControl.Contracts;

namespace MiRemoteControl.Plugin.XiaomiRemote;

public sealed record HidTapStatus(
    bool Listening,
    bool Connected,
    long AcceptedReports,
    long RejectedReports,
    string? LastDevice,
    string? LastReport,
    string? LastError);

/// <summary>
/// Receives reports copied by the Windows HID filter bridge. Keeping this IPC
/// boundary out of the Avalonia process means the UI stays cross-platform and
/// the privileged Windows component can remain small and replaceable.
/// </summary>
public sealed class HidTapInputSource : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _listener;
    private readonly object _sync = new();
    private bool _listening;
    private bool _connected;
    private long _accepted;
    private long _rejected;
    private string? _lastDevice;
    private string? _lastReport;
    private string? _lastError;

    public event Action<HidTapFrame>? Report;

    public HidTapInputSource() => _listener = Task.Run(ListenAsync);

    public HidTapStatus Status
    {
        get
        {
            lock (_sync)
                return new(_listening, _connected, _accepted, _rejected,
                    _lastDevice, _lastReport, _lastError);
        }
    }

    public void Accept(HidTapFrame frame, bool accepted)
    {
        lock (_sync)
        {
            if (accepted) _accepted++; else _rejected++;
            _lastDevice = frame.DevicePath;
            _lastReport = frame.ReportHex;
        }
    }

    private async Task ListenAsync()
    {
        lock (_sync) _listening = true;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(
                    Wire.HidTapPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                lock (_sync) _connected = true;
                try
                {
                    while (pipe.IsConnected && !_shutdown.IsCancellationRequested)
                    {
                        var frame = await Wire.ReadAsync<HidTapFrame>(pipe, _shutdown.Token);
                        Report?.Invoke(frame);
                    }
                }
                catch (EndOfStreamException) { }
                catch (IOException) when (!_shutdown.IsCancellationRequested) { }
                finally { lock (_sync) _connected = false; }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (_sync) _lastError = exception.Message;
        }
        finally
        {
            lock (_sync)
            {
                _listening = false;
                _connected = false;
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _listener.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _shutdown.Dispose();
    }
}
