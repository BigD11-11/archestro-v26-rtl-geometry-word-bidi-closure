namespace Archestro.MeetingVault.Models;

public sealed class AppSettings
{
    public string DisplayName { get; set; } = Environment.UserName;
    public string Organization { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public string GreetingName { get; set; } = "";
    // Canonical persisted value is the requested choice: System | Dark | Light.
    public string Appearance { get; set; } = "System";
    public string RequestedTheme
    {
        get => Appearance;
        set => Appearance = string.IsNullOrWhiteSpace(value) ? "System" : value;
    }
    public string PreferredLanguage { get; set; } = "English";
    public string RecordingSourceMode { get; set; } = "Microphone + System Audio";
    public bool SpeakerIntelligenceEnabled { get; set; } = true;
    public float SpeakerClusteringThreshold { get; set; } = 0.50f;
    public float SpeakerRecognitionThreshold { get; set; } = 0.82f;
    public int SpeakerMaxThreads { get; set; } = 4;
    public bool IntelligenceEnabled { get; set; } = true;
    public string IntelligenceModel { get; set; } = "Qwen3-4B-Q4_K_M";
    public string IntelligenceProvider { get; set; } = "Local";
    public string CloudIntelligenceModel { get; set; } = "";
    public string EncryptedIntelligenceApiKey { get; set; } = "";
    public bool CloudIntelligenceEnabled { get; set; }
    public bool CloudIntelligenceConsentAccepted { get; set; }
    public bool CloudLocalFallbackEnabled { get; set; } = true;
    // V30 processing controls. Offline remains the safe customer default.
    public string ProcessingMode { get; set; } = "Offline";
    public string TranscriptionProvider { get; set; } = "Local";
    public string EncryptedGroqApiKey { get; set; } = "";
    public string InboxPath { get; set; } = "";
    public string NewAudioPolicy { get; set; } = "Import + Transcribe";
    public bool MoveInboxFilesToProcessed { get; set; }
    public int AiMaxThreads { get; set; } = 6;
    public int AiContextTokens { get; set; } = 8192;
    public string DefaultMeetingMode { get; set; } = "General";
    // Legacy fields retained so existing settings.json files remain compatible.
    // R9.4 native audio recording does not launch or require OBS.
    public string ObsExe { get; set; } = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
    public int ObsPort { get; set; } = 4455;
    public string ObsPassword { get; set; } = "";
    public string BuzzExe { get; set; } = "";
    public string FfmpegExe { get; set; } = "";
    public string BuzzModelType { get; set; } = "whisper";
    public string BuzzModelSize { get; set; } = "large-v3";

    // R9.5 production transcription path.
    // "direct-whisper" bypasses Buzz.exe/PyInstaller completely and calls
    // OpenAI Whisper from a private local Python runtime.
    public string TranscriptionEngine { get; set; } = "native-whisper";
    public string DirectWhisperPythonExe { get; set; } = "";
    public string DirectWhisperWorkerPath { get; set; } = "";
    public string DirectWhisperModelPath { get; set; } = "";

    // R9.5.5 optimized multilingual engine.
    public string FasterWhisperWorkerPath { get; set; } = "";
    public string FasterWhisperModelPath { get; set; } = "";
    public string FasterWhisperComputeType { get; set; } = "int8_float32";
    public int FasterWhisperBatchSize { get; set; } = 2;

    // R9.5.7: multilingual language detection is performed per inference chunk.
    // Short chunks allow Arabic -> English -> Arabic code-switching to be
    // recognized without a second whole-file pass.
    public int FasterWhisperMixedChunkSeconds { get; set; } = 8;

    public string TranscriptionLanguageMode { get; set; } = "Arabic + English";
    public bool ShowRecentRecordings { get; set; } = true;
    // QUALITY-FIRST DEFAULT:
    // Keep the proven large-v3 model and allow bilingual recovery automatically,
    // but only inside the quiet background scheduler.
    public bool AutomaticMixedLanguageRecovery { get; set; } = true;
    // V13.8: first transcript readiness is prioritized. Optional quality repair follows later/on demand.
    public bool FirstTranscriptFastPathEnabled { get; set; } = true;
    public bool DeferBilingualRepairUntilAfterFirstReady { get; set; } = true;
    public bool DeferSpeakerAnalysisUntilAfterFirstReady { get; set; } = true;


    // Very short accidental recordings should not load Whisper automatically.
    public int AutoTranscribeMinimumSeconds { get; set; } = 8;

    // R9.5.4: tiny clips should not automatically trigger a second whole-file
    // recovery pass. Full meetings still get bounded automatic bilingual recovery.
    public int AutomaticBilingualRecoveryMinimumSeconds { get; set; } = 20;

    // Keep automatic quality recovery bounded for long meetings. A long recording should
    // not silently double its total processing time by running a second whole-file model pass.
    // Manual Improve/repair may still run explicitly when the owner asks for it.
    public int AutomaticWholeFileRecoveryMaxSeconds { get; set; } = 300;

    // HIGH-QUALITY QUIET BACKGROUND MODE.
    // Transcription is intentionally delayed until the computer has been idle.
    // The model quality stays unchanged; only scheduling/resource usage is constrained.
    public bool BackgroundTranscriptionQuietMode { get; set; } = true;

    // Legacy R9.4.9 field retained for settings.json compatibility.
    // R9.4.10 no longer requires mouse/keyboard inactivity.
    public int BackgroundTranscriptionIdleSeconds { get; set; } = 120;

    // SMART BACKGROUND:
    // Wait a short fixed grace period after saving, then start when system load is reasonable.
    // User mouse/keyboard activity does NOT reset this timer.
    public int BackgroundTranscriptionStartDelaySeconds { get; set; } = 45;
    // Fast transcript-first path: begin quickly after save while preserving CPU/memory guards.
    public int FirstTranscriptFastStartDelaySeconds { get; set; } = 5;
    public int BackgroundTranscriptionMaxSystemCpuPercent { get; set; } = 65;

    // ADAPTIVE RESOURCE GOVERNOR.
    // large-v3 remains unchanged. Only its CPU scheduling changes.
    // While the user is actively using the PC, Whisper gets a very small share.
    // When the user has not touched mouse/keyboard for a while, it may use more
    // CPU to finish sooner. A live recording always has absolute priority.
    public int BackgroundTranscriptionActiveUserCpuCapPercent { get; set; } = 20;
    public int BackgroundTranscriptionIdleCpuCapPercent { get; set; } = 70;
    public int BackgroundTranscriptionAccelerateAfterNoInputSeconds { get; set; } = 30;

    // Legacy setting retained for settings.json compatibility.
    public int BackgroundTranscriptionCpuCapPercent { get; set; } = 20;

    // Keep enough worker capacity for faster idle processing; the Job Object
    // hard cap prevents this from taking over the PC while the user is active.
    public int BackgroundTranscriptionMaxCpuThreads { get; set; } = 6;
    public int BackgroundTranscriptionMinimumFreeMemoryMb { get; set; } = 2048;
    // Manual "Transcribe Now" uses a lower but still bounded memory floor so an explicit
    // owner action does not remain queued forever on a moderately loaded workstation.
    public int ManualTranscriptionMinimumFreeMemoryMb { get; set; } = 2048;
    public bool ResumeQueuedTranscriptsOnStartup { get; set; } = true;
}
