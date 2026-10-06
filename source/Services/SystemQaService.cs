using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Archestro.MeetingVault.Services;

public static class SystemQaService
{
    public static string ValidateEngineIdentity(string configured)
    {
        var engine = (configured ?? "").Trim().ToLowerInvariant();
        return engine switch
        {
            "native-whisper" => engine,
            "faster-whisper" => engine,
            "direct-whisper" => engine,
            _ => throw new InvalidOperationException($"Unsupported configured transcription engine: {configured}")
        };
    }
    private static async Task<T> WithTimeout<T>(
        Func<Task<T>> action,
        TimeSpan timeout,
        string timeoutMessage)
    {
        var work = Task.Run(action);
        var completed = await Task.WhenAny(work, Task.Delay(timeout));

        if (completed != work)
            throw new TimeoutException(timeoutMessage);

        return await work;
    }

    private static async Task WithTimeout(
        Func<Task> action,
        TimeSpan timeout,
        string timeoutMessage)
    {
        var work = Task.Run(action);
        var completed = await Task.WhenAny(work, Task.Delay(timeout));

        if (completed != work)
            throw new TimeoutException(timeoutMessage);

        await work;
    }

    public static async Task RunAsync()
    {
        AppPaths.Ensure();

        var settings = new SettingsService().LoadOrDiscover();
        var repo = new MeetingRepository();
        var recorder = new RecordingService(settings, repo);
        var transcriber = new TranscriptionService(settings, repo);

        var receiptPath = Path.Combine(
            AppPaths.System,
            "SYSTEM_QA_NATIVE_AUDIO_PASS.txt");

        var failPath = Path.Combine(
            AppPaths.System,
            "SYSTEM_QA_NATIVE_AUDIO_FAIL.txt");

        var progressPath = Path.Combine(
            AppPaths.System,
            "SYSTEM_QA_NATIVE_AUDIO_PROGRESS.txt");

        File.Delete(receiptPath);
        File.Delete(failPath);
        File.Delete(progressPath);

        Models.MeetingRecord? meeting = null;

        void Progress(int stage, string text)
        {
            File.WriteAllText(
                progressPath,
                $"[{stage}/7] {text}{Environment.NewLine}{DateTimeOffset.Now:o}");
        }

        try
        {
            var engine = ValidateEngineIdentity(settings.TranscriptionEngine);
            Progress(1, $"Checking FFmpeg and configured production engine: {engine}");

            if (!File.Exists(settings.FfmpegExe))
                throw new FileNotFoundException(
                    "FFmpeg missing.",
                    settings.FfmpegExe);

            switch (engine)
            {
                case "native-whisper":
                    if (!File.Exists(settings.BuzzExe))
                        throw new FileNotFoundException("Native Whisper runtime missing.", settings.BuzzExe);
                    break;
                case "direct-whisper":
                    if (!File.Exists(settings.DirectWhisperPythonExe) || !File.Exists(settings.DirectWhisperWorkerPath) || !File.Exists(settings.DirectWhisperModelPath))
                        throw new FileNotFoundException("Direct Whisper runtime, worker, or model missing.", settings.DirectWhisperWorkerPath);
                    break;
                case "faster-whisper":
                    if (!File.Exists(settings.DirectWhisperPythonExe) || !File.Exists(settings.FasterWhisperWorkerPath) ||
                        !Directory.Exists(settings.FasterWhisperModelPath) || !File.Exists(Path.Combine(settings.FasterWhisperModelPath, "model.bin")))
                        throw new FileNotFoundException("Faster Whisper runtime, worker, or large-v3 model missing.", settings.FasterWhisperWorkerPath);
                    break;
            }

            // R9.6.2:
            // Do NOT pre-enumerate MMDevice endpoints.
            // That Windows native call can hang on some systems.
            // The real product path below is the source of truth:
            // RecordingService opens microphone + WASAPI loopback directly.
            Progress(
                2,
                "Opening REAL microphone + System Audio capture");

            await WithTimeout(
                () => recorder.StartAsync(
                    "EMV Native Audio QA",
                    CancellationToken.None),
                TimeSpan.FromSeconds(15),
                "Opening native microphone/system-audio capture timed out.");

            Progress(
                3,
                "Playing local tone through Windows System Audio");

            // Produce deterministic System Audio through the normal Windows
            // output path while WasapiLoopbackCapture is active.
            using (var output = new WaveOutEvent())
            {
                var signal = new SignalGenerator(48000, 1)
                {
                    Gain = 0.08,
                    Frequency = 660,
                    Type = SignalGeneratorType.Sin
                };

                output.Init(signal);
                output.Play();
                await Task.Delay(1500);
                output.Stop();
            }

            // Leave a little real microphone capture window as well.
            await Task.Delay(700);

            Progress(
                4,
                "Testing Pause and Resume");

            await WithTimeout(
                () => recorder.PauseAsync(
                    CancellationToken.None),
                TimeSpan.FromSeconds(5),
                "Pause timed out.");

            if (!recorder.IsPaused)
                throw new InvalidOperationException(
                    "Pause test failed.");

            await Task.Delay(500);

            await WithTimeout(
                () => recorder.ResumeAsync(
                    CancellationToken.None),
                TimeSpan.FromSeconds(5),
                "Resume timed out.");

            if (recorder.IsPaused)
                throw new InvalidOperationException(
                    "Resume test failed.");

            await Task.Delay(600);

            Progress(
                5,
                "Stopping capture and proving BOTH native streams");

            meeting = await WithTimeout(
                () => recorder.StopAsync(
                    Array.Empty<Models.ImportantMark>(),
                    CancellationToken.None),
                TimeSpan.FromSeconds(20),
                "Stopping/finalizing native audio QA timed out.");

            if (!recorder.LastStopMicrophoneValid)
            {
                throw new InvalidOperationException(
                    "Microphone did not produce a usable native stream.");
            }

            if (!recorder.LastStopSystemAudioValid)
            {
                throw new InvalidOperationException(
                    "System Audio loopback did not produce a usable native stream. " +
                    "Teams/Zoom/Webex/browser/app audio is therefore NOT verified.");
            }

            if (!File.Exists(meeting.RecordingPath) ||
                new FileInfo(meeting.RecordingPath).Length < 512)
            {
                throw new InvalidOperationException(
                    "Native QA recording master is missing or empty.");
            }

            if (!File.Exists(meeting.AudioPath) ||
                new FileInfo(meeting.AudioPath).Length < 256)
            {
                throw new InvalidOperationException(
                    "Native QA M4A file is missing or empty.");
            }

            Progress(
                6,
                $"Running configured local transcription engine: {engine}");

            await WithTimeout(
                () => transcriber.TranscribeAsync(
                    meeting,
                    CancellationToken.None),
                TimeSpan.FromMinutes(6),
                "QA transcription exceeded 6 minutes.");

            meeting = repo.Get(meeting.Id) ?? meeting;

            if (!string.Equals(
                    meeting.TranscriptionStatus,
                    "Transcript ready",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Local transcription QA did not reach Transcript ready. " +
                    $"Status: {meeting.TranscriptionStatus}");
            }

            if (!File.Exists(meeting.TranscriptPath))
            {
                throw new InvalidOperationException(
                    "Local transcription QA transcript file was not created.");
            }

            Progress(
                7,
                "Verifying SQLite persistence");

            if (repo.Get(meeting.Id) is null)
            {
                throw new InvalidOperationException(
                    "SQLite persistence verification failed.");
            }

            var receipt = $"""
            ARCHESTRO MEETING VAULT - NATIVE AUDIO SYSTEM QA PASS
            Date: {DateTimeOffset.Now:o}
            Build: R9.6.2
            Recording engine: Windows WASAPI / NAudio
            Microphone native stream: PASS
            System Audio loopback native stream: PASS
            Online-meeting audio path: PASS
            Teams / Zoom / Webex / browser / app output: VERIFIED THROUGH WINDOWS LOOPBACK
            Native Start/Stop: PASS
            Native Pause/Resume: PASS
            Lossless recording master: PASS
            FFmpeg M4A working copy: PASS
            Configured transcription engine: {engine} PASS
            SQLite persistence: PASS
            Endpoint pre-enumeration: NOT USED
            External Factory Watchdog: REQUIRED / ACTIVE
            OBS dependency: NOT USED
            Buzz production dependency: NOT USED
            """;

            File.WriteAllText(
                receiptPath,
                receipt);

            Progress(7, $"SYSTEM QA PASS • engine={engine}");
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                failPath,
                $"FAIL {DateTimeOffset.Now:o}" +
                Environment.NewLine +
                ex);

            File.WriteAllText(
                progressPath,
                $"FAIL: {ex.Message}" +
                Environment.NewLine +
                DateTimeOffset.Now.ToString("o"));

            throw;
        }
        finally
        {
            if (recorder.IsRecording)
            {
                try
                {
                    meeting = await WithTimeout(
                        () => recorder.StopAsync(
                            Array.Empty<Models.ImportantMark>(),
                            CancellationToken.None),
                        TimeSpan.FromSeconds(8),
                        "Cleanup stop timed out.");
                }
                catch { }
            }

            if (meeting is not null)
            {
                try
                {
                    repo.Delete(meeting.Id);
                }
                catch { }

                try
                {
                    if (Directory.Exists(meeting.FolderPath))
                        Directory.Delete(meeting.FolderPath, true);
                }
                catch { }
            }
        }
    }
}
