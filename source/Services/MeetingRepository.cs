using Microsoft.Data.Sqlite;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public sealed class MeetingRepository
{
    private readonly string _connectionString;

    public MeetingRepository(string? databasePath = null)
    {
        AppPaths.Ensure();
        var db = databasePath ?? AppPaths.Database;
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        _connectionString = $"Data Source={db}";
        Initialize();
    }

    private void Initialize()
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();

        using var cmd = c.CreateCommand();
        cmd.CommandText = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS meetings (
            id TEXT PRIMARY KEY,
            folder_path TEXT NOT NULL UNIQUE,
            title TEXT NOT NULL DEFAULT '',
            has_explicit_title INTEGER NOT NULL DEFAULT 0,
            suggested_title TEXT NOT NULL DEFAULT '',
            start_local TEXT NOT NULL,
            end_local TEXT NULL,
            duration_seconds INTEGER NOT NULL DEFAULT 0,
            recording_path TEXT NOT NULL DEFAULT '',
            audio_path TEXT NOT NULL DEFAULT '',
            transcript_path TEXT NOT NULL DEFAULT '',
            srt_path TEXT NOT NULL DEFAULT '',
            transcription_status TEXT NOT NULL DEFAULT 'Saved',
            notes TEXT NOT NULL DEFAULT '',
            category TEXT NOT NULL DEFAULT 'Uncategorized',
            category_color TEXT NOT NULL DEFAULT '#7B8798',
            marks_count INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS important_marks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            meeting_id TEXT NOT NULL,
            offset_seconds INTEGER NOT NULL,
            created_local TEXT NOT NULL
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS meeting_fts USING fts5(
            meeting_id UNINDEXED,
            title,
            suggested_title,
            notes,
            transcript
        );

        CREATE TABLE IF NOT EXISTS categories (
            name TEXT PRIMARY KEY COLLATE NOCASE,
            color TEXT NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 500,
            pinned INTEGER NOT NULL DEFAULT 0,
            created_local TEXT NOT NULL
        );
        """;
        cmd.ExecuteNonQuery();
        var addedPinnedColumn = EnsureCategoryPinnedColumn(c);
        SeedCategories(c);

        if (addedPinnedColumn)
        {
            using var pinDefaults = c.CreateCommand();
            pinDefaults.CommandText = "UPDATE categories SET pinned=1 WHERE sort_order <= 80;";
            pinDefaults.ExecuteNonQuery();
        }
    }

    private static bool EnsureCategoryPinnedColumn(SqliteConnection c)
    {
        using var inspect = c.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(categories);";
        using var reader = inspect.ExecuteReader();

        var hasPinned = false;
        while (reader.Read())
        {
            if (reader.GetString(1).Equals("pinned", StringComparison.OrdinalIgnoreCase))
            {
                hasPinned = true;
                break;
            }
        }

        if (hasPinned) return false;

        using var alter = c.CreateCommand();
        alter.CommandText = "ALTER TABLE categories ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0;";
        alter.ExecuteNonQuery();
        return true;
    }

    private static void SeedCategories(SqliteConnection c)
    {
        foreach (var item in CategoryCatalog.Defaults)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
            INSERT INTO categories(name,color,sort_order,pinned,created_local)
            VALUES($name,$color,$sort,$pinned,$created)
            ON CONFLICT(name) DO NOTHING;
            """;
            cmd.Parameters.AddWithValue("$name", item.Name);
            cmd.Parameters.AddWithValue("$color", item.Color);
            cmd.Parameters.AddWithValue("$sort", item.SortOrder);
            cmd.Parameters.AddWithValue("$pinned", item.SortOrder <= 80 ? 1 : 0);
            cmd.Parameters.AddWithValue("$created", DateTimeOffset.Now.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public void Upsert(MeetingRecord m, string transcriptText = "")
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var tx = c.BeginTransaction();

        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
            INSERT INTO meetings (
              id, folder_path, title, has_explicit_title, suggested_title, start_local, end_local,
              duration_seconds, recording_path, audio_path, transcript_path, srt_path,
              transcription_status, notes, category, category_color, marks_count
            ) VALUES (
              $id,$folder,$title,$explicit,$suggested,$start,$end,$duration,$recording,$audio,$transcript,$srt,
              $status,$notes,$category,$color,$marks
            )
            ON CONFLICT(id) DO UPDATE SET
              folder_path=excluded.folder_path,
              title=excluded.title,
              has_explicit_title=excluded.has_explicit_title,
              suggested_title=excluded.suggested_title,
              start_local=excluded.start_local,
              end_local=excluded.end_local,
              duration_seconds=excluded.duration_seconds,
              recording_path=excluded.recording_path,
              audio_path=excluded.audio_path,
              transcript_path=excluded.transcript_path,
              srt_path=excluded.srt_path,
              transcription_status=excluded.transcription_status,
              notes=excluded.notes,
              category=excluded.category,
              category_color=excluded.category_color,
              marks_count=excluded.marks_count;
            """;
            Bind(cmd, m);
            cmd.ExecuteNonQuery();
        }

        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM meeting_fts WHERE meeting_id=$id;";
            del.Parameters.AddWithValue("$id", m.Id);
            del.ExecuteNonQuery();
        }

        using (var ins = c.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
            INSERT INTO meeting_fts(meeting_id,title,suggested_title,notes,transcript)
            VALUES($id,$title,$suggested,$notes,$transcript);
            """;
            ins.Parameters.AddWithValue("$id", m.Id);
            ins.Parameters.AddWithValue("$title", m.Title);
            ins.Parameters.AddWithValue("$suggested", m.SuggestedTitle);
            ins.Parameters.AddWithValue("$notes", m.Notes);
            ins.Parameters.AddWithValue("$transcript", transcriptText);
            ins.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void AddMark(ImportantMark mark)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();

        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
            INSERT INTO important_marks(meeting_id,offset_seconds,created_local)
            VALUES($meeting,$offset,$created);
            """;
            cmd.Parameters.AddWithValue("$meeting", mark.MeetingId);
            cmd.Parameters.AddWithValue("$offset", mark.OffsetSeconds);
            cmd.Parameters.AddWithValue("$created", mark.CreatedLocal.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        using (var update = c.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE meetings SET marks_count=marks_count+1 WHERE id=$id;";
            update.Parameters.AddWithValue("$id", mark.MeetingId);
            update.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<ImportantMark> GetMarks(string meetingId)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();

        using var cmd = c.CreateCommand();
        cmd.CommandText = """
        SELECT id, meeting_id, offset_seconds, created_local
        FROM important_marks
        WHERE meeting_id=$meeting
        ORDER BY offset_seconds ASC, id ASC;
        """;
        cmd.Parameters.AddWithValue("$meeting", meetingId);

        var list = new List<ImportantMark>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ImportantMark
            {
                Id = reader.GetInt64(0),
                MeetingId = reader.GetString(1),
                OffsetSeconds = reader.GetInt32(2),
                CreatedLocal = DateTimeOffset.Parse(reader.GetString(3))
            });
        }
        return list;
    }

    public IReadOnlyList<MeetingRecord> Recent(int limit = 5)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM meetings ORDER BY start_local DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        return Read(cmd);
    }

    public IReadOnlyList<MeetingRecord> Search(string? query, string? category, int limit = 200)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();

        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var hasCategory = !string.IsNullOrWhiteSpace(category) &&
                          !category.Equals("All", StringComparison.OrdinalIgnoreCase);

        if (!hasQuery)
        {
            using var plain = c.CreateCommand();
            plain.CommandText = "SELECT * FROM meetings " +
                                (hasCategory ? "WHERE category=$category " : "") +
                                "ORDER BY start_local DESC LIMIT $limit;";
            if (hasCategory) plain.Parameters.AddWithValue("$category", category!);
            plain.Parameters.AddWithValue("$limit", limit);
            return Read(plain);
        }

        var results = new List<MeetingRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Fast path: SQLite FTS5.
        try
        {
            using var fts = c.CreateCommand();
            fts.CommandText = """
            SELECT m.*
            FROM meetings m
            INNER JOIN meeting_fts f ON f.meeting_id=m.id
            WHERE meeting_fts MATCH $q
            """ + (hasCategory ? " AND m.category=$category " : " ") +
            " ORDER BY m.start_local DESC LIMIT $limit;";
            fts.Parameters.AddWithValue("$q", BuildFtsQuery(query!));
            if (hasCategory) fts.Parameters.AddWithValue("$category", category!);
            fts.Parameters.AddWithValue("$limit", limit);

            foreach (var meeting in Read(fts))
            {
                if (seen.Add(meeting.Id)) results.Add(meeting);
            }
        }
        catch
        {
            // A malformed/edge FTS query must never make Search look broken.
        }

        // Recall path: Arabic/English normalization + filesystem transcript scan.
        // This catches hamza/diacritic variants and exact phrases that FTS tokenization
        // may miss, while preserving the fast FTS result order first.
        var normalizedQuery = NormalizeSearchText(query!);
        if (!string.IsNullOrWhiteSpace(normalizedQuery) && results.Count < limit)
        {
            using var all = c.CreateCommand();
            all.CommandText = "SELECT * FROM meetings " +
                              (hasCategory ? "WHERE category=$category " : "") +
                              "ORDER BY start_local DESC LIMIT 500;";
            if (hasCategory) all.Parameters.AddWithValue("$category", category!);

            foreach (var meeting in Read(all))
            {
                if (seen.Contains(meeting.Id)) continue;

                var searchable =
                    meeting.PrimaryTitle + "\n" +
                    meeting.SuggestedTitle + "\n" +
                    meeting.Notes + "\n";

                try
                {
                    if (File.Exists(meeting.TranscriptPath))
                        searchable += File.ReadAllText(meeting.TranscriptPath);
                }
                catch { }

                if (!NormalizeSearchText(searchable).Contains(
                        normalizedQuery,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                seen.Add(meeting.Id);
                results.Add(meeting);
                if (results.Count >= limit) break;
            }
        }

        return results;
    }

    private static string NormalizeSearchText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var normalized = text
            .Normalize(System.Text.NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Replace('أ', 'ا')
            .Replace('إ', 'ا')
            .Replace('آ', 'ا')
            .Replace('ى', 'ي')
            .Replace('ؤ', 'و')
            .Replace('ئ', 'ي')
            .Replace('ة', 'ه');

        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"[\u064B-\u065F\u0670\u06D6-\u06ED]",
            "");

        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"[^\p{L}\p{N}]+",
            " ");

        return System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"\s+",
            " ").Trim();
    }

    public MeetingRecord? Get(string id)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM meetings WHERE id=$id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);
        return Read(cmd).FirstOrDefault();
    }



    public IReadOnlyList<CategoryRecord> GetCategories()
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
        SELECT c.name, c.color, c.sort_order, c.pinned, COUNT(m.id) AS meeting_count
        FROM categories c
        LEFT JOIN meetings m ON m.category = c.name COLLATE NOCASE
        GROUP BY c.name, c.color, c.sort_order, c.pinned
        ORDER BY c.pinned DESC, c.sort_order ASC, meeting_count DESC, c.name COLLATE NOCASE ASC;
        """;

        var list = new List<CategoryRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new CategoryRecord
            {
                Name = reader.GetString(0),
                Color = reader.GetString(1),
                SortOrder = Convert.ToInt32(reader.GetInt64(2)),
                Pinned = reader.GetInt64(3) == 1,
                MeetingCount = Convert.ToInt32(reader.GetInt64(4))
            });
        }
        return list;
    }

    public bool AddCategory(string name, string? color = null)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) return false;

        using var c = new SqliteConnection(_connectionString);
        c.Open();

        using (var exists = c.CreateCommand())
        {
            exists.CommandText = "SELECT COUNT(*) FROM categories WHERE name=$name COLLATE NOCASE;";
            exists.Parameters.AddWithValue("$name", name);
            if (Convert.ToInt32(exists.ExecuteScalar()) > 0) return false;
        }

        var nextSort = 100;
        using (var next = c.CreateCommand())
        {
            next.CommandText = "SELECT COALESCE(MAX(sort_order),0)+10 FROM categories WHERE pinned=1;";
            nextSort = Convert.ToInt32(next.ExecuteScalar());
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText = """
        INSERT INTO categories(name,color,sort_order,pinned,created_local)
        VALUES($name,$color,$sort,1,$created);
        """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$color", string.IsNullOrWhiteSpace(color) ? CategoryCatalog.ColorFromName(name) : color);
        cmd.Parameters.AddWithValue("$sort", nextSort);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.Now.ToString("o"));
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool SetCategoryPinned(string name, bool pinned)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();

        var sort = pinned ? 100 : 500;
        using (var next = c.CreateCommand())
        {
            next.CommandText = pinned
                ? "SELECT COALESCE(MAX(sort_order),0)+10 FROM categories WHERE pinned=1;"
                : "SELECT COALESCE(MAX(sort_order),490)+10 FROM categories WHERE pinned=0;";
            sort = Convert.ToInt32(next.ExecuteScalar());
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE categories SET pinned=$pinned, sort_order=$sort WHERE name=$name COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort", sort);
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool MoveCategory(string name, int direction)
    {
        if (direction == 0) return false;

        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var tx = c.BeginTransaction();

        string currentName;
        long currentSort;
        long currentPinned;

        using (var current = c.CreateCommand())
        {
            current.Transaction = tx;
            current.CommandText = "SELECT name,sort_order,pinned FROM categories WHERE name=$name COLLATE NOCASE LIMIT 1;";
            current.Parameters.AddWithValue("$name", name);
            using var reader = current.ExecuteReader();
            if (!reader.Read()) return false;
            currentName = reader.GetString(0);
            currentSort = reader.GetInt64(1);
            currentPinned = reader.GetInt64(2);
        }

        string? otherName = null;
        long otherSort = 0;
        using (var other = c.CreateCommand())
        {
            other.Transaction = tx;
            other.CommandText = direction < 0
                ? "SELECT name,sort_order FROM categories WHERE pinned=$pinned AND sort_order < $sort ORDER BY sort_order DESC LIMIT 1;"
                : "SELECT name,sort_order FROM categories WHERE pinned=$pinned AND sort_order > $sort ORDER BY sort_order ASC LIMIT 1;";
            other.Parameters.AddWithValue("$pinned", currentPinned);
            other.Parameters.AddWithValue("$sort", currentSort);
            using var reader = other.ExecuteReader();
            if (reader.Read())
            {
                otherName = reader.GetString(0);
                otherSort = reader.GetInt64(1);
            }
        }

        if (string.IsNullOrWhiteSpace(otherName)) return false;

        using (var first = c.CreateCommand())
        {
            first.Transaction = tx;
            first.CommandText = "UPDATE categories SET sort_order=$sort WHERE name=$name COLLATE NOCASE;";
            first.Parameters.AddWithValue("$sort", otherSort);
            first.Parameters.AddWithValue("$name", currentName);
            first.ExecuteNonQuery();
        }

        using (var second = c.CreateCommand())
        {
            second.Transaction = tx;
            second.CommandText = "UPDATE categories SET sort_order=$sort WHERE name=$name COLLATE NOCASE;";
            second.Parameters.AddWithValue("$sort", currentSort);
            second.Parameters.AddWithValue("$name", otherName);
            second.ExecuteNonQuery();
        }

        tx.Commit();
        return true;
    }

    public bool RenameCategory(string oldName, string newName)
    {
        oldName = (oldName ?? "").Trim();
        newName = (newName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return false;
        if (oldName.Equals(newName, StringComparison.OrdinalIgnoreCase)) return true;

        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var tx = c.BeginTransaction();

        using (var exists = c.CreateCommand())
        {
            exists.Transaction = tx;
            exists.CommandText = "SELECT COUNT(*) FROM categories WHERE name=$name COLLATE NOCASE;";
            exists.Parameters.AddWithValue("$name", newName);
            if (Convert.ToInt32(exists.ExecuteScalar()) > 0) return false;
        }

        using (var rename = c.CreateCommand())
        {
            rename.Transaction = tx;
            rename.CommandText = "UPDATE categories SET name=$new WHERE name=$old COLLATE NOCASE;";
            rename.Parameters.AddWithValue("$new", newName);
            rename.Parameters.AddWithValue("$old", oldName);
            if (rename.ExecuteNonQuery() != 1) return false;
        }

        using (var meetings = c.CreateCommand())
        {
            meetings.Transaction = tx;
            meetings.CommandText = "UPDATE meetings SET category=$new WHERE category=$old COLLATE NOCASE;";
            meetings.Parameters.AddWithValue("$new", newName);
            meetings.Parameters.AddWithValue("$old", oldName);
            meetings.ExecuteNonQuery();
        }

        tx.Commit();
        SyncCategoryMetadata(newName);
        return true;
    }

    public bool DeleteCategory(string name)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase)) return false;

        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var tx = c.BeginTransaction();

        var uncatColor = CategoryCatalog.ColorFor("Uncategorized");

        using (var move = c.CreateCommand())
        {
            move.Transaction = tx;
            move.CommandText = """
            UPDATE meetings
            SET category='Uncategorized', category_color=$color
            WHERE category=$name COLLATE NOCASE;
            """;
            move.Parameters.AddWithValue("$color", uncatColor);
            move.Parameters.AddWithValue("$name", name);
            move.ExecuteNonQuery();
        }

        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM categories WHERE name=$name COLLATE NOCASE;";
            del.Parameters.AddWithValue("$name", name);
            if (del.ExecuteNonQuery() != 1) return false;
        }

        tx.Commit();
        SyncCategoryMetadata("Uncategorized");
        return true;
    }

    private void SyncCategoryMetadata(string category)
    {
        foreach (var meeting in Search(null, category, 1000))
        {
            try { MeetingMetadataService.Write(meeting); } catch { }
        }
    }

    public string GetCategoryColor(string category)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT color FROM categories WHERE name=$name COLLATE NOCASE LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", category);
        return cmd.ExecuteScalar()?.ToString() ?? CategoryCatalog.ColorFor(category);
    }

    public void Delete(string id)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var tx = c.BeginTransaction();

        using (var marks = c.CreateCommand())
        {
            marks.Transaction = tx;
            marks.CommandText = "DELETE FROM important_marks WHERE meeting_id=$id;";
            marks.Parameters.AddWithValue("$id", id);
            marks.ExecuteNonQuery();
        }
        using (var fts = c.CreateCommand())
        {
            fts.Transaction = tx;
            fts.CommandText = "DELETE FROM meeting_fts WHERE meeting_id=$id;";
            fts.Parameters.AddWithValue("$id", id);
            fts.ExecuteNonQuery();
        }
        using (var meeting = c.CreateCommand())
        {
            meeting.Transaction = tx;
            meeting.CommandText = "DELETE FROM meetings WHERE id=$id;";
            meeting.Parameters.AddWithValue("$id", id);
            meeting.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void UpdateEditableFields(string id, string title, bool explicitTitle, string notes, string category)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
        UPDATE meetings SET
          title=$title,
          has_explicit_title=$explicit,
          notes=$notes,
          category=$category,
          category_color=$color
        WHERE id=$id;
        """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$explicit", explicitTitle ? 1 : 0);
        cmd.Parameters.AddWithValue("$notes", notes);
        cmd.Parameters.AddWithValue("$category", category);
        cmd.Parameters.AddWithValue("$color", GetCategoryColor(category));
        cmd.ExecuteNonQuery();

        var record = Get(id);
        if (record is not null)
        {
            var transcript = File.Exists(record.TranscriptPath) ? File.ReadAllText(record.TranscriptPath) : "";
            Upsert(record, transcript);
            try { MeetingMetadataService.Write(record); } catch { }
        }
    }

    private static string BuildFtsQuery(string raw)
    {
        var tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Replace("\"", "\"\""))
            .Where(t => t.Length > 0)
            .Take(12)
            .Select(t => $"\"{t}\"*");
        var built = string.Join(" AND ", tokens);
        return string.IsNullOrWhiteSpace(built) ? "\"\"" : built;
    }

    private static void Bind(SqliteCommand cmd, MeetingRecord m)
    {
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$folder", m.FolderPath);
        cmd.Parameters.AddWithValue("$title", m.Title);
        cmd.Parameters.AddWithValue("$explicit", m.HasExplicitTitle ? 1 : 0);
        cmd.Parameters.AddWithValue("$suggested", m.SuggestedTitle);
        cmd.Parameters.AddWithValue("$start", m.StartLocal.ToString("o"));
        cmd.Parameters.AddWithValue("$end", m.EndLocal?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$duration", m.DurationSeconds);
        cmd.Parameters.AddWithValue("$recording", m.RecordingPath);
        cmd.Parameters.AddWithValue("$audio", m.AudioPath);
        cmd.Parameters.AddWithValue("$transcript", m.TranscriptPath);
        cmd.Parameters.AddWithValue("$srt", m.SrtPath);
        cmd.Parameters.AddWithValue("$status", m.TranscriptionStatus);
        cmd.Parameters.AddWithValue("$notes", m.Notes);
        cmd.Parameters.AddWithValue("$category", m.Category);
        cmd.Parameters.AddWithValue("$color", m.CategoryColor);
        cmd.Parameters.AddWithValue("$marks", m.MarksCount);
    }

    private static IReadOnlyList<MeetingRecord> Read(SqliteCommand cmd)
    {
        var list = new List<MeetingRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new MeetingRecord
            {
                Id = r.GetString(r.GetOrdinal("id")),
                FolderPath = r.GetString(r.GetOrdinal("folder_path")),
                Title = r.GetString(r.GetOrdinal("title")),
                HasExplicitTitle = r.GetInt32(r.GetOrdinal("has_explicit_title")) == 1,
                SuggestedTitle = r.GetString(r.GetOrdinal("suggested_title")),
                StartLocal = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("start_local"))),
                EndLocal = r.IsDBNull(r.GetOrdinal("end_local")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("end_local"))),
                DurationSeconds = r.GetInt32(r.GetOrdinal("duration_seconds")),
                RecordingPath = r.GetString(r.GetOrdinal("recording_path")),
                AudioPath = r.GetString(r.GetOrdinal("audio_path")),
                TranscriptPath = r.GetString(r.GetOrdinal("transcript_path")),
                SrtPath = r.GetString(r.GetOrdinal("srt_path")),
                TranscriptionStatus = r.GetString(r.GetOrdinal("transcription_status")),
                Notes = r.GetString(r.GetOrdinal("notes")),
                Category = r.GetString(r.GetOrdinal("category")),
                CategoryColor = r.GetString(r.GetOrdinal("category_color")),
                MarksCount = r.GetInt32(r.GetOrdinal("marks_count"))
            });
        }
        return list;
    }
}
