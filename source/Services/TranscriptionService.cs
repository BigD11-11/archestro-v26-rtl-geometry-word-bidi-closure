using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public sealed class TranscriptionService
{
    private readonly AppSettings _settings;
    private readonly MeetingRepository _repository;
    private readonly SuggestedMetadataService _suggestions = new();

    // HARD STABILITY RULE: exactly one meeting transcript may run at a time.
    // This prevents multiple 2.9 GB Whisper jobs from overwhelming the manager PC.
    private readonly SemaphoreSlim _serialGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobs = new();
    private readonly ConcurrentDictionary<string, bool> _requeueAfterRecording = new();
    private readonly ConcurrentDictionary<string, bool> _manualStartNow = new();
    private string? _activeMeetingId;

    private static readonly HashSet<string> CommonEnglish = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","about","after","again","all","and","are","as","at","be","because","before","but","by",
        "can","client","company","contract","day","do","for","from","good","have","he","hello","here",
        "how","i","if","in","is","it","meeting","need","new","no","not","of","on","one","or","our",
        "please","project","review","right","so","start","stop","system","test","that","the","their",
        "there","they","this","time","to","today","we","what","when","will","with","work","yes","you"
    };

    private static readonly string[] RomanizedArabicHints =
    {
        "ana","enta","inta","ant","hatha","hada","hadi","hay","eh","esh","shlon","shno","shu",
        "yalla","khalas","tamam","mafi","mashi","wallah","inshallah","mashallah","habibi","habibti",
        "salam","salaam","shukran","shokr","aywa","aewa","naam","laa","wesh","wain","wein","wahne",
        "daraada","alu"
    };

    // Initial decoder prompts are vocabulary context, not instruction channels.
    // Final closeout intentionally avoids instruction prose to prevent prompt leakage.
    private const string BilingualVocabularyHint = "Archestro, meeting, project, contract, procurement, sales, client";


    public TranscriptionService(AppSettings settings, MeetingRepository repository)
    {
        _settings = settings;
        _repository = repository;
        BackgroundTranscriptionRuntime.Configure(settings);
    }

    public bool IsPending(string meetingId) => _jobs.ContainsKey(meetingId);

    public bool RequestStartNow(MeetingRecord meeting)
    {
        _manualStartNow[meeting.Id] = true;

        if (!_jobs.ContainsKey(meeting.Id))
            return false;

        var activeMeetingId = Volatile.Read(ref _activeMeetingId);
        meeting.TranscriptionStatus =
            activeMeetingId is null ||
            string.Equals(activeMeetingId, meeting.Id, StringComparison.Ordinal)
                ? "Starting transcript now…"
                : "Queued • manual start • next after current transcript";

        _repository.Upsert(meeting);
        MeetingMetadataService.Write(meeting);
        return true;
    }

    public void YieldToRecording()
    {
        // R9.5.5: DO NOT cancel/requeue an active transcript when a new meeting starts.
        // Cancel/requeue changed FIFO order and made the user see an active job become
        // "Queued". Recording priority is now enforced by the adaptive process governor:
        // while recording is active, the transcription process is throttled to 1% CPU.
        // The same transcript keeps its place and continues after recording stops.
    }

    public async Task CancelAndWaitAsync(string meetingId, TimeSpan timeout)
    {
        if (_jobs.TryGetValue(meetingId, out var cts))
            cts.Cancel();

        var deadline = DateTimeOffset.Now + timeout;
        while (_jobs.ContainsKey(meetingId) && DateTimeOffset.Now < deadline)
            await Task.Delay(100);
    }

    public void RecoverInterruptedStatuses()
    {
        foreach (var meeting in _repository.Search(null, null, 1000))
        {
            var status = meeting.TranscriptionStatus ?? "";
            var looksActive =
                status.Contains("Queued", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Transcribing", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Mixed-language recovery", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Arabic-script recovery", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Retrying transcript", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Manual start", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("waiting for memory", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("auto-start", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Starting transcript", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("runtime missing", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Repair required", StringComparison.OrdinalIgnoreCase);

            if (!looksActive) continue;

            if (!string.IsNullOrWhiteSpace(meeting.TranscriptPath) && File.Exists(meeting.TranscriptPath))
                meeting.TranscriptionStatus = "Transcript ready";
            else
                meeting.TranscriptionStatus = "Transcript interrupted • Retry available";

            _repository.Upsert(meeting);
            MeetingMetadataService.Write(meeting);
        }
    }

    public async Task TranscribeAsync(
        MeetingRecord meeting,
        CancellationToken ct,
        bool forceMixedLanguageRecovery = false,
        bool startImmediately = false)
    {
        if (startImmediately)
            _manualStartNow[meeting.Id] = true;

        var queuedAt = DateTimeOffset.Now;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (!_jobs.TryAdd(meeting.Id, linked))
        {
            linked.Dispose();
            return;
        }

        var entered = false;
        var shouldRequeue = false;
        var preserveManualStart = false;

        try
        {
            meeting.TranscriptionStatus = _serialGate.CurrentCount == 0
                ? "Queued • waiting for current transcript"
                : (startImmediately
                    ? "Queued • manual start requested"
                    : "Queued • smart background");

            _repository.Upsert(meeting);
            MeetingMetadataService.Write(meeting);

            await _serialGate.WaitAsync(linked.Token);
            entered = true;
            Volatile.Write(ref _activeMeetingId, meeting.Id);

            void UpdateQueueStatus(string status)
            {
                meeting.TranscriptionStatus = status;
                _repository.Upsert(meeting);
                MeetingMetadataService.Write(meeting);
            }

            await BackgroundTranscriptionScheduler.WaitForSmartWindowAsync(
                _settings,
                queuedAt,
                () => _manualStartNow.TryGetValue(
                    meeting.Id,
                    out var requestedNow) && requestedNow,
                UpdateQueueStatus,
                linked.Token);

            await TranscribeCoreAsync(
                meeting,
                linked.Token,
                forceMixedLanguageRecovery);
        }
        catch (OperationCanceledException)
        {
            shouldRequeue =
                _requeueAfterRecording.TryRemove(
                    meeting.Id,
                    out var requested) &&
                requested;

            preserveManualStart =
                _manualStartNow.TryGetValue(
                    meeting.Id,
                    out var requestedManual) &&
                requestedManual;

            meeting.TranscriptionStatus = shouldRequeue
                ? "Queued • recording has priority"
                : "Transcript canceled • Retry available";

            _repository.Upsert(meeting);
            MeetingMetadataService.Write(meeting);
        }
        finally
        {
            if (entered)
            {
                Interlocked.CompareExchange(ref _activeMeetingId, null, meeting.Id);
                _serialGate.Release();
            }

            _jobs.TryRemove(meeting.Id, out _);

            if (!shouldRequeue)
                _manualStartNow.TryRemove(meeting.Id, out _);

            linked.Dispose();
        }

        if (shouldRequeue)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));

                    await TranscribeAsync(
                        meeting,
                        CancellationToken.None,
                        forceMixedLanguageRecovery,
                        startImmediately: preserveManualStart);
                }
                catch { }
            });
        }
    }

    private async Task TranscribeCoreAsync(
        MeetingRecord meeting,
        CancellationToken ct,
        bool forceMixedLanguageRecovery)
    {
        var coreTimer = Stopwatch.StartNew();
        var useGroq = !_settings.ProcessingMode.Equals("Offline", StringComparison.OrdinalIgnoreCase) &&
                      (_settings.ProcessingMode.Equals("Cloud Fast", StringComparison.OrdinalIgnoreCase) ||
                       (_settings.ProcessingMode.Equals("Custom", StringComparison.OrdinalIgnoreCase) &&
                        _settings.TranscriptionProvider.Equals("Groq", StringComparison.OrdinalIgnoreCase)));
        if (useGroq)
        {
            await TranscribeWithGroqAsync(meeting, ct);
            return;
        }

        var fasterEngine =
            _settings.TranscriptionEngine.Equals(
                "faster-whisper",
                StringComparison.OrdinalIgnoreCase);

        var directEngine =
            _settings.TranscriptionEngine.Equals(
                "direct-whisper",
                StringComparison.OrdinalIgnoreCase);

        if (fasterEngine)
        {
            if (string.IsNullOrWhiteSpace(_settings.DirectWhisperPythonExe) ||
                !File.Exists(_settings.DirectWhisperPythonExe) ||
                string.IsNullOrWhiteSpace(_settings.FasterWhisperWorkerPath) ||
                !File.Exists(_settings.FasterWhisperWorkerPath) ||
                string.IsNullOrWhiteSpace(_settings.FasterWhisperModelPath) ||
                !Directory.Exists(_settings.FasterWhisperModelPath))
            {
                meeting.TranscriptionStatus =
                    "Faster Whisper runtime missing • Repair required";
                _repository.Upsert(meeting);
                MeetingMetadataService.Write(meeting);
                return;
            }
        }
        else if (directEngine)
        {
            if (string.IsNullOrWhiteSpace(_settings.DirectWhisperPythonExe) ||
                !File.Exists(_settings.DirectWhisperPythonExe) ||
                string.IsNullOrWhiteSpace(_settings.DirectWhisperWorkerPath) ||
                !File.Exists(_settings.DirectWhisperWorkerPath) ||
                string.IsNullOrWhiteSpace(_settings.DirectWhisperModelPath) ||
                !File.Exists(_settings.DirectWhisperModelPath))
            {
                meeting.TranscriptionStatus =
                    "Direct Whisper runtime missing • Repair required";
                _repository.Upsert(meeting);
                MeetingMetadataService.Write(meeting);
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(_settings.BuzzExe) ||
                 !File.Exists(_settings.BuzzExe))
        {
            meeting.TranscriptionStatus =
                _settings.TranscriptionEngine.Equals("native-whisper", StringComparison.OrdinalIgnoreCase)
                    ? "Native transcription runtime missing • Repair required"
                    : "Legacy Buzz engine missing";
            _repository.Upsert(meeting);
            MeetingMetadataService.Write(meeting);
            return;
        }

        TranscriptionProgressService.Begin(
            meeting.Id,
            Math.Max(1, meeting.DurationSeconds));

        meeting.TranscriptionStatus = "Transcribing…";
        _repository.Upsert(meeting);
        MeetingMetadataService.Write(meeting);

        var workRoot = Path.Combine(
            meeting.FolderPath,
            "_TRANSCRIPTION_WORK");

        var primaryPromptDir = Path.Combine(workRoot, "AUTO_BILINGUAL");
        var primaryCompatDir = Path.Combine(workRoot, "AUTO_COMPAT");
        var arabicDir = Path.Combine(workRoot, "ARABIC_RECOVERY");
        var englishDir = Path.Combine(workRoot, "ENGLISH_RECOVERY");
        var diagnosticsDir = Path.Combine(
            meeting.FolderPath,
            "_TRANSCRIPTION_DIAGNOSTICS");

        TryResetDirectory(workRoot);
        Directory.CreateDirectory(diagnosticsDir);

        var success = false;

        try
        {
            // PASS 1:
            // Auto language detection + explicit code-switch preservation prompt.
            Directory.CreateDirectory(primaryPromptDir);
            TranscriptionProgressService.SetStage(
                meeting.Id,
                "Transcribing • Arabic + English");

            var primary = await RunPassAsync(
                meeting,
                primaryPromptDir,
                diagnosticsDir,
                language: null,
                prompt: null,
                ct);

            // Compatibility fallback only if the prompted pass itself failed.
            if (!primary.Success ||
                string.IsNullOrWhiteSpace(primary.Text))
            {
                Directory.CreateDirectory(primaryCompatDir);
                TranscriptionProgressService.SetStage(
                    meeting.Id,
                    "Transcribing • compatibility retry");

                primary = await RunPassAsync(
                    meeting,
                    primaryCompatDir,
                    diagnosticsDir,
                    language: null,
                    prompt: null,
                    ct);
            }

            if (!primary.Success ||
                string.IsNullOrWhiteSpace(primary.Text))
            {
                FailMeeting(
                    meeting,
                    "Transcript failed • diagnostics saved • Retry available");
                return;
            }

            var selected = primary;
            var recoveryNotes = new List<string>();

            var smartBilingual =
                _settings.TranscriptionLanguageMode.Equals(
                    "Arabic + English",
                    StringComparison.OrdinalIgnoreCase) ||
                _settings.TranscriptionLanguageMode.Equals(
                    "Auto",
                    StringComparison.OrdinalIgnoreCase);

            if (smartBilingual)
            {
                var primaryHasArabic =
                    MixedLanguageChunkRecoveryService.ContainsArabic(
                        primary.Text);

                var primaryHasEnglish =
                    BilingualTranscriptFusionService
                        .HasMeaningfulEnglishCoverage(primary.Text);

                // AUTOMATIC RECOVERY IS BOUNDED:
                // at most ONE extra whole-file model pass.
                // No fixed 10-second chunk farm and no repeated model loads.
                var automaticRecovery =
                    !fasterEngine &&
                    _settings.AutomaticMixedLanguageRecovery &&
                    meeting.DurationSeconds >= Math.Max(
                        _settings.AutoTranscribeMinimumSeconds,
                        _settings.AutomaticBilingualRecoveryMinimumSeconds) &&
                    meeting.DurationSeconds <= Math.Max(
                        _settings.AutomaticBilingualRecoveryMinimumSeconds,
                        _settings.AutomaticWholeFileRecoveryMaxSeconds);

                var needArabic =
                    !primaryHasArabic;

                var needEnglish =
                    primaryHasArabic &&
                    !primaryHasEnglish;

                if (forceMixedLanguageRecovery)
                {
                    // Manual Improve is allowed to run both repairs, sequentially.
                    needArabic = true;
                    needEnglish = true;
                }
                else if (!automaticRecovery)
                {
                    needArabic = false;
                    needEnglish = false;
                }

                // For automatic mode choose one missing-language repair only.
                // Arabic gets priority when no Arabic script survived at all.
                if (!forceMixedLanguageRecovery &&
                    needArabic &&
                    needEnglish)
                {
                    needEnglish = false;
                }

                if (needEnglish)
                {
                    Directory.CreateDirectory(englishDir);
                    TranscriptionProgressService.SetStage(
                        meeting.Id,
                        forceMixedLanguageRecovery
                            ? "Quality repair • English"
                            : "Pass 2 • English recovery");

                    var englishRetry = await RunPassAsync(
                        meeting,
                        englishDir,
                        diagnosticsDir,
                        language: "en",
                        prompt: null,
                        ct);

                    if (englishRetry.Success &&
                        !string.IsNullOrWhiteSpace(englishRetry.Text) &&
                        !string.IsNullOrWhiteSpace(selected.SrtPath) &&
                        File.Exists(selected.SrtPath) &&
                        !string.IsNullOrWhiteSpace(englishRetry.SrtPath) &&
                        File.Exists(englishRetry.SrtPath))
                    {
                        var fusedEnglishSrt = Path.Combine(
                            workRoot,
                            "FUSED_ENGLISH_RECOVERY.srt");

                        var fusion =
                            BilingualTranscriptFusionService
                                .FuseEnglishRecovery(
                                    selected.Text,
                                    selected.SrtPath,
                                    englishRetry.Text,
                                    englishRetry.SrtPath,
                                    fusedEnglishSrt);

                        if (fusion.ReplacedSegments > 0 &&
                            !string.IsNullOrWhiteSpace(fusion.Text))
                        {
                            selected = new PassResult(
                                true,
                                fusion.Text,
                                fusion.SrtPath);

                            recoveryNotes.Add(
                                $"English recovery replaced {fusion.ReplacedSegments} timed segment(s).");
                        }
                    }
                }

                if (needArabic)
                {
                    Directory.CreateDirectory(arabicDir);
                    TranscriptionProgressService.SetStage(
                        meeting.Id,
                        forceMixedLanguageRecovery
                            ? "Quality repair • Arabic"
                            : "Pass 2 • Arabic recovery");

                    var arabicRetry = await RunPassAsync(
                        meeting,
                        arabicDir,
                        diagnosticsDir,
                        language: "ar",
                        prompt: null,
                        ct);

                    if (arabicRetry.Success &&
                        !string.IsNullOrWhiteSpace(arabicRetry.Text) &&
                        !string.IsNullOrWhiteSpace(selected.SrtPath) &&
                        File.Exists(selected.SrtPath) &&
                        !string.IsNullOrWhiteSpace(arabicRetry.SrtPath) &&
                        File.Exists(arabicRetry.SrtPath))
                    {
                        var fusedArabicSrt = Path.Combine(
                            workRoot,
                            "FUSED_ARABIC_RECOVERY.srt");

                        var fusion =
                            BilingualTranscriptFusionService.Fuse(
                                selected.Text,
                                selected.SrtPath,
                                arabicRetry.Text,
                                arabicRetry.SrtPath,
                                fusedArabicSrt);

                        if (fusion.ReplacedSegments > 0 &&
                            !string.IsNullOrWhiteSpace(fusion.Text))
                        {
                            selected = new PassResult(
                                true,
                                fusion.Text,
                                fusion.SrtPath);

                            recoveryNotes.Add(
                                $"Arabic recovery replaced {fusion.ReplacedSegments} timed segment(s).");
                        }
                    }
                }
            }

            var targetTxt = Path.Combine(
                meeting.FolderPath,
                "03_Transcript.txt");

            var targetSrt = Path.Combine(
                meeting.FolderPath,
                "04_Transcript.srt");

            var rawTranscript = selected.Text.Trim();
            if (LooksLikePromptLeak(rawTranscript))
            {
                recoveryNotes.Add("Prompt-leak guard rejected contaminated decoder-context text; clean-pass output required.");
                throw new InvalidOperationException("Transcription output matched internal decoder prompt/instruction text and was rejected before commit.");
            }
            var marks = _repository.GetMarks(meeting.Id);

            var selectedSrt =
                !string.IsNullOrWhiteSpace(selected.SrtPath) &&
                File.Exists(selected.SrtPath)
                    ? selected.SrtPath
                    : null;

            var annotatedTranscript =
                TranscriptContextService.BuildAnnotatedTranscript(
                    rawTranscript,
                    marks,
                    selectedSrt,
                    usedArabicRetry: false);

            if (recoveryNotes.Count > 0)
            {
                annotatedTranscript =
                    annotatedTranscript.TrimEnd() +
                    Environment.NewLine +
                    Environment.NewLine +
                    "BILINGUAL RECOVERY / استعادة ثنائية اللغة" +
                    Environment.NewLine +
                    string.Join(
                        Environment.NewLine,
                        recoveryNotes.Select(n => "• " + n)) +
                    Environment.NewLine;
            }

            await File.WriteAllTextAsync(
                targetTxt,
                annotatedTranscript,
                new UTF8Encoding(false),
                ct);

            if (!string.IsNullOrWhiteSpace(selected.SrtPath) &&
                File.Exists(selected.SrtPath))
            {
                File.Copy(
                    selected.SrtPath,
                    targetSrt,
                    true);
            }
            else
            {
                TryDelete(targetSrt);
            }

            var suggestion =
                _suggestions.Suggest(rawTranscript);

            meeting.TranscriptPath = targetTxt;
            meeting.SrtPath =
                File.Exists(targetSrt)
                    ? targetSrt
                    : "";

            meeting.TranscriptionStatus = "Transcript ready";
            meeting.SuggestedTitle = suggestion.SuggestedTitle;

            // Category suggestions are advisory only. A transcript/topic classifier must never
            // silently convert a user-owned category. New meetings remain Uncategorized until
            // the user explicitly chooses a category from the UI.
            _repository.Upsert(
                meeting,
                annotatedTranscript);

            MeetingMetadataService.Write(meeting);
            IntegrityService.Write(meeting);

            success = true;
            coreTimer.Stop();
            try
            {
                new AiUsageLedgerService().Record(new AiUsageRow(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                    meeting.Id, null, "Local", _settings.TranscriptionEngine, "Transcription", null, null, null,
                    null, null, null, null, coreTimer.ElapsedMilliseconds, true, null, 0, 0, "LOCAL_ZERO",
                    AudioDurationSeconds: Math.Max(0, meeting.DurationSeconds), RequestCount: 1));
            }
            catch { /* A ledger issue must not invalidate an already-written transcript. */ }
            TryDeleteDirectory(workRoot);
        }
        catch (OperationCanceledException)
        {
            FailMeeting(
                meeting,
                "Transcript canceled • Retry available");
            throw;
        }
        catch
        {
            FailMeeting(
                meeting,
                "Transcript failed • diagnostics saved • Retry available");
        }
        finally
        {
            TranscriptionProgressService.Complete(
                meeting.Id,
                success);
        }
    }

    private async Task TranscribeWithGroqAsync(MeetingRecord meeting, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.EncryptedGroqApiKey))
                throw new InvalidOperationException("Groq is not configured. Save a Groq key or choose Local transcription.");
            if (string.IsNullOrWhiteSpace(meeting.AudioPath) || !File.Exists(meeting.AudioPath))
                throw new FileNotFoundException("The original audio is still retained and can be retried.");
            var key = CloudSecretProtector.Unprotect(_settings.EncryptedGroqApiKey);
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Groq key is unavailable. Save the key again or use Local transcription.");
            TranscriptionProgressService.Begin(meeting.Id, Math.Max(1, meeting.DurationSeconds));
            TranscriptionProgressService.SetStage(meeting.Id, "Cloud transcription • Groq Whisper Large V3 Turbo");
            meeting.TranscriptionStatus = "Transcribing • Groq Whisper Large V3 Turbo";
            _repository.Upsert(meeting); MeetingMetadataService.Write(meeting);

            var result = await new GroqTranscriptionService().TranscribeAsync(meeting.AudioPath, key, ct, _settings.FfmpegExe);
            var transcriptPath = string.IsNullOrWhiteSpace(meeting.TranscriptPath)
                ? Path.Combine(meeting.FolderPath, "03_Transcript.txt") : meeting.TranscriptPath;
            var srtPath = string.IsNullOrWhiteSpace(meeting.SrtPath)
                ? Path.Combine(meeting.FolderPath, "04_Transcript.srt") : meeting.SrtPath;
            await File.WriteAllTextAsync(transcriptPath, result.Text, new System.Text.UTF8Encoding(false), ct);
            var srt = result.Segments.Count == 0
                ? "1\r\n00:00:00,000 --> 00:00:00,000\r\n" + result.Text + "\r\n"
                : string.Join("\r\n\r\n", result.Segments.Select((segment, index) =>
                    $"{index + 1}\r\n{SrtTime(segment.Start)} --> {SrtTime(segment.End)}\r\n{segment.Text}")) + "\r\n";
            await File.WriteAllTextAsync(srtPath, srt, new System.Text.UTF8Encoding(false), ct);
            meeting.TranscriptPath = transcriptPath; meeting.SrtPath = srtPath; meeting.TranscriptionStatus = "Transcript ready • Groq";
            meeting.EndLocal ??= meeting.StartLocal.AddSeconds(Math.Max(0, meeting.DurationSeconds));
            _repository.Upsert(meeting); MeetingMetadataService.Write(meeting);
            await GroqTranscriptionService.RecordCostAsync(new AiUsageLedgerService(), meeting.Id,
                Math.Max(0, meeting.DurationSeconds), result.ElapsedMs, result.RequestId, success: true, requestCount: result.RequestCount);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            meeting.TranscriptionStatus = "Groq transcription needs attention • Retry Groq or Use Local";
            _repository.Upsert(meeting); MeetingMetadataService.Write(meeting);
            var elapsed = (long)Math.Max(0, (DateTimeOffset.UtcNow - started).TotalMilliseconds);
            new AiUsageLedgerService().Record(new AiUsageRow(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                meeting.Id, null, "Groq", GroqTranscriptionService.Model, "Transcription", null, null, null, null,
                null, null, null, elapsed, false, null, null, null, "PROVIDER_FAILURE"), ex.GetType().Name);
        }
    }

    private static string SrtTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
    }

    private async Task<PassResult> RunPassAsync(
        MeetingRecord meeting,
        string outputDirectory,
        string diagnosticsDirectory,
        string? language,
        string? prompt,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(diagnosticsDirectory);

        var passName = string.IsNullOrWhiteSpace(language)
            ? "auto"
            : language;

        if (string.IsNullOrWhiteSpace(prompt))
            passName += "_compat";

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var log = Path.Combine(
            AppPaths.Logs,
            $"buzz_{passName}_{meeting.Id[..Math.Min(8, meeting.Id.Length)]}_{stamp}.log");

        var diagnostic = Path.Combine(
            diagnosticsDirectory,
            $"BUZZ_{passName}_{stamp}_DIAGNOSTIC.txt");

        List<string> args;
        string executable;
        string engineLabel;

        var fasterEngine =
            _settings.TranscriptionEngine.Equals(
                "faster-whisper",
                StringComparison.OrdinalIgnoreCase);

        var directEngine =
            _settings.TranscriptionEngine.Equals(
                "direct-whisper",
                StringComparison.OrdinalIgnoreCase);

        if (fasterEngine)
        {
            executable = _settings.DirectWhisperPythonExe;
            engineLabel = "Faster Whisper / CTranslate2";

            args = new List<string>
            {
                _settings.FasterWhisperWorkerPath,
                "--audio", meeting.AudioPath,
                "--model", _settings.FasterWhisperModelPath,
                "--output-dir", outputDirectory,
                "--threads",
                Math.Clamp(
                    _settings.BackgroundTranscriptionMaxCpuThreads,
                    1,
                    8).ToString(),
                "--batch-size",
                Math.Clamp(
                    _settings.FasterWhisperBatchSize,
                    1,
                    4).ToString(),
                "--compute-type",
                string.IsNullOrWhiteSpace(_settings.FasterWhisperComputeType)
                    ? "int8_float32"
                    : _settings.FasterWhisperComputeType,
                "--mixed-chunk-seconds",
                Math.Clamp(
                    _settings.FasterWhisperMixedChunkSeconds,
                    5,
                    20).ToString()
            };

            if (!string.IsNullOrWhiteSpace(language))
            {
                args.Add("--language");
                args.Add(language);
            }

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                args.Add("--prompt");
                args.Add(prompt);
            }
        }
        else if (directEngine)
        {
            executable = _settings.DirectWhisperPythonExe;
            engineLabel = "Direct OpenAI Whisper";

            args = new List<string>
            {
                _settings.DirectWhisperWorkerPath,
                "--audio", meeting.AudioPath,
                "--model", _settings.DirectWhisperModelPath,
                "--output-dir", outputDirectory,
                "--threads",
                Math.Clamp(
                    _settings.BackgroundTranscriptionMaxCpuThreads,
                    1,
                    8).ToString()
            };

            if (!string.IsNullOrWhiteSpace(_settings.FfmpegExe))
            {
                args.Add("--ffmpeg");
                args.Add(_settings.FfmpegExe);
            }

            if (meeting.DurationSeconds <= 120)
                args.Add("--short-audio");

            if (!string.IsNullOrWhiteSpace(language))
            {
                args.Add("--language");
                args.Add(language);
            }

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                args.Add("--prompt");
                args.Add(prompt);
            }
        }
        else
        {
            // Legacy fallback retained only so an older settings file can still
            // be repaired. R9.5 owner/manager installers set direct-whisper.
            executable = _settings.BuzzExe;
            engineLabel = _settings.TranscriptionEngine.Equals("native-whisper", StringComparison.OrdinalIgnoreCase)
                ? "Archestro Native Whisper"
                : "Legacy Buzz";

            args = new List<string>
            {
                "add",
                "--task","transcribe",
                "--model-type",_settings.BuzzModelType,
                "--model-size",_settings.BuzzModelSize
            };

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                args.Add("--prompt");
                args.Add(prompt);
            }

            args.Add("--txt");
            args.Add("--srt");
            args.Add("--hide-gui");
            args.Add("--output-directory");
            args.Add(outputDirectory);

            if (!string.IsNullOrWhiteSpace(language))
            {
                args.Add("--language");
                args.Add(language);
            }

            args.Add(meeting.AudioPath);
        }

        var passStarted = DateTimeOffset.Now;

        ProcessRunResult run;
        try
        {
            run = await ProcessService.RunDetailedAsync(
                executable,
                args,
                log,
                outputDirectory,
                ct);
        }
        catch (Exception ex)
        {
            await WritePassDiagnosticAsync(
                diagnostic,
                meeting,
                passName,
                args,
                outputDirectory,
                passStarted,
                run: null,
                discovered: Array.Empty<string>(),
                note:
                    $"{engineLabel} process threw before a normal exit.",
                exception: ex,
                ct);

            return PassResult.Failed;
        }

        // Some Windows GUI-packaged executables can return before their worker has
        // finished flushing exported files. Do not fail immediately.
        var discovery = await WaitForOutputsAsync(
            meeting,
            outputDirectory,
            passStarted,
            TimeSpan.FromSeconds(25),
            ct);

        var txt = discovery
            .Where(IsTranscriptTextCandidate)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        var srt = discovery
            .Where(p => p.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        string text = "";

        if (!string.IsNullOrWhiteSpace(txt) && File.Exists(txt))
        {
            text = await File.ReadAllTextAsync(txt, ct);
        }
        else if (!string.IsNullOrWhiteSpace(srt) && File.Exists(srt))
        {
            // If Buzz produced only SRT, recover a usable transcript instead of
            // declaring the entire meeting failed.
            var cues = TranscriptContextService.ParseSrt(srt);
            text = string.Join(
                Environment.NewLine,
                cues.Select(c => c.Text)
                    .Where(v => !string.IsNullOrWhiteSpace(v)));

            if (!string.IsNullOrWhiteSpace(text))
            {
                var recoveredTxt = Path.Combine(
                    outputDirectory,
                    "RECOVERED_FROM_SRT.txt");

                await File.WriteAllTextAsync(
                    recoveredTxt,
                    text,
                    new UTF8Encoding(false),
                    ct);

                txt = recoveredTxt;
            }
        }

        var success =
            run.ExitCode == 0 &&
            !string.IsNullOrWhiteSpace(text);

        var note = success
            ? "PASS: usable transcript output discovered."
            : run.ExitCode != 0
                ? $"FAIL: Buzz returned exit code {run.ExitCode}."
                : "FAIL: Buzz returned exit code 0 but no usable TXT/SRT output was discovered after the grace period.";

        await WritePassDiagnosticAsync(
            diagnostic,
            meeting,
            passName,
            args,
            outputDirectory,
            passStarted,
            run,
            discovery,
            note,
            exception: null,
            ct);

        if (!success)
            return PassResult.Failed;

        return new PassResult(
            true,
            text,
            srt ?? "");
    }

    private async Task<IReadOnlyList<string>> WaitForOutputsAsync(
        MeetingRecord meeting,
        string outputDirectory,
        DateTimeOffset passStarted,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.Now + timeout;
        IReadOnlyList<string> latest = Array.Empty<string>();

        while (DateTimeOffset.Now <= deadline)
        {
            latest = DiscoverPassOutputs(
                meeting,
                outputDirectory,
                passStarted);

            if (latest.Any(IsTranscriptTextCandidate) ||
                latest.Any(p => p.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)))
            {
                return latest;
            }

            await Task.Delay(500, ct);
        }

        return DiscoverPassOutputs(
            meeting,
            outputDirectory,
            passStarted);
    }

    private static IReadOnlyList<string> DiscoverPassOutputs(
        MeetingRecord meeting,
        string outputDirectory,
        DateTimeOffset passStarted)
    {
        var candidates = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        void AddFrom(string? root, bool recursive)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return;

            try
            {
                foreach (var file in Directory.EnumerateFiles(
                    root,
                    "*.*",
                    recursive
                        ? SearchOption.AllDirectories
                        : SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file);
                    if (!ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".srt", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".vtt", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var name = Path.GetFileName(file);

                    // Never accidentally adopt our already-finalized meeting outputs
                    // from a previous transcript attempt.
                    if (name.Equals("03_Transcript.txt", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("04_Transcript.srt", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("_DIAGNOSTIC.txt", StringComparison.OrdinalIgnoreCase))
                        continue;

                    DateTimeOffset modified;
                    try
                    {
                        modified = File.GetLastWriteTimeUtc(file);
                    }
                    catch
                    {
                        continue;
                    }

                    if (modified < passStarted.UtcDateTime.AddSeconds(-3))
                        continue;

                    candidates.Add(file);
                }
            }
            catch { }
        }

        // Expected export directory.
        AddFrom(outputDirectory, recursive: true);

        // Some Buzz/packaging combinations may emit next to the source audio
        // even when --output-directory was requested.
        AddFrom(meeting.FolderPath, recursive: false);

        // Also scan the transcription work root only, never the whole vault.
        var workRoot = Path.Combine(
            meeting.FolderPath,
            "_TRANSCRIPTION_WORK");

        AddFrom(workRoot, recursive: true);

        return candidates
            .OrderByDescending(p =>
            {
                try { return File.GetLastWriteTimeUtc(p); }
                catch { return DateTime.MinValue; }
            })
            .ToList();
    }

    private static bool IsTranscriptTextCandidate(string path)
    {
        if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return false;

        var name = Path.GetFileName(path);

        return !name.EndsWith("_DIAGNOSTIC.txt", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("03_Transcript.txt", StringComparison.OrdinalIgnoreCase);
    }

    private async Task WritePassDiagnosticAsync(
        string diagnosticPath,
        MeetingRecord meeting,
        string passName,
        IReadOnlyList<string> args,
        string outputDirectory,
        DateTimeOffset passStarted,
        ProcessRunResult? run,
        IReadOnlyList<string> discovered,
        string note,
        Exception? exception,
        CancellationToken ct)
    {
        var sb = new StringBuilder();

        sb.AppendLine("ARCHESTRO MEETING VAULT - LEGACY TRANSCRIPTION DIAGNOSTIC");
        sb.AppendLine($"MeetingId: {meeting.Id}");
        sb.AppendLine($"MeetingTitle: {meeting.PrimaryTitle}");
        sb.AppendLine($"Pass: {passName}");
        sb.AppendLine($"Started: {passStarted:o}");
        sb.AppendLine($"AudioPath: {meeting.AudioPath}");
        sb.AppendLine($"AudioExists: {File.Exists(meeting.AudioPath)}");

        if (File.Exists(meeting.AudioPath))
        {
            try
            {
                sb.AppendLine(
                    $"AudioBytes: {new FileInfo(meeting.AudioPath).Length}");
            }
            catch { }
        }

        sb.AppendLine($"BuzzExe: {_settings.BuzzExe}");
        sb.AppendLine($"OutputDirectory: {outputDirectory}");
        sb.AppendLine($"Arguments: {ProcessService.FormatArguments(args)}");
        sb.AppendLine();

        if (run is not null)
        {
            sb.AppendLine($"ExitCode: {run.ExitCode}");
            sb.AppendLine($"Ended: {run.Ended:o}");
            sb.AppendLine($"DurationSeconds: {run.Duration.TotalSeconds:F3}");
            sb.AppendLine($"WorkingDirectory: {run.WorkingDirectory}");
            sb.AppendLine();
            sb.AppendLine("----- STDOUT -----");
            sb.AppendLine(string.IsNullOrWhiteSpace(run.StandardOutput)
                ? "<empty>"
                : run.StandardOutput.TrimEnd());
            sb.AppendLine();
            sb.AppendLine("----- STDERR -----");
            sb.AppendLine(string.IsNullOrWhiteSpace(run.StandardError)
                ? "<empty>"
                : run.StandardError.TrimEnd());
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("ExitCode: <process did not complete normally>");
            sb.AppendLine();
        }

        sb.AppendLine("----- DISCOVERED OUTPUT FILES -----");

        if (discovered.Count == 0)
        {
            sb.AppendLine("<none>");
        }
        else
        {
            foreach (var file in discovered)
            {
                try
                {
                    var info = new FileInfo(file);
                    sb.AppendLine(
                        $"{file} | {info.Length} bytes | {info.LastWriteTimeUtc:o}");
                }
                catch
                {
                    sb.AppendLine(file);
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("----- RESULT -----");
        sb.AppendLine(note);

        if (exception is not null)
        {
            sb.AppendLine();
            sb.AppendLine("----- EXCEPTION -----");
            sb.AppendLine(exception.ToString());
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(diagnosticPath)!);

        await File.WriteAllTextAsync(
            diagnosticPath,
            sb.ToString(),
            new UTF8Encoding(false),
            ct);
    }


    private static bool LooksLikePromptLeak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim();
        var banned = new[]
        {
            "do not translate", "english language in english", "arabic speech must stay arabic",
            "english speech must stay english", "preserve the language that was actually spoken",
            "لا تترجم الكلام", "اكتب الكلام العربي بحروف عربية"
        };
        if (banned.Any(normalized.Contains)) return true;
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 8) return false;
        // repeated short instruction/hallucination loops are also rejected.
        var window = string.Join(' ', words.Take(Math.Min(8, words.Length)));
        return normalized.IndexOf(window, window.Length, StringComparison.Ordinal) >= 0;
    }

    private static bool ContainsArabic(string text) =>
        Regex.IsMatch(text, @"[\u0600-\u06FF]");

    private void FailMeeting(MeetingRecord meeting, string status)
    {
        meeting.TranscriptionStatus = status;
        _repository.Upsert(meeting);
        MeetingMetadataService.Write(meeting);
    }

    private static void TryResetDirectory(string path)
    {
        TryDeleteDirectory(path);
        Directory.CreateDirectory(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private sealed record PassResult(bool Success, string Text, string SrtPath)
    {
        public static readonly PassResult Failed = new(false, "", "");
    }
}
