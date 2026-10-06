using SherpaOnnx;

namespace Archestro.MeetingVault.Services;

public static class SpeakerSelfTestService
{
    public static void Run()
    {
        var root = SpeakerModelLocator.ResolveRoot();
        var segmentation = Path.Combine(root, "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
        var embedding = Path.Combine(root, "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx");
        var testWave = Environment.GetEnvironmentVariable("ARCHESTRO_SPEAKER_TEST_WAV");
        if (string.IsNullOrWhiteSpace(testWave))
            testWave = Path.Combine(root, "0-four-speakers-zh.wav");

        if (!File.Exists(segmentation)) throw new FileNotFoundException("Speaker segmentation model missing.", segmentation);
        if (!File.Exists(embedding)) throw new FileNotFoundException("Speaker embedding model missing.", embedding);
        if (!File.Exists(testWave)) throw new FileNotFoundException("Speaker test wave missing.", testWave);

        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentation;
        config.Embedding.Model = embedding;
        config.Embedding.NumThreads = 2;
        config.Clustering.NumClusters = 4;

        using var diarizer = new OfflineSpeakerDiarization(config);
        var wave = SpeakerWaveLoader.Read(testWave);
        if (wave.SampleRate != diarizer.SampleRate)
            throw new InvalidOperationException("Speaker self-test sample rate mismatch.");

        var segments = diarizer.Process(wave.Samples);
        var speakers = segments.Select(x => x.Speaker).Distinct().OrderBy(x => x).ToArray();

        if (segments.Length == 0)
            throw new InvalidOperationException("Speaker self-test returned no segments.");
        if (speakers.Length != 4)
            throw new InvalidOperationException(
                $"Speaker self-test expected 4 speakers but detected {speakers.Length}.");

        var extractorConfig = new SpeakerEmbeddingExtractorConfig
        {
            Model = embedding,
            NumThreads = 2,
            Provider = "cpu"
        };
        using var extractor = new SpeakerEmbeddingExtractor(extractorConfig);
        using var stream = extractor.CreateStream();

        var first = segments.OrderByDescending(x => x.End - x.Start).First();
        var start = Math.Clamp((int)(first.Start * wave.SampleRate), 0, wave.Samples.Length);
        var end = Math.Clamp((int)(first.End * wave.SampleRate), start, wave.Samples.Length);
        var count = Math.Min(end - start, wave.SampleRate * 10);
        var clip = new float[Math.Max(0, count)];
        if (clip.Length > 0)
            Array.Copy(wave.Samples, start, clip, 0, clip.Length);

        stream.AcceptWaveform(wave.SampleRate, clip);
        stream.InputFinished();
        if (!extractor.IsReady(stream))
            throw new InvalidOperationException("Speaker embedding extractor is not ready.");
        var vector = extractor.Compute(stream);
        if (vector.Length == 0 || vector.All(x => Math.Abs(x) < 1e-8))
            throw new InvalidOperationException("Speaker embedding self-test returned an empty vector.");

        File.WriteAllText(
            Path.Combine(AppPaths.Logs, "speaker-self-test-pass.txt"),
            $"PASS {DateTimeOffset.Now:o}{Environment.NewLine}" +
            $"sherpa-onnx speaker diarization PASS{Environment.NewLine}" +
            $"Detected speakers: {speakers.Length}{Environment.NewLine}" +
            $"Embedding dimensions: {vector.Length}");
    }
}
