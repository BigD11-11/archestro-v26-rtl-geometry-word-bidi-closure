using NAudio.Wave;

namespace Archestro.MeetingVault.Services;

public static class SpeakerAudioService
{
    public static string Create16kMonoWave(string inputPath)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Meeting audio was not found.", inputPath);

        var temp = Path.Combine(
            Path.GetTempPath(),
            "ArchestroSpeaker_" + Guid.NewGuid().ToString("N") + ".wav");

        using var reader = new MediaFoundationReader(inputPath);
        var target = new WaveFormat(16000, 16, 1);
        using var resampler = new MediaFoundationResampler(reader, target)
        {
            ResamplerQuality = 60
        };
        WaveFileWriter.CreateWaveFile(temp, resampler);
        return temp;
    }
}

public sealed class MeetingAudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private MediaFoundationReader? _reader;
    private string _currentPath = string.Empty;
    private bool _disposed;

    public bool IsLoaded => _reader is not null;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public TimeSpan CurrentTime => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan TotalTime => _reader?.TotalTime ?? TimeSpan.Zero;
    public float Volume
    {
        get => _output?.Volume ?? 1f;
        set { if (_output is not null) _output.Volume = Math.Clamp(value, 0f, 1f); }
    }

    public void Load(string path, double seconds = 0)
    {
        Close();
        _currentPath = path;
        _reader = new MediaFoundationReader(path);
        _output = new WaveOutEvent();
        _output.Init(_reader);
        Seek(seconds);
    }

    public void PlayFrom(string path, double seconds)
    {
        Load(path, seconds);
        Play();
    }

    public void Play()
    {
        _output?.Play();
    }

    public void Pause()
    {
        _output?.Pause();
    }

    public void Seek(double seconds)
    {
        if (_reader is null) return;
        var total = _reader.TotalTime.TotalSeconds;
        var bounded = Math.Max(0, Math.Min(total <= 0 ? seconds : total, seconds));
        _reader.CurrentTime = TimeSpan.FromSeconds(bounded);
    }

    public void Stop()
    {
        try { _output?.Stop(); } catch { }
        if (_reader is not null)
            _reader.CurrentTime = TimeSpan.Zero;
    }

    public void Close()
    {
        try { _output?.Stop(); } catch { }
        _output?.Dispose();
        _reader?.Dispose();
        _output = null;
        _reader = null;
        _currentPath = string.Empty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }
}
