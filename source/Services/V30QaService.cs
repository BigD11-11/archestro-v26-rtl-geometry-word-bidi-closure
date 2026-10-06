using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public static class V30QaService
{
    public static async Task RunAsync(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new InvalidOperationException("V30 QA output path is required.");
        var root = AppPaths.Root;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        Directory.CreateDirectory(Path.Combine(root, "System"));
        Directory.CreateDirectory(Path.Combine(root, "AI", "Models"));
        _ = new SettingsService().LoadOrDiscover();
        _ = new AiUsageLedgerService(); // additive 1.1.0 -> 1.2.0 migration on the isolated copy of the owner DB
        InboxQueueService.EnsureSchemaForOwnerDatabase();
        var defaults = new SettingsService().LoadOrDiscover();
        var testDir = Path.Combine(root, "V30SyntheticInbox"); Directory.CreateDirectory(testDir);
        var db = Path.Combine(root, "System", "v30-qa.db");
        var checks = new Dictionary<string, string>();
        if (!defaults.ProcessingMode.Equals("Offline", StringComparison.OrdinalIgnoreCase) ||
            !defaults.TranscriptionProvider.Equals("Local", StringComparison.OrdinalIgnoreCase) || defaults.CloudIntelligenceEnabled)
            throw new InvalidDataException("Clean settings are not Offline/Local by default.");
        checks["clean_settings_default"] = "PASS_OFFLINE_LOCAL_CLOUD_DISABLED";

        var audio = Path.Combine(root, "synthetic_ar_en_fixture.wav");
        await File.WriteAllBytesAsync(audio, Enumerable.Range(0, 1024).Select(i => (byte)(i % 251)).ToArray());
        var transcriptionAttempts = 0;
        var handler = new QaHttpHandler(async (request, ct) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/models", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"whisper-large-v3-turbo\"}]}", Encoding.UTF8, "application/json") };
            if (Interlocked.Increment(ref transcriptionAttempts) == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var body = await request.Content!.ReadAsStringAsync(ct);
            if (!body.Contains("whisper-large-v3-turbo", StringComparison.Ordinal) || !body.Contains("synthetic_ar_en_fixture.wav", StringComparison.Ordinal))
                throw new InvalidDataException("Groq multipart request was malformed.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"اجتماع افتراضي synthetic meeting\",\"segments\":[{\"start\":0.0,\"end\":1.2,\"text\":\"اجتماع افتراضي\"},{\"start\":1.2,\"end\":2.4,\"text\":\"synthetic meeting\"}],\"x_groq\":{\"id\":\"qa-request\"}}", Encoding.UTF8, "application/json")
            };
        });
        using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
        {
            var groq = new GroqTranscriptionService(client);
            await groq.TestConnectionAsync(Guid.NewGuid().ToString("N"));
            var result = await groq.TranscribeAsync(audio, Guid.NewGuid().ToString("N"));
            if (!result.Text.Contains("اجتماع") || !result.Text.Contains("synthetic", StringComparison.Ordinal) || result.Segments.Count != 2)
                throw new InvalidDataException("Groq Arabic/English transcript parse failed.");
            checks["groq_test_connection"] = "PASS_SYNTHETIC_HTTP";
            checks["groq_multipart_ar_en_segments_and_bounded_retry"] = "PASS_SYNTHETIC_HTTP";
        }
        using (var timeoutClient = new HttpClient(new QaHttpHandler(async (_, ct) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new HttpResponseMessage(HttpStatusCode.OK); })) { Timeout = Timeout.InfiniteTimeSpan })
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            var canceled = false;
            try { await new GroqTranscriptionService(timeoutClient).TranscribeAsync(audio, "qa-key", cts.Token); }
            catch (OperationCanceledException) { canceled = true; }
            if (!canceled) throw new InvalidDataException("Groq cancellation did not stop an in-flight request.");
            checks["groq_cancellation"] = "PASS";
        }

        var ledger = new AiUsageLedgerService(db);
        await GroqTranscriptionService.RecordCostAsync(ledger, "v30-fixture", 3600, 250, "qa-request", true);
        var cost = ledger.SummaryForMeeting("v30-fixture");
        if (!cost.Contains("$0.040000", StringComparison.Ordinal) || !cost.Contains("GROQ_AUDIO_DURATION_PRICED", StringComparison.Ordinal))
            throw new InvalidDataException("Groq hourly usage price or immutable cost snapshot failed.");
        checks["groq_duration_cost"] = "PASS_$0.04_PER_AUDIO_HOUR";

        var signalSettings = new AppSettings { BuzzExe = audio, IntelligenceModel = "fixture", TranscriptionEngine = "native-whisper" };
        await File.WriteAllBytesAsync(Path.Combine(AppPaths.AiModels, "Qwen3-4B-Q4_K_M.gguf"), [1]);
        var status = V30SystemStatus.Snapshot(signalSettings, false, false, true, true, "synthetic mic", "synthetic system device", false);
        if (status.Count != 6 || status.Any(s => string.IsNullOrWhiteSpace(s.State))) throw new InvalidDataException("System Status Pulse signal model failed.");
        checks["system_status_signals"] = "PASS_6_REAL_SIGNALS";
        checks["google_drive_root_detection"] = GoogleDriveDiscovery.FindRoots().Count > 0 ? "DETECTED" : "GRACEFUL_MISSING";

        var queueTcs = new TaskCompletionSource<IntakeJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inbox = new InboxQueueService(testDir, db);
        inbox.Start(job => { queueTcs.TrySetResult(job); return Task.CompletedTask; });
        var fixture = Path.Combine(testDir, "fictional_meeting.wav");
        await File.WriteAllBytesAsync(fixture, Enumerable.Range(0, 2048).Select(i => (byte)(i % 241)).ToArray());
        var firstJob = await queueTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        File.Copy(fixture, Path.Combine(testDir, "same_content_copy.wav"));
        await Task.Delay(TimeSpan.FromSeconds(5));
        inbox.Stop();
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString()))
        {
            c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM intake_jobs WHERE fingerprint=$fingerprint;";
            cmd.Parameters.AddWithValue("$fingerprint", firstJob.Fingerprint);
            if (Convert.ToInt32(cmd.ExecuteScalar()) != 1) throw new InvalidDataException("Inbox content deduplication failed.");
        }
        checks["inbox_file_watcher_and_dedupe"] = "PASS_SYNTHETIC_LOCAL_FOLDER";

        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString()))
        {
            c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA integrity_check;";
            if (!Convert.ToString(cmd.ExecuteScalar())!.Equals("ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("V30 QA database integrity check failed.");
            cmd.CommandText = "PRAGMA foreign_key_check;"; using var rows = cmd.ExecuteReader(); if (rows.Read()) throw new InvalidDataException("V30 QA foreign key check failed.");
        }
        checks["isolated_qa_database"] = "PASS_INTEGRITY_AND_FK";
        var report = new { status = "PASS", build = "V30", checks, cloudLive = "NOT_RUN_NO_USER_CREDENTIAL_IN_QA" };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class QaHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken); }
}
