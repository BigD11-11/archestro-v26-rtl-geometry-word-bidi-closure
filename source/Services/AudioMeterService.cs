using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Archestro.MeetingVault.Services;

public sealed class AudioMeterService : IDisposable
{
    private WasapiCapture? _microphone;
    private WasapiLoopbackCapture? _system;
    private readonly object _gate = new();
    private string? _microphoneDeviceId;
    private string? _systemDeviceId;
    private DateTimeOffset _lastDeviceRestart = DateTimeOffset.MinValue;

    public bool MicrophoneReady { get; private set; }
    public bool SystemAudioReady { get; private set; }
    public string MicrophoneError { get; private set; } = "";
    public string SystemAudioError { get; private set; } = "";
    public string MicrophoneDeviceName { get; private set; } = "";
    public string SystemAudioDeviceName { get; private set; } = "";
    public double MicrophoneLevel { get; private set; }
    public double SystemAudioLevel { get; private set; }

    public event Action<double, double>? LevelsChanged;

    public void Start()
    {
        Stop();

        _microphoneDeviceId = TryGetDefaultDeviceId(DataFlow.Capture, Role.Console);
        _systemDeviceId = TryGetDefaultDeviceId(DataFlow.Render, Role.Multimedia);
        MicrophoneDeviceName = TryGetDefaultDeviceName(DataFlow.Capture, Role.Console);
        SystemAudioDeviceName = TryGetDefaultDeviceName(DataFlow.Render, Role.Multimedia);
        _lastDeviceRestart = DateTimeOffset.Now;

        try
        {
            _microphone = new WasapiCapture();
            _microphone.DataAvailable += (_, e) =>
            {
                var level = CalculatePeak(e.Buffer, e.BytesRecorded, _microphone.WaveFormat);
                lock (_gate) MicrophoneLevel = Smooth(MicrophoneLevel, level);
                Raise();
            };
            _microphone.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    MicrophoneReady = false;
                    MicrophoneError = e.Exception.Message;
                }
            };
            _microphone.StartRecording();
            MicrophoneReady = true;
            MicrophoneError = "";
        }
        catch (Exception ex)
        {
            MicrophoneReady = false;
            MicrophoneError = ex.Message;
        }

        try
        {
            _system = new WasapiLoopbackCapture();
            _system.DataAvailable += (_, e) =>
            {
                var level = CalculatePeak(e.Buffer, e.BytesRecorded, _system.WaveFormat);
                lock (_gate) SystemAudioLevel = Smooth(SystemAudioLevel, level);
                Raise();
            };
            _system.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    SystemAudioReady = false;
                    SystemAudioError = e.Exception.Message;
                }
            };
            _system.StartRecording();
            SystemAudioReady = true;
            SystemAudioError = "";
        }
        catch (Exception ex)
        {
            SystemAudioReady = false;
            SystemAudioError = ex.Message;
        }

        Raise();
    }

    public bool RefreshIfDeviceChanged()
    {
        // Polling is intentional for NAudio 2.x: it gives the user true hot-plug recovery
        // without requiring the app to restart or relying on COM notification lifetime.
        if (DateTimeOffset.Now - _lastDeviceRestart < TimeSpan.FromSeconds(2.5))
            return false;

        var currentMic = TryGetDefaultDeviceId(DataFlow.Capture, Role.Console);
        var currentSystem = TryGetDefaultDeviceId(DataFlow.Render, Role.Multimedia);

        var changed =
            !string.Equals(currentMic, _microphoneDeviceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(currentSystem, _systemDeviceId, StringComparison.OrdinalIgnoreCase);

        var needsRecovery = !MicrophoneReady || !SystemAudioReady;

        if (!changed && !needsRecovery)
            return false;

        Start();
        return true;
    }

    private static string? TryGetDefaultDeviceId(DataFlow flow, Role role)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    private static string TryGetDefaultDeviceName(DataFlow flow, Role role)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.FriendlyName;
        }
        catch { return "Unavailable"; }
    }

    public void Stop()
    {
        try { _microphone?.StopRecording(); } catch { }
        try { _system?.StopRecording(); } catch { }
        try { _microphone?.Dispose(); } catch { }
        try { _system?.Dispose(); } catch { }
        _microphone = null;
        _system = null;
    }

    private void Raise()
    {
        double mic;
        double system;
        lock (_gate)
        {
            mic = MicrophoneLevel;
            system = SystemAudioLevel;
        }
        LevelsChanged?.Invoke(mic, system);
    }

    private static double Smooth(double previous, double current) =>
        Math.Clamp(previous * 0.68 + current * 0.32, 0, 1);

    private static double CalculatePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded <= 0) return 0;
        double peak = 0;

        try
        {
            if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                for (var i = 0; i + 3 < bytesRecorded; i += 4)
                {
                    var sample = Math.Abs(BitConverter.ToSingle(buffer, i));
                    if (!double.IsNaN(sample) && sample > peak) peak = sample;
                }
            }
            else if (format.BitsPerSample == 16)
            {
                for (var i = 0; i + 1 < bytesRecorded; i += 2)
                {
                    var sample = Math.Abs(BitConverter.ToInt16(buffer, i) / 32768.0);
                    if (sample > peak) peak = sample;
                }
            }
            else if (format.BitsPerSample == 24)
            {
                for (var i = 0; i + 2 < bytesRecorded; i += 3)
                {
                    var sample = buffer[i] | (buffer[i + 1] << 8) | (buffer[i + 2] << 16);
                    if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                    var value = Math.Abs(sample / 8388608.0);
                    if (value > peak) peak = value;
                }
            }
            else
            {
                for (var i = 0; i < bytesRecorded; i++)
                {
                    var sample = Math.Abs((buffer[i] - 128) / 128.0);
                    if (sample > peak) peak = sample;
                }
            }
        }
        catch
        {
            return 0;
        }

        return Math.Clamp(peak, 0, 1);
    }

    public void Dispose() => Stop();
}
