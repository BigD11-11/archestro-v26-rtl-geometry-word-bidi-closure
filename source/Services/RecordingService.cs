using Archestro.MeetingVault.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Archestro.MeetingVault.Services;

public sealed class RecordingService
{
    private readonly AppSettings _settings;
    private readonly MeetingRepository _repository;

    private MeetingRecord? _current;
    private DateTimeOffset? _pausedAt;
    private TimeSpan _pausedTotal = TimeSpan.Zero;

    private WasapiCapture? _microphoneCapture;
    private WasapiLoopbackCapture? _systemCapture;
    private WaveFileWriter? _microphoneWriter;
    private WaveFileWriter? _systemWriter;

    private TaskCompletionSource<bool>? _microphoneStopped;
    private TaskCompletionSource<bool>? _systemStopped;
    private Exception? _microphoneStopError;
    private Exception? _systemStopError;

    private readonly object _microphoneWriteGate = new();
    private readonly object _systemWriteGate = new();

    private string _microphoneRawPath = "";
    private string _systemRawPath = "";

    public MeetingRecord? Current => _current;
    public bool IsRecording => _current is not null;
    public bool IsPaused { get; private set; }

    public bool LastStopMicrophoneValid { get; private set; }
    public bool LastStopSystemAudioValid { get; private set; }

    public RecordingService(AppSettings settings, MeetingRepository repository)
    {
        _settings = settings;
        _repository = repository;
    }

    public async Task StartAsync(string? meetingName, CancellationToken ct)
    {
        BackgroundTranscriptionScheduler.SetRecordingActive(true);
        if (IsRecording)
            throw new InvalidOperationException("A meeting is already recording.");

        if (string.IsNullOrWhiteSpace(_settings.FfmpegExe) || !File.Exists(_settings.FfmpegExe))
            throw new FileNotFoundException("The local audio processor is missing. Run Install/Repair.", _settings.FfmpegExe);

        var start = DateTimeOffset.Now;
        var folder = AppPaths.CreateMeetingFolder(meetingName, start);

        _microphoneRawPath = Path.Combine(folder, "_TEMP_Microphone_Raw.wav");
        _systemRawPath = Path.Combine(folder, "_TEMP_System_Audio_Raw.wav");

        _microphoneStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _systemStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _microphoneStopError = null;
        _systemStopError = null;

        try
        {
            // Windows WASAPI shared-mode capture. No OBS or external recorder is launched.
            _microphoneCapture = new WasapiCapture();
            _systemCapture = new WasapiLoopbackCapture();

            _microphoneWriter = new WaveFileWriter(_microphoneRawPath, _microphoneCapture.WaveFormat);
            _systemWriter = new WaveFileWriter(_systemRawPath, _systemCapture.WaveFormat);

            _microphoneCapture.DataAvailable += Microphone_DataAvailable;
            _systemCapture.DataAvailable += System_DataAvailable;

            _microphoneCapture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                    _microphoneStopError = e.Exception;
                _microphoneStopped?.TrySetResult(true);
            };

            _systemCapture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                    _systemStopError = e.Exception;
                _systemStopped?.TrySetResult(true);
            };

            // Start loopback first, then microphone. The difference is only a few milliseconds,
            // and both streams are normalized to first_pts=0 when finalized.
            _systemCapture.StartRecording();
            _microphoneCapture.StartRecording();

            // Give WASAPI a short window to surface an immediate device failure before
            // declaring the meeting live.
            await Task.Delay(250, ct);

            if (_microphoneStopError is not null)
                throw new InvalidOperationException("The microphone could not start recording.", _microphoneStopError);

            if (_systemStopError is not null)
                throw new InvalidOperationException("System audio could not start recording.", _systemStopError);

            IsPaused = false;
            _pausedAt = null;
            _pausedTotal = TimeSpan.Zero;

            _current = new MeetingRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                FolderPath = folder,
                Title = meetingName?.Trim() ?? "",
                HasExplicitTitle = !string.IsNullOrWhiteSpace(meetingName),
                StartLocal = start,
                TranscriptionStatus = "Recording",
                Category = "Uncategorized",
                CategoryColor = CategoryCatalog.ColorFor("Uncategorized")
            };

            _repository.Upsert(_current);
        }
        catch
        {
            BackgroundTranscriptionScheduler.SetRecordingActive(false);
            await StopNativeCaptureAsync(CancellationToken.None);
            TryDelete(_microphoneRawPath);
            TryDelete(_systemRawPath);

            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch { }

            throw;
        }
    }

    private void Microphone_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (IsPaused || e.BytesRecorded <= 0) return;

        lock (_microphoneWriteGate)
        {
            try
            {
                _microphoneWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            }
            catch (Exception ex)
            {
                _microphoneStopError ??= ex;
            }
        }
    }

    private void System_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (IsPaused || e.BytesRecorded <= 0) return;

        lock (_systemWriteGate)
        {
            try
            {
                _systemWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            }
            catch (Exception ex)
            {
                _systemStopError ??= ex;
            }
        }
    }

    public Task PauseAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_current is null)
            throw new InvalidOperationException("No meeting is recording.");

        if (IsPaused) return Task.CompletedTask;

        IsPaused = true;
        _pausedAt = DateTimeOffset.Now;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_current is null)
            throw new InvalidOperationException("No meeting is recording.");

        if (!IsPaused) return Task.CompletedTask;

        if (_pausedAt.HasValue)
            _pausedTotal += DateTimeOffset.Now - _pausedAt.Value;

        _pausedAt = null;
        IsPaused = false;
        return Task.CompletedTask;
    }

    public async Task<MeetingRecord> StopAsync(IReadOnlyList<ImportantMark> marks, CancellationToken ct)
    {
        if (_current is null)
            throw new InvalidOperationException("No meeting is recording.");

        var meeting = _current;
        var end = DateTimeOffset.Now;

        if (IsPaused && _pausedAt.HasValue)
        {
            _pausedTotal += end - _pausedAt.Value;
            _pausedAt = null;
        }

        try
        {
            await StopNativeCaptureAsync(ct);

            var micValid = IsUsefulWave(_microphoneRawPath);
            var systemValid = IsUsefulWave(_systemRawPath);

            LastStopMicrophoneValid = micValid;
            LastStopSystemAudioValid = systemValid;

            if (!micValid && !systemValid)
                throw new InvalidOperationException(
                    "No usable audio reached the recorder. Check the microphone and speaker/output device, then try again.");

            var original = Path.Combine(meeting.FolderPath, "01_Original_Capture.mka");
            var audio = Path.Combine(meeting.FolderPath, "02_Meeting_Audio.m4a");
            var ffLog = Path.Combine(AppPaths.Logs, $"ffmpeg_native_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            IReadOnlyList<string> originalArgs;

            if (micValid && systemValid)
            {
                originalArgs = new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-i", _microphoneRawPath,
                    "-i", _systemRawPath,
                    "-filter_complex",
                    "[0:a]aresample=async=1:first_pts=0[mic];" +
                    "[1:a]aresample=async=1:first_pts=0[sys];" +
                    "[mic][sys]amix=inputs=2:duration=longest:normalize=1[mix]",
                    "-map", "[mix]",
                    "-c:a", "flac",
                    original
                };
            }
            else
            {
                var source = micValid ? _microphoneRawPath : _systemRawPath;
                originalArgs = new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-i", source,
                    "-c:a", "flac",
                    original
                };
            }

            var originalCode = await ProcessService.RunAsync(
                _settings.FfmpegExe,
                originalArgs,
                ffLog,
                ct);

            if (originalCode != 0 || !File.Exists(original) || new FileInfo(original).Length < 512)
                throw new InvalidOperationException(
                    "The meeting audio was captured, but the lossless master file could not be finalized.");

            var m4aCode = await ProcessService.RunAsync(
                _settings.FfmpegExe,
                new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-i", original,
                    "-vn",
                    "-c:a", "aac",
                    "-b:a", "192k",
                    audio
                },
                ffLog,
                ct);

            if (m4aCode != 0 || !File.Exists(audio) || new FileInfo(audio).Length < 256)
                throw new InvalidOperationException(
                    "The lossless meeting master was saved, but the M4A working copy could not be created.");

            meeting.EndLocal = end;
            meeting.DurationSeconds = (int)Math.Max(
                0,
                (end - meeting.StartLocal - _pausedTotal).TotalSeconds);
            meeting.RecordingPath = original;
            meeting.AudioPath = audio;
            meeting.TranscriptPath = Path.Combine(meeting.FolderPath, "03_Transcript.txt");
            meeting.SrtPath = Path.Combine(meeting.FolderPath, "04_Transcript.srt");
            meeting.TranscriptionStatus = "Queued for transcript";
            meeting.MarksCount = marks.Count;

            foreach (var mark in marks)
            {
                mark.MeetingId = meeting.Id;
                _repository.AddMark(mark);
            }

            MeetingMetadataService.Write(meeting);
            MeetingMetadataService.WriteMarks(meeting, marks);
            IntegrityService.Write(meeting);
            _repository.Upsert(meeting);

            // Raw device streams are temporary implementation details. Keep the lossless
            // composite master and the M4A, then remove the raw scratch files.
            TryDelete(_microphoneRawPath);
            TryDelete(_systemRawPath);

            return meeting;
        }
        catch
        {
            meeting.EndLocal = end;
            meeting.DurationSeconds = (int)Math.Max(
                0,
                (end - meeting.StartLocal - _pausedTotal).TotalSeconds);
            meeting.TranscriptionStatus = "Audio processing issue";
            _repository.Upsert(meeting);
            throw;
        }
        finally
        {
            BackgroundTranscriptionScheduler.SetRecordingActive(false);
            _current = null;
            IsPaused = false;
            _pausedAt = null;
            _pausedTotal = TimeSpan.Zero;
            ResetCaptureFields();
        }
    }

    private async Task StopNativeCaptureAsync(CancellationToken ct)
    {
        try { _microphoneCapture?.StopRecording(); } catch { }
        try { _systemCapture?.StopRecording(); } catch { }

        var waits = new List<Task>();

        if (_microphoneStopped is not null)
            waits.Add(_microphoneStopped.Task);

        if (_systemStopped is not null)
            waits.Add(_systemStopped.Task);

        if (waits.Count > 0)
        {
            var all = Task.WhenAll(waits);
            var timeout = Task.Delay(TimeSpan.FromSeconds(5), ct);
            await Task.WhenAny(all, timeout);
        }

        lock (_microphoneWriteGate)
        {
            try { _microphoneWriter?.Flush(); } catch { }
            try { _microphoneWriter?.Dispose(); } catch { }
            _microphoneWriter = null;
        }

        lock (_systemWriteGate)
        {
            try { _systemWriter?.Flush(); } catch { }
            try { _systemWriter?.Dispose(); } catch { }
            _systemWriter = null;
        }

        try { _microphoneCapture?.Dispose(); } catch { }
        try { _systemCapture?.Dispose(); } catch { }

        _microphoneCapture = null;
        _systemCapture = null;
    }

    private void ResetCaptureFields()
    {
        try { _microphoneWriter?.Dispose(); } catch { }
        try { _systemWriter?.Dispose(); } catch { }
        try { _microphoneCapture?.Dispose(); } catch { }
        try { _systemCapture?.Dispose(); } catch { }

        _microphoneWriter = null;
        _systemWriter = null;
        _microphoneCapture = null;
        _systemCapture = null;
        _microphoneStopped = null;
        _systemStopped = null;
        _microphoneStopError = null;
        _systemStopError = null;
        _microphoneRawPath = "";
        _systemRawPath = "";
    }

    private static bool IsUsefulWave(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 64;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
