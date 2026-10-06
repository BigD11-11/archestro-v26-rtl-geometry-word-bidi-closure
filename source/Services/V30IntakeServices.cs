using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Archestro.MeetingVault.Services;

public sealed record IntakeJob(string Id, string SourcePath, string Fingerprint, string SourceType, string State);

public static class GoogleDriveDiscovery
{
    public static IReadOnlyList<string> FindRoots()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[] { Path.Combine(profile, "Google Drive"), Path.Combine(profile, "My Drive"),
                     Environment.GetEnvironmentVariable("GOOGLE_DRIVE_ROOT") ?? "" })
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate)) found.Add(Path.GetFullPath(candidate));
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.IsReady))
        {
            try
            {
                if (drive.VolumeLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase) ||
                    Directory.Exists(Path.Combine(drive.RootDirectory.FullName, "My Drive")))
                    found.Add(drive.RootDirectory.FullName);
            }
            catch { }
        }
        return found.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

/// <summary>Persistent, content-deduplicated inbox queue. Watches only the selected folder.</summary>
public sealed class InboxQueueService : IDisposable
{
    private readonly string _dbPath;
    private readonly string _folder;
    private FileSystemWatcher? _watcher;
    private Timer? _reconcile;
    private Func<IntakeJob, Task>? _onIncoming;
    private int _scanRunning;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".mka", ".mp4", ".mov", ".ogg", ".webm" };

    public InboxQueueService(string folder, string? databasePath = null)
    {
        _folder = Path.GetFullPath(folder);
        _dbPath = databasePath ?? AppPaths.Database;
        EnsureSchema();
    }

    public static void EnsureSchemaForOwnerDatabase() => new InboxQueueService(AppPaths.Inbox).Dispose();

    public bool IsRunning => _watcher?.EnableRaisingEvents == true;
    public string Folder => _folder;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, ForeignKeys = true }.ToString());
        c.Open();
        return c;
    }

    private void EnsureSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_dbPath))!);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS intake_jobs(id TEXT PRIMARY KEY, source_type TEXT NOT NULL, source_name TEXT NOT NULL, source_path TEXT NOT NULL, fingerprint TEXT NOT NULL UNIQUE, received_utc TEXT NOT NULL, state TEXT NOT NULL, retries INTEGER NOT NULL DEFAULT 0, meeting_id TEXT NULL, sanitized_error TEXT NULL, next_retry_utc TEXT NULL); CREATE INDEX IF NOT EXISTS ix_intake_state ON intake_jobs(state,received_utc);";
        cmd.ExecuteNonQuery();
        using var info = c.CreateCommand(); info.CommandText = "PRAGMA table_info(intake_jobs);";
        using var reader = info.ExecuteReader(); var hasRetry = false;
        while (reader.Read()) if (reader.GetString(1).Equals("next_retry_utc", StringComparison.OrdinalIgnoreCase)) hasRetry = true;
        reader.Close();
        if (!hasRetry) { using var alter = c.CreateCommand(); alter.CommandText = "ALTER TABLE intake_jobs ADD COLUMN next_retry_utc TEXT NULL;"; alter.ExecuteNonQuery(); }
    }

    public bool TestFolder() => Directory.Exists(_folder) && Directory.Exists(Path.GetDirectoryName(_folder)!);

    public void Start(Func<IntakeJob, Task> onIncoming)
    {
        Stop();
        if (!Directory.Exists(_folder)) return;
        _onIncoming = onIncoming;
        _watcher = new FileSystemWatcher(_folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
        _watcher.Created += OnChanged; _watcher.Changed += OnChanged; _watcher.Renamed += OnRenamed; _watcher.Error += (_, _) => _ = ReconcileAsync();
        _watcher.EnableRaisingEvents = true;
        _reconcile = new Timer(_ => _ = ReconcileAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
    }

    public void Stop()
    {
        if (_watcher is not null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; }
        _reconcile?.Dispose(); _reconcile = null;
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => QueueCandidate(e.FullPath);
    private void OnRenamed(object sender, RenamedEventArgs e) => QueueCandidate(e.FullPath);
    private void QueueCandidate(string path) { _ = Task.Run(async () => await ProcessCandidateAsync(path)); }
    private async Task ReconcileAsync()
    {
        if (Interlocked.Exchange(ref _scanRunning, 1) != 0) return;
        try
        {
            if (Directory.Exists(_folder))
                foreach (var path in Directory.EnumerateFiles(_folder, "*", SearchOption.TopDirectoryOnly))
                    await ProcessCandidateAsync(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { Volatile.Write(ref _scanRunning, 0); }
    }

    private async Task ProcessCandidateAsync(string path)
    {
        if (!Extensions.Contains(Path.GetExtension(path)) || !File.Exists(path)) return;
        try
        {
            long size = -1;
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                var next = new FileInfo(path).Length;
                if (next == size && next > 0) break;
                size = next;
                if (i == 2) return;
            }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fingerprint = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            var job = new IntakeJob(Guid.NewGuid().ToString("N"), path, fingerprint, "GoogleDriveInbox", "Incoming");
            using var c = Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO intake_jobs(id,source_type,source_name,source_path,fingerprint,received_utc,state) VALUES($id,$type,$name,$path,$fingerprint,$utc,'Incoming'); SELECT changes();";
            cmd.Parameters.AddWithValue("$id", job.Id); cmd.Parameters.AddWithValue("$type", job.SourceType);
            cmd.Parameters.AddWithValue("$name", Path.GetFileName(path)); cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$fingerprint", fingerprint); cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            var inserted = Convert.ToInt64(cmd.ExecuteScalar()) == 1;
            if (!inserted)
            {
                using var retry = c.CreateCommand();
                retry.CommandText = "UPDATE intake_jobs SET state='Incoming' WHERE fingerprint=$fingerprint AND state='Needs Attention' AND retries<3 AND (next_retry_utc IS NULL OR next_retry_utc<=strftime('%Y-%m-%dT%H:%M:%fZ','now')); SELECT changes();";
                retry.Parameters.AddWithValue("$fingerprint", fingerprint);
                inserted = Convert.ToInt64(retry.ExecuteScalar()) == 1;
                if (inserted)
                {
                    using var existing = c.CreateCommand(); existing.CommandText = "SELECT id,source_type,state FROM intake_jobs WHERE fingerprint=$fingerprint;";
                    existing.Parameters.AddWithValue("$fingerprint", fingerprint); using var r = existing.ExecuteReader();
                    if (r.Read()) job = job with { Id = r.GetString(0), SourceType = r.GetString(1), State = r.GetString(2) };
                }
            }
            if (inserted && _onIncoming is not null) await _onIncoming(job);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SqliteException) { }
    }

    public void SetState(string fingerprint, string state, string? meetingId = null, string? sanitizedError = null)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE intake_jobs SET state=$state,meeting_id=COALESCE($meeting,meeting_id),sanitized_error=$error,retries=CASE WHEN $state='Needs Attention' THEN retries+1 ELSE retries END,next_retry_utc=CASE WHEN $state='Needs Attention' AND retries<3 THEN strftime('%Y-%m-%dT%H:%M:%fZ','now','+'||min(60,2*(1 << retries))||' minutes') ELSE NULL END WHERE fingerprint=$fingerprint;";
        cmd.Parameters.AddWithValue("$state", state); cmd.Parameters.AddWithValue("$meeting", (object?)meetingId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)sanitizedError ?? DBNull.Value); cmd.Parameters.AddWithValue("$fingerprint", fingerprint); cmd.ExecuteNonQuery();
    }

    public void Dispose() => Stop();
}

public sealed record StatusSignal(string Name, string State, string Detail);

public static class V30SystemStatus
{
    public static bool HasLocalQwenModel()
    {
        var configured = Environment.GetEnvironmentVariable("ARCHESTRO_AI_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return true;
        var bundled = Path.Combine(AppContext.BaseDirectory, "AI", "Models", "Qwen3-4B-Q4_K_M.gguf");
        return File.Exists(bundled) || File.Exists(Path.Combine(AppPaths.AiModels, "Qwen3-4B-Q4_K_M.gguf"));
    }

    public static IReadOnlyList<StatusSignal> Snapshot(Models.AppSettings settings, bool recording, bool systemAudio,
        bool microphoneReady, bool systemAudioReady, string microphoneDevice, string systemDevice, bool inboxRunning)
    {
        var bridge = !string.IsNullOrWhiteSpace(settings.BuzzExe) && File.Exists(settings.BuzzExe);
        var qwen = HasLocalQwenModel();
        var groq = !string.IsNullOrWhiteSpace(settings.EncryptedGroqApiKey);
        var deepSeek = !string.IsNullOrWhiteSpace(settings.EncryptedIntelligenceApiKey);
        return new[]
        {
            new StatusSignal("Recording", !microphoneReady ? "Needs Attention" : recording ? "Working" : "Ready", "WASAPI microphone • " + microphoneDevice),
            new StatusSignal("System Audio", !systemAudioReady ? "Needs Attention" : systemAudio ? "Working" : "Ready", "WASAPI loopback • " + (systemAudio ? "receiving" : "not receiving") + " • " + systemDevice),
            new StatusSignal("Intake", inboxRunning ? "Ready" : (string.IsNullOrWhiteSpace(settings.InboxPath) ? "Disabled" : "Needs Attention"), inboxRunning ? "Inbox watcher active" : "Inbox not configured"),
            new StatusSignal("Transcription", bridge ? "Ready" : "Needs Attention", bridge ? settings.TranscriptionEngine : "Native Whisper bridge missing"),
            new StatusSignal("Local AI", qwen ? "Ready" : "Needs Attention", qwen ? settings.IntelligenceModel : "Local Qwen model missing"),
            new StatusSignal("Cloud AI", groq || deepSeek ? "Ready" : "Disabled", groq && deepSeek ? "Groq + DeepSeek keys saved; connectivity not tested" : groq ? "Groq key saved; connectivity not tested" : deepSeek ? "DeepSeek key saved; connectivity not tested" : "No cloud credentials saved")
        };
    }
}
