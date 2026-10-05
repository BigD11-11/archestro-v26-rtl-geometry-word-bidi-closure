namespace Archestro.MeetingVault.Models;

public sealed class EvidenceRef
{
    public string Id { get; set; } = "";
    public string MeetingId { get; set; } = "";
    public string MeetingTitle { get; set; } = "";
    public DateTimeOffset MeetingStartLocal { get; set; }
    public string MeetingCategory { get; set; } = "";
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public string Speaker { get; set; } = "";
    public string SpeakerMatchLabel { get; set; } = "";
    public string Text { get; set; } = "";
    public string TimeText => TimeSpan.FromSeconds(Math.Max(0, StartSeconds)).ToString(@"hh\:mm\:ss");
    public string MeetingDateText => MeetingStartLocal == default
        ? ""
        : MeetingStartLocal.ToString("dd MMM yyyy • hh:mm tt");
}

public sealed class IntelligenceItem
{
    public string Text { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Due { get; set; } = "";
    public string Severity { get; set; } = "";
    public List<string> Evidence { get; set; } = new();
}

public sealed class MeetingIntelligenceReport
{
    public string MeetingId { get; set; } = "";
    public string MeetingMode { get; set; } = "General";
    public DateTimeOffset GeneratedLocal { get; set; } = DateTimeOffset.Now;
    public string Model { get; set; } = "Qwen3-4B-Q4_K_M";
    public string ExecutiveSummary { get; set; } = "";
    public List<IntelligenceItem> KeyPoints { get; set; } = new();
    public List<IntelligenceItem> Decisions { get; set; } = new();
    public List<IntelligenceItem> ActionItems { get; set; } = new();
    public List<IntelligenceItem> Commitments { get; set; } = new();
    public List<IntelligenceItem> Deadlines { get; set; } = new();
    public List<IntelligenceItem> Risks { get; set; } = new();
    public List<IntelligenceItem> OpenItems { get; set; } = new();
    public List<IntelligenceItem> CommercialPoints { get; set; } = new();
    public List<IntelligenceItem> Topics { get; set; } = new();
    public List<IntelligenceItem> ImportantMoments { get; set; } = new();
    public List<IntelligenceItem> ParticipantContributions { get; set; } = new();
    public List<IntelligenceItem> FollowUp { get; set; } = new();
    public string ReportVersion { get; set; } = "2.0";
    public string ReportLanguage { get; set; } = "en";
    public string MeetingTitle { get; set; } = "";
    public DateTimeOffset MeetingStartLocal { get; set; }
    public string SourceTranscriptSha256 { get; set; } = "";
    public bool NeedsRefresh { get; set; }
    public List<EvidenceRef> EvidenceIndex { get; set; } = new();
}

public sealed class MeetingReportProgress
{
    public int Percent { get; set; }
    public string Stage { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed record EvidenceSufficiency(bool Sufficient, int WordCount, int UsefulCharacterCount, string Reason);

public sealed class InsufficientMeetingEvidenceException : InvalidOperationException
{
    public string Language { get; }
    public int WordCount { get; }

    public InsufficientMeetingEvidenceException(string language, int wordCount, string? message = null)
        : base(message ?? (language == "ar"
            ? "الأدلة المتاحة قصيرة أو محدودة. أضف تفاصيل أكثر إلى النص قبل إنشاء تقرير موثوق. لم يتم حفظ تقرير جاهز."
            : "The available transcript is too short or sparse for a reliable report. Add more transcript evidence before retrying. No ready report was saved."))
    {
        Language = language;
        WordCount = wordCount;
    }
}

public sealed class ReportLanguageValidationException : InvalidOperationException
{
    public ReportLanguageValidationException(string language)
        : base(language == "ar"
            ? "لم يجتز التقرير فحص اللغة بعد محاولة إصلاح واحدة. لم يُحفظ تقرير جديد؛ أعد المحاولة أو تحقق من نص الاجتماع."
            : "The report did not pass language validation after one repair attempt. No new report was saved; retry or review the transcript.") { }
}

public sealed class AskAnswer
{
    public string Answer { get; set; } = "";
    public List<string> Evidence { get; set; } = new();
    public List<EvidenceRef> EvidenceIndex { get; set; } = new();
}

public static class MeetingModes
{
    public static readonly string[] All =
    {
        "General", "Executive", "Contracts", "Procurement",
        "Sales", "Project", "HR", "Custom"
    };
}
