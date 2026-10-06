using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using NAudio.Wave;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public sealed record GroqTranscriptSegment(double Start, double End, string Text);
public sealed record GroqTranscriptResult(string Text, IReadOnlyList<GroqTranscriptSegment> Segments, string? RequestId, long ElapsedMs, int RequestCount = 1);

public sealed class GroqTranscriptionService(HttpClient? client = null)
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(12) };
    private const string Endpoint = "https://api.groq.com/openai/v1/";
    public const string Model = "whisper-large-v3-turbo";
    public const decimal PriceUsdPerHour = 0.04m;

    public async Task<bool> TestConnectionAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + "models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await SendWithRetryAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw await ProviderException(response, ct);
        return true;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // One bounded retry for transient throttling or server failures. No retry on invalid credentials.
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode != 429 && (int)response.StatusCode < 500) return response;
        response.Dispose();
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        using var clone = await CloneRequestAsync(request, ct);
        return await _client.SendAsync(clone, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (original.Content is MultipartFormDataContent multipart)
        {
            var content = new MultipartFormDataContent(multipart.Headers.ContentType?.Parameters.FirstOrDefault(x => x.Name == "boundary")?.Value?.Trim('"') ?? "v30-boundary");
            foreach (var part in multipart)
            {
                var bytes = await part.ReadAsByteArrayAsync(ct);
                var copy = new ByteArrayContent(bytes);
                foreach (var h in part.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
                var disposition = part.Headers.ContentDisposition;
                var name = disposition?.Name?.Trim('"') ?? "field";
                var fileName = disposition?.FileName?.Trim('"');
                if (string.IsNullOrWhiteSpace(fileName)) content.Add(copy, name);
                else content.Add(copy, name, fileName);
            }
            clone.Content = content;
        }
        return clone;
    }

    public async Task<GroqTranscriptResult> TranscribeAsync(string path, string apiKey, CancellationToken ct = default, string? ffmpegPath = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Audio file unavailable.", path);
        if (info.Length > 100L * 1024 * 1024)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
                throw new InvalidOperationException("Audio exceeds Groq's 100 MB upload limit. The original was kept; Local transcription is available because the local audio splitter is missing.");
            return await TranscribeOversizedAsync(path, apiKey, ffmpegPath, ct);
        }

        var timer = Stopwatch.StartNew();
        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(path, ct);
        using var audio = new ByteArrayContent(bytes);
        audio.Headers.ContentType = new MediaTypeHeaderValue(ContentType(Path.GetExtension(path)));
        form.Add(audio, "file", Path.GetFileName(path) ?? "audio.m4a");
        form.Add(new StringContent(Model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");
        form.Add(new StringContent("0"), "temperature");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint + "audio/transcriptions") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await SendWithRetryAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw await ProviderException(response, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        var root = json.RootElement;
        var text = root.TryGetProperty("text", out var textNode) ? textNode.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Groq returned an empty transcript.");
        var segments = new List<GroqTranscriptSegment>();
        if (root.TryGetProperty("segments", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var segment in items.EnumerateArray())
            {
                var start = segment.TryGetProperty("start", out var s) ? s.GetDouble() : 0;
                var end = segment.TryGetProperty("end", out var e) ? e.GetDouble() : start;
                var value = segment.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(value)) segments.Add(new(start, end, value.Trim()));
            }
        string? requestId = null;
        if (root.TryGetProperty("x_groq", out var groq) && groq.TryGetProperty("id", out var id)) requestId = id.GetString();
        timer.Stop();
        return new(text, segments, requestId, timer.ElapsedMilliseconds);
    }

    private async Task<GroqTranscriptResult> TranscribeOversizedAsync(string path, string apiKey, string ffmpegPath, CancellationToken ct)
    {
        double duration;
        try { using var reader = new MediaFoundationReader(path); duration = reader.TotalTime.TotalSeconds; }
        catch { throw new InvalidOperationException("Audio duration could not be read safely. The original was kept; use Local transcription or a supported audio file."); }
        if (!double.IsFinite(duration) || duration < 1) throw new InvalidOperationException("Audio duration is invalid; the original was kept.");
        var workspace = Path.Combine(Path.GetTempPath(), "ArchestroGroqChunks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            // 45 minute mono 64 kbps AAC chunks stay well below the provider's 100 MB limit.
            var chunkPattern = Path.Combine(workspace, "part_%04d.m4a");
            var log = Path.Combine(workspace, "split.log");
            var exit = await ProcessService.RunAsync(ffmpegPath,
                new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", path, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "aac", "-b:a", "64k", "-f", "segment", "-segment_time", "2700", "-reset_timestamps", "1", "-segment_format", "mp4", chunkPattern }, log, ct);
            if (exit != 0) throw new InvalidOperationException("Audio chunking failed. The original remains available for Local transcription.");
            var chunks = Directory.EnumerateFiles(workspace, "part_*.m4a").OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (chunks.Length < 2 || chunks.Any(x => new FileInfo(x).Length > 100L * 1024 * 1024))
                throw new InvalidOperationException("Safe Groq chunk sizes could not be produced. The original remains available for Local transcription.");
            var allSegments = new List<GroqTranscriptSegment>(); var textParts = new List<string>();
            var offset = 0d; long elapsed = 0; var ids = new List<string>();
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                var result = await TranscribeAsync(chunk, apiKey, ct);
                textParts.Add(result.Text); elapsed += result.ElapsedMs;
                if (!string.IsNullOrWhiteSpace(result.RequestId)) ids.Add(result.RequestId);
                double chunkDuration;
                using (var reader = new MediaFoundationReader(chunk)) chunkDuration = reader.TotalTime.TotalSeconds;
                if (result.Segments.Count > 0)
                    allSegments.AddRange(result.Segments.Select(s => s with { Start = s.Start + offset, End = s.End + offset }));
                else allSegments.Add(new(offset, offset + chunkDuration, result.Text));
                offset += chunkDuration;
            }
            return new(string.Join(" ", textParts), allSegments, ids.Count == 0 ? null : string.Join(",", ids), elapsed, chunks.Length);
        }
        finally { try { Directory.Delete(workspace, true); } catch { } }
    }

    public static async Task RecordCostAsync(AiUsageLedgerService ledger, string meetingId, int durationSeconds,
        long elapsedMs, string? requestId, bool success, string? failureType = null, int requestCount = 1)
    {
        ledger.Record(new AiUsageRow(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, meetingId, null,
            "Groq", Model, "Transcription", requestId, null, null, null, null, null, null,
            elapsedMs, success, "groq-whisper-large-v3-turbo-2026-10-06-v1", null, null, "DURATION_PRICED",
            AudioDurationSeconds: durationSeconds, RequestCount: Math.Max(1, requestCount)), failureType);
        await Task.CompletedTask;
    }

    private static string ContentType(string extension) => extension.ToLowerInvariant() switch
    { ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".flac" => "audio/flac", ".ogg" => "audio/ogg", ".webm" => "audio/webm", _ => "audio/mp4" };

    private static async Task<InvalidOperationException> ProviderException(HttpResponseMessage response, CancellationToken ct)
    {
        var code = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        var type = response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "credential rejected" :
            response.StatusCode == (System.Net.HttpStatusCode)429 ? "rate limit" : "provider error";
        return new InvalidOperationException($"Groq {type} ({code}). Audio remains available for retry or Local transcription.");
    }
}
