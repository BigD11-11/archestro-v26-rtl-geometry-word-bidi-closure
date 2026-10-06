using System.Text;
using System.Text.RegularExpressions;

namespace Archestro.MeetingVault.Services;

public static class BilingualTranscriptFusionService
{
    private static readonly HashSet<string> CommonEnglish = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","about","after","again","all","and","are","as","at","be","because","before","but","by",
        "can","client","company","contract","day","do","for","from","good","have","he","hello","here",
        "how","i","if","in","is","it","meeting","need","new","no","not","of","on","one","or","our",
        "please","project","record","recording","review","right","so","start","stop","system","test",
        "that","the","their","there","they","this","time","to","today","we","what","when","will",
        "with","work","working","yes","you","your",
        "action","agenda","approval","budget","business","call","commercial","customer",
        "desktop","email","follow","invoice","item","laptop","logistics","manager","market",
        "marketing","paper","payment","plan","price","proposal","quote","sales","schedule",
        "status","supplier","team","transport","update","vendor",
        "air","conditioner","conditioning","temperature","computer","software","hardware","server",
        "online","offline","meeting","intelligence","search","transcript","audio","system"
    };

    private static readonly HashSet<string> StrongStandaloneEnglish = new(StringComparer.OrdinalIgnoreCase)
    {
        "phone","mobile","computer","laptop","email","server","software","hardware","online","offline",
        "project","contract","meeting","system","client","supplier","vendor","marketing","sales","invoice",
        "budget","payment","temperature","conditioner","air"
    };

    private static readonly IReadOnlyDictionary<string, string[]> EmbeddedEnglishArabicForms =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["phone"] = new[] { "فون", "فُون" },
            ["mobile"] = new[] { "موبايل", "موبيل" },
            ["computer"] = new[] { "كمبيوتر", "كومبيوتر" },
            ["laptop"] = new[] { "لابتوب", "لاب توب" },
            ["email"] = new[] { "ايميل", "إيميل" },
            ["server"] = new[] { "سيرفر" },
            ["software"] = new[] { "سوفتوير", "سوفت وير" },
            ["hardware"] = new[] { "هاردوير", "هارد وير" },
            ["project"] = new[] { "بروجكت", "بروجيكت" },
            ["contract"] = new[] { "كونتراكت", "كنتركت" },
            ["meeting"] = new[] { "ميتنق", "ميتينق", "ميتينج" },
            ["system"] = new[] { "سيستم" },
            ["client"] = new[] { "كلاينت" },
            ["supplier"] = new[] { "سبلاير" },
            ["vendor"] = new[] { "فندر", "فيندر" },
            ["marketing"] = new[] { "ماركتنق", "ماركتنج" },
            ["sales"] = new[] { "سيلز" }
        };

    private static readonly string[] RomanizedArabicHints =
    {
        "ana","enta","inta","ant","hatha","hada","hadi","hay","eh","esh","shlon","shno","shu",
        "yalla","khalas","tamam","mafi","mashi","wallah","inshallah","mashallah","habibi","habibti",
        "salam","salaam","assalamu","assalam","alaikum","alaykum","warahmatullahi","rahmatullahi","wabarakatuh","barakatuh","shukran","shokr","aywa","aewa","naam","laa","wesh","wain","wein",
        "wahne","daraada","alu","marhaba","shabab","akeed","sah","kwayes","zain"
    };

    public static bool ShouldTryArabicRecovery(string baselineText, string? baselineSrtPath)
    {
        if (string.IsNullOrWhiteSpace(baselineText))
            return true;

        // Strong romanized-Arabic evidence must be repaired even when the same cue also
        // contains genuine English. This is the common code-switch case:
        // "Assalamu alaikum ... How are you?".
        if (HasStrongRomanizedArabicEvidence(baselineText))
            return true;

        // If Auto already produced Arabic script and recognizable English, protect it.
        if (ContainsArabic(baselineText) && HasStrongEnglishEvidence(baselineText))
            return false;

        var cues = TranscriptContextService.ParseSrt(baselineSrtPath);
        if (cues.Count > 0)
            return cues.Any(c => LooksLikeRomanizedArabicSegment(c.Text));

        return LooksLikeRomanizedArabicSegment(baselineText);
    }

    public static FusionResult Fuse(
        string baselineText,
        string? baselineSrtPath,
        string arabicRetryText,
        string? arabicRetrySrtPath,
        string outputSrtPath)
    {
        var baseline = TranscriptContextService.ParseSrt(baselineSrtPath);
        var arabic = TranscriptContextService.ParseSrt(arabicRetrySrtPath);

        if (baseline.Count == 0 || arabic.Count == 0)
            return new FusionResult(baselineText, baselineSrtPath ?? "", 0);

        var fused = new List<SubtitleCue>();
        var replaced = 0;

        foreach (var baseCue in baseline)
        {
            var selectedText = baseCue.Text;

            // Genuine English is protected, but a cue with strong romanized-Arabic
            // evidence is still eligible for the Arabic recovery pass. The Arabic retry
            // itself is instructed to preserve genuine English words in Latin script.
            if (LooksLikeRomanizedArabicSegment(baseCue.Text) &&
                (HasStrongRomanizedArabicEvidence(baseCue.Text) ||
                 !HasStrongEnglishEvidence(baseCue.Text)))
            {
                var candidate = FindBestOverlappingCue(baseCue, arabic);

                if (candidate is not null &&
                    ContainsArabic(candidate.Text) &&
                    IsUsefulArabicReplacement(baseCue.Text, candidate.Text))
                {
                    selectedText = candidate.Text;
                    replaced++;
                }
            }

            fused.Add(new SubtitleCue(
                baseCue.StartSeconds,
                baseCue.EndSeconds,
                selectedText));
        }

        WriteSrt(fused, outputSrtPath);

        var mergedText = string.Join(
            Environment.NewLine,
            fused.Select(c => c.Text.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)));

        return new FusionResult(mergedText, outputSrtPath, replaced);
    }

    public static bool HasStrongEnglishEvidence(string text)
    {
        var tokens = Regex.Matches(text.ToLowerInvariant(), @"[a-z']+")
            .Select(m => m.Value)
            .Where(t => t.Length > 1)
            .ToList();

        if (tokens.Count == 0)
            return false;

        var hits = tokens.Count(t => CommonEnglish.Contains(t));

        // One or two unmistakable common words in a short subtitle should protect it.
        if (tokens.Count <= 5)
            return hits >= 2;

        return hits / (double)tokens.Count >= 0.42;
    }

    public static double EnglishEvidenceRatio(string text)
    {
        var tokens = Regex.Matches((text ?? "").ToLowerInvariant(), @"[a-z']+")
            .Select(m => m.Value)
            .Where(t => t.Length > 1)
            .ToList();

        if (tokens.Count == 0)
            return 0;

        var hits = tokens.Count(t => CommonEnglish.Contains(t));
        return hits / (double)tokens.Count;
    }

    public static bool HasMeaningfulEnglishCoverage(string text)
    {
        var tokens = Regex.Matches((text ?? "").ToLowerInvariant(), @"[a-z']+")
            .Select(m => m.Value)
            .Where(t => t.Length > 1)
            .ToList();

        if (tokens.Count < 3)
            return false;

        var hits = tokens.Count(t => CommonEnglish.Contains(t));
        var ratio = hits / (double)tokens.Count;

        // This is intentionally stricter than segment protection.
        // A couple of surviving English nouns do not prove that an English
        // sentence was preserved correctly in a bilingual meeting.
        return hits >= 3 && ratio >= 0.45;
    }

    public static FusionResult FuseEnglishRecovery(
        string baselineText,
        string? baselineSrtPath,
        string englishRetryText,
        string? englishRetrySrtPath,
        string outputSrtPath)
    {
        var baseline = TranscriptContextService.ParseSrt(baselineSrtPath);
        var english = TranscriptContextService.ParseSrt(englishRetrySrtPath);

        if (baseline.Count == 0 || english.Count == 0)
            return new FusionResult(baselineText, baselineSrtPath ?? "", 0);

        var fused = new List<SubtitleCue>();
        var replaced = 0;

        foreach (var baseCue in baseline)
        {
            var selectedText = baseCue.Text;
            var candidate = FindBestOverlappingCue(baseCue, english);

            if (candidate is not null)
            {
                var restoredInline = RestoreEmbeddedEnglishTerms(baseCue.Text, candidate.Text);
                if (!string.Equals(restoredInline, baseCue.Text, StringComparison.Ordinal))
                {
                    selectedText = restoredInline;
                    replaced++;
                }
                else if (IsConfidentEnglishReplacement(candidate.Text) &&
                         !HasStrongEnglishEvidence(baseCue.Text) &&
                         !ContainsArabic(baseCue.Text))
                {
                    selectedText = candidate.Text;
                    replaced++;
                }
            }

            fused.Add(new SubtitleCue(
                baseCue.StartSeconds,
                baseCue.EndSeconds,
                selectedText));
        }

        WriteSrt(fused, outputSrtPath);

        var mergedText = string.Join(
            Environment.NewLine,
            fused.Select(c => c.Text.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        return new FusionResult(
            mergedText,
            outputSrtPath,
            replaced);
    }

    private static string RestoreEmbeddedEnglishTerms(string baseline, string englishCandidate)
    {
        if (string.IsNullOrWhiteSpace(baseline) || string.IsNullOrWhiteSpace(englishCandidate) || !ContainsArabic(baseline))
            return baseline;

        var result = baseline;
        var candidateTokens = Regex.Matches(englishCandidate.ToLowerInvariant(), @"[a-z']+")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var token in candidateTokens)
        {
            if (!StrongStandaloneEnglish.Contains(token) || !EmbeddedEnglishArabicForms.TryGetValue(token, out var forms))
                continue;
            foreach (var form in forms)
            {
                if (result.Contains(form, StringComparison.OrdinalIgnoreCase))
                    result = result.Replace(form, token, StringComparison.OrdinalIgnoreCase);
            }
        }

        return result;
    }

    private static bool IsConfidentEnglishReplacement(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || ContainsArabic(text))
            return false;

        if (LooksLikeRomanizedArabicSegment(text))
            return false;

        var tokens = Regex.Matches(text.ToLowerInvariant(), @"[a-z']+")
            .Select(m => m.Value)
            .Where(t => t.Length > 1)
            .ToList();

        if (tokens.Count == 1)
            return StrongStandaloneEnglish.Contains(tokens[0]);
        if (tokens.Count < 2)
            return false;

        var hits = tokens.Count(t => CommonEnglish.Contains(t));
        var ratio = hits / (double)tokens.Count;

        // A short two-word technical/business term (for example "air conditioner")
        // is valid code-switching evidence and should not be forced into Arabic script.
        if (tokens.Count <= 3)
            return hits >= 2 && ratio >= 0.66 && !IsHighlyRepetitive(text);

        return hits >= 3 && ratio >= 0.60 && !IsHighlyRepetitive(text);
    }

    public static bool HasStrongRomanizedArabicEvidence(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || ContainsArabic(text))
            return false;

        var tokens = Regex.Matches(text.ToLowerInvariant(), @"[a-z']+")
            .Select(m => m.Value)
            .Where(t => t.Length > 1)
            .ToList();

        if (tokens.Count < 2)
            return false;

        var hintHits = tokens.Count(t =>
            RomanizedArabicHints.Contains(t, StringComparer.OrdinalIgnoreCase));

        // Two or more Arabic transliteration anchors are deliberate evidence,
        // even if an English sentence follows in the same timed cue.
        return hintHits >= 2;
    }

    public static bool LooksLikeRomanizedArabicSegment(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || ContainsArabic(text))
            return false;

        var tokens = Regex.Matches(text.ToLowerInvariant(), @"[a-z']+")
            .Select(m => m.Value)
            .Where(t => t.Length > 1)
            .ToList();

        if (tokens.Count < 2)
            return false;

        var hintHits = tokens.Count(t =>
            RomanizedArabicHints.Contains(t, StringComparer.OrdinalIgnoreCase));

        if (hintHits >= 2)
            return true;

        if (hintHits > 0 && !HasStrongEnglishEvidence(text))
            return true;

        var englishHits = tokens.Count(t => CommonEnglish.Contains(t));
        var englishRatio = englishHits / (double)Math.Max(1, tokens.Count);

        return tokens.Count >= 4 && englishRatio < 0.30;
    }

    private static bool IsUsefulArabicReplacement(string baseline, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !ContainsArabic(candidate))
            return false;

        // Avoid replacing a meaningful long baseline by a tiny hallucinated phrase.
        var baseLen = VisibleLength(baseline);
        var candidateLen = VisibleLength(candidate);

        if (baseLen >= 12 && candidateLen < Math.Max(6, baseLen * 0.35))
            return false;

        // Repetitive candidate text is suspicious.
        if (IsHighlyRepetitive(candidate))
            return false;

        return true;
    }

    private static SubtitleCue? FindBestOverlappingCue(
        SubtitleCue baseline,
        IReadOnlyList<SubtitleCue> candidates)
    {
        return candidates
            .Select(c => new
            {
                Cue = c,
                Overlap = Math.Max(
                    0,
                    Math.Min(baseline.EndSeconds, c.EndSeconds) -
                    Math.Max(baseline.StartSeconds, c.StartSeconds)),
                MidDistance = Math.Abs(
                    ((baseline.StartSeconds + baseline.EndSeconds) / 2.0) -
                    ((c.StartSeconds + c.EndSeconds) / 2.0))
            })
            .Where(x => x.Overlap > 0 || x.MidDistance <= 1.5)
            .OrderByDescending(x => x.Overlap)
            .ThenBy(x => x.MidDistance)
            .Select(x => x.Cue)
            .FirstOrDefault();
    }

    private static int VisibleLength(string text) =>
        Regex.Replace(text ?? "", @"\s+", "").Length;

    private static bool IsHighlyRepetitive(string text)
    {
        var tokens = Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(m => m.Value)
            .ToList();

        if (tokens.Count < 5)
            return false;

        var unique = tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return unique / (double)tokens.Count < 0.42;
    }

    private static bool ContainsArabic(string text) =>
        Regex.IsMatch(text, @"[\u0600-\u06FF]");

    private static void WriteSrt(IReadOnlyList<SubtitleCue> cues, string path)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < cues.Count; i++)
        {
            sb.AppendLine((i + 1).ToString());
            sb.AppendLine(
                $"{FormatSrt(cues[i].StartSeconds)} --> {FormatSrt(cues[i].EndSeconds)}");
            sb.AppendLine(cues[i].Text.Trim());
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string FormatSrt(double seconds)
    {
        if (seconds < 0) seconds = 0;

        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }
}

public sealed record FusionResult(
    string Text,
    string SrtPath,
    int ReplacedSegments);
