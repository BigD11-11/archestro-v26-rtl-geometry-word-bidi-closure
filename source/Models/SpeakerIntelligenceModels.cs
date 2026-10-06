using System.Text.Json.Serialization;

namespace Archestro.MeetingVault.Models;

public sealed class SpeakerSegment
{
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public int SpeakerIndex { get; set; }
    public string ClusterId => $"Speaker {SpeakerIndex + 1}";
}

public sealed class SpeakerTranscriptLine
{
    public int Index { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public int SpeakerIndex { get; set; }
    public string SpeakerName { get; set; } = "";
    public string Text { get; set; } = "";
    public string SpeakerColor { get; set; } = "#3B82F6";
    public string TimeText => TimeSpan.FromSeconds(Math.Max(0, StartSeconds)).ToString(@"hh\:mm\:ss");
}

public sealed class SpeakerAnalysisResult
{
    public string MeetingId { get; set; } = "";
    public DateTimeOffset GeneratedLocal { get; set; } = DateTimeOffset.Now;
    public string Engine { get; set; } = "sherpa-onnx";
    public string EngineVersion { get; set; } = "1.13.5";
    public int DetectedSpeakerCount { get; set; }
    public List<SpeakerSegment> Segments { get; set; } = new();
    public List<SpeakerTranscriptLine> Lines { get; set; } = new();
    public Dictionary<int, string> SpeakerNames { get; set; } = new();
    public Dictionary<int, string> SpeakerColors { get; set; } = new();

    // Voice recognition evidence. These dictionaries are intentionally persisted with
    // the speaker analysis so a future People view can explain why a person was linked.
    public Dictionary<int, string> SpeakerMatchCandidates { get; set; } = new();
    public Dictionary<int, float> SpeakerMatchScores { get; set; } = new();
    public Dictionary<int, string> SpeakerMatchKinds { get; set; } = new();

    public string DisplayNameFor(int speakerIndex) =>
        SpeakerNames.TryGetValue(speakerIndex, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : $"Speaker {speakerIndex + 1}";

    public string ColorFor(int speakerIndex) =>
        SpeakerColors.TryGetValue(speakerIndex, out var color) && !string.IsNullOrWhiteSpace(color)
            ? color
            : SpeakerPalette.ColorFor(speakerIndex);
}

public sealed class SpeakerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public DateTimeOffset CreatedLocal { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedLocal { get; set; } = DateTimeOffset.Now;

    // User-confirmed source meetings are kept separately from automatic matches.
    // This avoids presenting model similarity as if it were human confirmation.
    public List<string> ConfirmedMeetingIds { get; set; } = new();
    public List<string> ConfirmedSpeakerRefs { get; set; } = new();
    public int SampleCount { get; set; } = 1;
    public string PhotoPath { get; set; } = "";
    public List<string> ImportedVoiceSamplePaths { get; set; } = new();
}

public sealed class SpeakerProfileMatch
{
    public string ProfileId { get; set; } = "";
    public string Name { get; set; } = "";
    public float Score { get; set; }
}

public static class SpeakerPalette
{
    private static readonly string[] Colors =
    {
        "#3B82F6", "#C69A52", "#22C7D9", "#8B5CF6",
        "#27C281", "#EC4899", "#F59E0B", "#14B8A6"
    };

    public static string ColorFor(int index) => Colors[Math.Abs(index) % Colors.Length];
}
