using NAudio.Wave;

namespace Archestro.MeetingVault.Services;

public sealed class SpeakerWaveData
{
    public required int SampleRate { get; init; }
    public required float[] Samples { get; init; }
}

public static class SpeakerWaveLoader
{
    public static SpeakerWaveData Read(string wavePath)
    {
        if (!File.Exists(wavePath))
            throw new FileNotFoundException("Speaker wave file was not found.", wavePath);

        using var reader = new WaveFileReader(wavePath);
        var format = reader.WaveFormat;

        if (format.Channels != 1)
            throw new InvalidOperationException(
                $"Speaker engine requires mono audio. Received {format.Channels} channels.");

        var provider = reader.ToSampleProvider();
        var samples = new List<float>(Math.Max(16000, (int)(reader.Length / Math.Max(1, format.BlockAlign))));
        var buffer = new float[8192];

        while (true)
        {
            var count = provider.Read(buffer, 0, buffer.Length);
            if (count <= 0) break;

            for (var i = 0; i < count; i++)
                samples.Add(buffer[i]);
        }

        if (samples.Count == 0)
            throw new InvalidOperationException("Speaker wave file contained no audio samples.");

        return new SpeakerWaveData
        {
            SampleRate = format.SampleRate,
            Samples = samples.ToArray()
        };
    }
}
