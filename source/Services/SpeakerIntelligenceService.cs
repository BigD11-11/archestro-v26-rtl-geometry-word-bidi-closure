using Archestro.MeetingVault.Models;
using SherpaOnnx;

namespace Archestro.MeetingVault.Services;

public sealed class SpeakerIntelligenceService
{
    private readonly AppSettings _settings;
    private readonly SpeakerProfileStore _profiles = new();
    private readonly SpeakerTranscriptService _transcripts = new();

    public SpeakerIntelligenceService(AppSettings settings)
    {
        _settings = settings;
    }

    public string ModelRoot => SpeakerModelLocator.ResolveRoot();

    public float RecognitionThreshold =>
        Math.Clamp(Math.Max(_settings.SpeakerRecognitionThreshold, 0.82f), 0.82f, 0.95f);


    public async Task<SpeakerAnalysisResult> AnalyzeAsync(
        MeetingRecord meeting,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(meeting.SrtPath))
                throw new InvalidOperationException(
                    "Speaker Intelligence needs the timed transcript (.srt). Finish transcription first.");

            var sourceAudio = File.Exists(meeting.AudioPath)
                ? meeting.AudioPath
                : meeting.RecordingPath;

            if (!File.Exists(sourceAudio))
                throw new FileNotFoundException("Meeting audio was not found.");

            var tempWave = SpeakerAudioService.Create16kMonoWave(sourceAudio);
            try
            {
                var (segments, waveSamples, sampleRate) =
                    RunDiarization(tempWave, progress, cancellationToken);

                var result = new SpeakerAnalysisResult
                {
                    MeetingId = meeting.Id,
                    GeneratedLocal = DateTimeOffset.Now,
                    DetectedSpeakerCount = segments
                        .Select(x => x.SpeakerIndex)
                        .Distinct()
                        .Count(),
                    Segments = segments
                };

                foreach (var speakerIndex in segments.Select(x => x.SpeakerIndex).Distinct().OrderBy(x => x))
                {
                    result.SpeakerColors[speakerIndex] = SpeakerPalette.ColorFor(speakerIndex);

                    var embedding = ComputeClusterEmbedding(
                        speakerIndex, segments, waveSamples, sampleRate);

                    if (embedding.Length == 0) continue;

                    var autoThreshold = Math.Clamp(
                        Math.Max(_settings.SpeakerRecognitionThreshold, 0.88f),
                        0.88f,
                        0.95f);
                    const float possibleThreshold = 0.75f;
                    const float minimumMargin = 0.06f;
                    var matches = _profiles.FindTopMatches(embedding, 2);
                    var best = matches.FirstOrDefault();
                    var second = matches.Skip(1).FirstOrDefault();

                    if (best is not null)
                    {
                        result.SpeakerMatchCandidates[speakerIndex] = best.Name;
                        result.SpeakerMatchScores[speakerIndex] = best.Score;

                        var marginSafe = second is null || best.Score - second.Score >= minimumMargin;
                        if (best.Score >= autoThreshold && marginSafe)
                        {
                            result.SpeakerNames[speakerIndex] = best.Name;
                            result.SpeakerMatchKinds[speakerIndex] = "Auto match";
                        }
                        else if (best.Score >= possibleThreshold)
                        {
                            result.SpeakerMatchKinds[speakerIndex] = "Possible";
                        }
                    }
                }

                result.Lines = _transcripts.Align(meeting.SrtPath, segments, result);
                _transcripts.Save(meeting, result);
                return result;
            }
            finally
            {
                try { File.Delete(tempWave); } catch { }
            }
        }, cancellationToken);
    }

    public void RejectCandidate(
        MeetingRecord meeting,
        SpeakerAnalysisResult result,
        int speakerIndex)
    {
        var candidate = result.SpeakerMatchCandidates.TryGetValue(speakerIndex, out var c) ? c : null;
        result.SpeakerMatchCandidates.Remove(speakerIndex);
        result.SpeakerMatchScores.Remove(speakerIndex);
        result.SpeakerMatchKinds.Remove(speakerIndex);

        if (!string.IsNullOrWhiteSpace(candidate) &&
            result.SpeakerNames.TryGetValue(speakerIndex, out var assigned) &&
            assigned.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            result.SpeakerNames.Remove(speakerIndex);

        _transcripts.RefreshNames(result);
        _transcripts.Save(meeting, result);
    }

    public void RenameSpeaker(
        MeetingRecord meeting,
        SpeakerAnalysisResult result,
        int speakerIndex,
        string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        result.SpeakerNames[speakerIndex] = name.Trim();
        _transcripts.RefreshNames(result);
        _transcripts.Save(meeting, result);
    }

    public async Task RememberSpeakerAsync(
        MeetingRecord meeting,
        SpeakerAnalysisResult result,
        int speakerIndex,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Give this speaker a name before remembering the voice.");

        var sourceAudio = File.Exists(meeting.AudioPath)
            ? meeting.AudioPath
            : meeting.RecordingPath;

        var tempWave = SpeakerAudioService.Create16kMonoWave(sourceAudio);
        try
        {
            var reader = SpeakerWaveLoader.Read(tempWave);
            var embedding = ComputeClusterEmbedding(
                speakerIndex,
                result.Segments,
                reader.Samples,
                reader.SampleRate);

            if (embedding.Length == 0)
                throw new InvalidOperationException(
                    "There is not enough clean speech from this speaker to create a voice profile.");

            _profiles.Upsert(name.Trim(), embedding, meeting.Id, speakerIndex);

            result.SpeakerNames[speakerIndex] = name.Trim();
            result.SpeakerMatchCandidates[speakerIndex] = name.Trim();
            result.SpeakerMatchScores[speakerIndex] = 1.0f;
            result.SpeakerMatchKinds[speakerIndex] = "Confirmed";

            _transcripts.RefreshNames(result);
            _transcripts.Save(meeting, result);
        }
        finally
        {
            try { File.Delete(tempWave); } catch { }
        }

        await Task.CompletedTask;
    }

    public async Task RememberVoiceFileAsync(
        string profileId,
        string profileName,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(profileName))
            throw new InvalidOperationException("Choose a saved person before importing a voice sample.");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The selected voice file was not found.", sourcePath);

        Directory.CreateDirectory(AppPaths.SpeakerVoiceSamples);
        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".audio";
        var stored = Path.Combine(
            AppPaths.SpeakerVoiceSamples,
            $"{AppPaths.Sanitize(profileName)}_{profileId}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
        File.Copy(sourcePath, stored, false);

        var tempWave = SpeakerAudioService.Create16kMonoWave(stored);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reader = SpeakerWaveLoader.Read(tempWave);
            var embedding = ComputeImportedVoiceEmbedding(reader.Samples, reader.SampleRate);
            if (embedding.Length == 0)
                throw new InvalidOperationException("The imported recording does not contain enough usable speech.");

            _profiles.Upsert(profileName.Trim(), embedding);
            _profiles.RegisterImportedVoiceSample(profileId, stored);
        }
        catch
        {
            try { if (File.Exists(stored)) File.Delete(stored); } catch { }
            throw;
        }
        finally
        {
            try { File.Delete(tempWave); } catch { }
        }

        await Task.CompletedTask;
    }

    private float[] ComputeImportedVoiceEmbedding(float[] samples, int sampleRate)
    {
        var model = Path.Combine(
            ModelRoot,
            "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx");
        if (!File.Exists(model))
            throw new InvalidOperationException("Speaker embedding model is missing.");

        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = model,
            NumThreads = Math.Clamp(_settings.SpeakerMaxThreads, 1, 6),
            Provider = "cpu"
        };
        using var extractor = new SpeakerEmbeddingExtractor(config);
        var duration = samples.Length / (double)Math.Max(1, sampleRate);
        var starts = new[] { 0.0, Math.Max(0, duration * 0.33), Math.Max(0, duration * 0.66) };
        var vectors = new List<float[]>();
        foreach (var start in starts.Distinct())
        {
            var chunk = Slice(samples, sampleRate, start, Math.Min(duration, start + 12), maxSeconds: 12);
            if (chunk.Length < sampleRate) continue;
            using var stream = extractor.CreateStream();
            stream.AcceptWaveform(sampleRate, chunk);
            stream.InputFinished();
            if (!extractor.IsReady(stream)) continue;
            var vector = extractor.Compute(stream);
            if (vector.Length > 0) vectors.Add(vector);
        }
        return AverageNormalized(vectors);
    }

    public SpeakerAnalysisResult? Load(MeetingRecord meeting) => _transcripts.Load(meeting);

    private (List<SpeakerSegment> Segments, float[] Samples, int SampleRate) RunDiarization(
        string wavePath,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var root = ModelRoot;
        var segmentation = Path.Combine(root, "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
        var embedding = Path.Combine(root, "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx");

        if (!File.Exists(segmentation) || !File.Exists(embedding))
            throw new InvalidOperationException(
                "Speaker model pack is missing. Re-run the Build 2 preparation or install the Speaker Pack.");

        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentation;
        config.Embedding.Model = embedding;
        config.Embedding.NumThreads = Math.Clamp(_settings.SpeakerMaxThreads, 1, 6);
        config.Clustering.Threshold = Math.Clamp(_settings.SpeakerClusteringThreshold, 0.30f, 0.90f);

        using var diarizer = new OfflineSpeakerDiarization(config);
        var reader = SpeakerWaveLoader.Read(wavePath);

        if (reader.SampleRate != diarizer.SampleRate)
            throw new InvalidOperationException(
                $"Speaker engine expects {diarizer.SampleRate} Hz audio, but received {reader.SampleRate} Hz.");

        var callback = new OfflineSpeakerDiarizationProgressCallback(
            (processed, total, _) =>
            {
                if (cancellationToken.IsCancellationRequested) return 1;
                var percent = total <= 0 ? 0 : (int)Math.Round(100.0 * processed / total);
                progress?.Report(Math.Clamp(percent, 0, 100));
                return 0;
            });

        var raw = diarizer.ProcessWithCallback(reader.Samples, callback, IntPtr.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var segments = raw
            .Select(x => new SpeakerSegment
            {
                StartSeconds = x.Start,
                EndSeconds = x.End,
                SpeakerIndex = x.Speaker
            })
            .Where(x => x.EndSeconds > x.StartSeconds)
            .OrderBy(x => x.StartSeconds)
            .ToList();

        if (segments.Count == 0)
            throw new InvalidOperationException("No speakers were detected in this meeting.");

        return (segments, reader.Samples, reader.SampleRate);
    }

    private float[] ComputeClusterEmbedding(
        int speakerIndex,
        IReadOnlyList<SpeakerSegment> segments,
        float[] allSamples,
        int sampleRate)
    {
        var model = Path.Combine(
            ModelRoot,
            "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx");

        var candidates = segments
            .Where(x => x.SpeakerIndex == speakerIndex)
            .OrderByDescending(x => x.EndSeconds - x.StartSeconds)
            .Take(3)
            .ToList();

        var vectors = new List<float[]>();
        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = model,
            NumThreads = Math.Clamp(_settings.SpeakerMaxThreads, 1, 6),
            Provider = "cpu"
        };

        using var extractor = new SpeakerEmbeddingExtractor(config);

        foreach (var seg in candidates)
        {
            var samples = Slice(allSamples, sampleRate, seg.StartSeconds, seg.EndSeconds, maxSeconds: 15);
            if (samples.Length < sampleRate) continue;

            using var stream = extractor.CreateStream();
            stream.AcceptWaveform(sampleRate, samples);
            stream.InputFinished();
            if (!extractor.IsReady(stream)) continue;

            var v = extractor.Compute(stream);
            if (v.Length > 0) vectors.Add(v);
        }

        return AverageNormalized(vectors);
    }

    private static float[] Slice(
        float[] samples,
        int sampleRate,
        double startSeconds,
        double endSeconds,
        double maxSeconds)
    {
        var start = Math.Clamp((int)Math.Floor(startSeconds * sampleRate), 0, samples.Length);
        var requestedEnd = Math.Min(endSeconds, startSeconds + maxSeconds);
        var end = Math.Clamp((int)Math.Ceiling(requestedEnd * sampleRate), start, samples.Length);
        var count = end - start;
        if (count <= 0) return Array.Empty<float>();
        var output = new float[count];
        Array.Copy(samples, start, output, 0, count);
        return output;
    }

    private static float[] AverageNormalized(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0) return Array.Empty<float>();
        var dim = vectors[0].Length;
        if (vectors.Any(x => x.Length != dim)) return vectors[0];

        var result = new float[dim];
        for (var i = 0; i < dim; i++)
            result[i] = vectors.Average(x => x[i]);

        double norm = Math.Sqrt(result.Sum(x => x * x));
        if (norm > 0)
            for (var i = 0; i < result.Length; i++)
                result[i] = (float)(result[i] / norm);

        return result;
    }
}

public static class SpeakerModelLocator
{
    public static string ResolveRoot()
    {
        var env = Environment.GetEnvironmentVariable("ARCHESTRO_SPEAKER_MODEL_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var besideApp = Path.Combine(AppContext.BaseDirectory, "SpeakerModels");
        if (Directory.Exists(besideApp))
            return besideApp;

        return AppPaths.SpeakerModels;
    }
}
