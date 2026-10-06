using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Archestro.MeetingVault.Services;

public sealed record AiUsageRow(string Id, DateTimeOffset TimestampUtc, string? MeetingId, string? ReportId,
    string Provider, string Model, string Operation, string? RequestId, int? InputTokens, int? CacheHitTokens,
    int? CacheMissTokens, int? OutputTokens, int? ReasoningTokens, int? TotalTokens, long ElapsedMs,
    bool Success, string? PricingSnapshotId, decimal? CostUsd, decimal? CostSar, string CostQuality,
    int? AudioDurationSeconds = null, int? RequestCount = null);

public sealed class AiUsageLedgerService
{
    private const string PricingId = "deepseek-2026-10-06-v1";
    private const string FxId = "usd-sar-2026-10-06-v1";
    private readonly string _dbPath;

    public AiUsageLedgerService(string? databasePath = null) { _dbPath = databasePath ?? AppPaths.Database; EnsureSchema(); }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, ForeignKeys = true }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON;"; cmd.ExecuteNonQuery();
        return connection;
    }

    private void EnsureSchema()
    {
        AppPaths.Ensure();
        using var c = Open(); using var t = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS ai_pricing_snapshots(
          id TEXT PRIMARY KEY, provider TEXT NOT NULL, model TEXT NOT NULL, effective_from_utc TEXT NOT NULL,
          cache_hit_usd_per_million REAL, cache_miss_usd_per_million REAL, output_usd_per_million REAL,
          peak_cache_hit_usd_per_million REAL, peak_cache_miss_usd_per_million REAL, peak_output_usd_per_million REAL,
          currency TEXT NOT NULL, source TEXT NOT NULL, snapshot_json TEXT NOT NULL, created_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS ai_fx_snapshots(
          id TEXT PRIMARY KEY, base_currency TEXT NOT NULL, quote_currency TEXT NOT NULL,
          rate REAL NOT NULL, effective_from_utc TEXT NOT NULL, source TEXT NOT NULL, created_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS app_schema_migrations(
          version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS ai_usage_ledger(
          id TEXT PRIMARY KEY, timestamp_utc TEXT NOT NULL, meeting_id TEXT NULL,
          report_id TEXT NULL, provider TEXT NOT NULL, model TEXT NOT NULL, operation_type TEXT NOT NULL,
          request_id TEXT NULL, input_tokens INTEGER NULL, cache_hit_tokens INTEGER NULL, cache_miss_tokens INTEGER NULL,
          output_tokens INTEGER NULL, reasoning_tokens INTEGER NULL, total_tokens INTEGER NULL, elapsed_ms INTEGER NOT NULL,
          audio_duration_seconds INTEGER NULL, request_count INTEGER NOT NULL DEFAULT 1,
          success INTEGER NOT NULL, pricing_snapshot_id TEXT NULL REFERENCES ai_pricing_snapshots(id),
          fx_snapshot_id TEXT NULL REFERENCES ai_fx_snapshots(id), calculated_cost_usd REAL NULL,
          calculated_cost_sar REAL NULL, cost_quality TEXT NOT NULL, failure_type TEXT NULL);
        CREATE INDEX IF NOT EXISTS ix_ai_usage_time ON ai_usage_ledger(timestamp_utc);
        CREATE INDEX IF NOT EXISTS ix_ai_usage_meeting ON ai_usage_ledger(meeting_id, report_id);
        CREATE TRIGGER IF NOT EXISTS ai_usage_immutable_update BEFORE UPDATE ON ai_usage_ledger BEGIN SELECT RAISE(ABORT,'AI usage ledger is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS ai_usage_immutable_delete BEFORE DELETE ON ai_usage_ledger BEGIN SELECT RAISE(ABORT,'AI usage ledger is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS ai_pricing_immutable_update BEFORE UPDATE ON ai_pricing_snapshots BEGIN SELECT RAISE(ABORT,'Pricing snapshots are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS ai_pricing_immutable_delete BEFORE DELETE ON ai_pricing_snapshots BEGIN SELECT RAISE(ABORT,'Pricing snapshots are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS ai_fx_immutable_update BEFORE UPDATE ON ai_fx_snapshots BEGIN SELECT RAISE(ABORT,'FX snapshots are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS ai_fx_immutable_delete BEFORE DELETE ON ai_fx_snapshots BEGIN SELECT RAISE(ABORT,'FX snapshots are immutable'); END;
        """;
        cmd.ExecuteNonQuery();
        EnsureColumn(c, t, "ai_usage_ledger", "audio_duration_seconds", "INTEGER NULL");
        EnsureColumn(c, t, "ai_usage_ledger", "request_count", "INTEGER NOT NULL DEFAULT 1");
        cmd.CommandText = "INSERT OR IGNORE INTO app_schema_migrations(version,name,applied_utc) VALUES(29,'ai_usage_cost_ledger_v1_1_0',$now);";
        cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT OR IGNORE INTO app_schema_migrations(version,name,applied_utc) VALUES(30,'v30_intake_groq_usage_cost',$now);";
        cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT OR IGNORE INTO ai_pricing_snapshots VALUES($id,'DeepSeek','deepseek-flash','2026-10-06T00:00:00Z',0.003,0.15,0.6,0.006,0.3,1.2,'USD','https://api-docs.deepseek.com/quick_start/pricing/',$json,$now);";
        cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id", PricingId);
        cmd.Parameters.AddWithValue("$json", "{\"version\":1,\"schedule\":\"UTC weekdays except Chinese public holidays; peak 01-04 and 06-10\",\"sourceVerifiedUtc\":\"2026-10-06\"}");
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT OR IGNORE INTO ai_pricing_snapshots VALUES('groq-whisper-large-v3-turbo-2026-10-06-v1','Groq','whisper-large-v3-turbo','2026-10-06T00:00:00Z',0,0,0,0,0,0,'USD','https://console.groq.com/docs/model/whisper-large-v3-turbo/','{\"unit\":\"audio_hour\",\"usdPerHour\":0.04,\"sourceVerifiedUtc\":\"2026-10-06\"}',$now);";
        cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT OR IGNORE INTO ai_fx_snapshots VALUES($id,'USD','SAR',3.75,'2026-10-06T00:00:00Z','https://www.sama.gov.sa/en-US/MediaCenter/News/Pages/news-557.aspx',$now);";
        cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id", FxId); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        t.Commit();
    }

    public void Record(AiUsageRow row, string? failureType = null)
    {
        EnsureSchema();
        decimal? usd = null, sar = null; string quality;
        string? pricing = null, fx = null;
        if (row.Provider.Equals("Local", StringComparison.OrdinalIgnoreCase)) { usd = 0; sar = 0; quality = "LOCAL_ZERO"; }
        else if (row.Provider.Equals("Groq", StringComparison.OrdinalIgnoreCase) && row.Operation.Equals("Transcription", StringComparison.OrdinalIgnoreCase) && row.AudioDurationSeconds.HasValue)
        {
            usd = Math.Max(0, row.AudioDurationSeconds.Value) * 0.04m / 3600m; sar = usd * 3.75m;
            quality = "GROQ_AUDIO_DURATION_PRICED"; pricing = "groq-whisper-large-v3-turbo-2026-10-06-v1"; fx = FxId;
        }
        else if (row.InputTokens.HasValue && row.OutputTokens.HasValue && ResolvePricing(row.Provider,row.Model,row.TimestampUtc) is { } rates)
        {
            var utc = row.TimestampUtc.UtcDateTime;
            var peak = utc.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday && ((utc.Hour >= 1 && utc.Hour < 4) || (utc.Hour >= 6 && utc.Hour < 10));
            var hit = Math.Max(0, row.CacheHitTokens ?? 0); var miss = Math.Max(0, row.CacheMissTokens ?? Math.Max(0, row.InputTokens.Value - hit));
            usd = ((hit * (peak ? rates.PeakHit : rates.OffPeakHit)) + (miss * (peak ? rates.PeakMiss : rates.OffPeakMiss)) + (Math.Max(0, row.OutputTokens.Value) * (peak ? rates.PeakOutput : rates.OffPeakOutput))) / 1_000_000m;
            sar = usd * 3.75m; quality = "EXACT_USAGE_CALCULATED_PRICE"; pricing = rates.Id; fx = FxId;
        }
        else { quality = "RATE_UNKNOWN"; }
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO ai_usage_ledger(id,timestamp_utc,meeting_id,report_id,provider,model,operation_type,request_id,input_tokens,cache_hit_tokens,cache_miss_tokens,output_tokens,reasoning_tokens,total_tokens,elapsed_ms,audio_duration_seconds,request_count,success,pricing_snapshot_id,fx_snapshot_id,calculated_cost_usd,calculated_cost_sar,cost_quality,failure_type) VALUES($id,$ts,$meeting,$report,$provider,$model,$op,$rid,$input,$hit,$miss,$output,$reasoning,$total,$elapsed,$audio,$requests,$success,$pricing,$fx,$usd,$sar,$quality,$failure);";
        cmd.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(row.Id) ? Guid.NewGuid().ToString("N") : row.Id);
        cmd.Parameters.AddWithValue("$ts", row.TimestampUtc.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$meeting", (object?)row.MeetingId ?? DBNull.Value); cmd.Parameters.AddWithValue("$report", (object?)row.ReportId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$provider", row.Provider); cmd.Parameters.AddWithValue("$model", row.Model); cmd.Parameters.AddWithValue("$op", row.Operation);
        cmd.Parameters.AddWithValue("$rid", (object?)row.RequestId ?? DBNull.Value);
        AddNullable(cmd,"$input",row.InputTokens); AddNullable(cmd,"$hit",row.CacheHitTokens); AddNullable(cmd,"$miss",row.CacheMissTokens); AddNullable(cmd,"$output",row.OutputTokens); AddNullable(cmd,"$reasoning",row.ReasoningTokens); AddNullable(cmd,"$total",row.TotalTokens);
        cmd.Parameters.AddWithValue("$elapsed", Math.Max(0,row.ElapsedMs)); cmd.Parameters.AddWithValue("$audio", (object?)row.AudioDurationSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$requests", Math.Max(1,row.RequestCount ?? 1)); cmd.Parameters.AddWithValue("$success",row.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("$pricing", (object?)pricing ?? DBNull.Value); cmd.Parameters.AddWithValue("$fx", (object?)fx ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$usd", (object?)usd ?? DBNull.Value); cmd.Parameters.AddWithValue("$sar", (object?)sar ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$quality",quality); cmd.Parameters.AddWithValue("$failure", (object?)failureType ?? DBNull.Value); cmd.ExecuteNonQuery();
    }

    public void ImportPricingSnapshot(string json)
    {
        using var doc=JsonDocument.Parse(json); var root=doc.RootElement;
        string id=root.GetProperty("snapshotId").GetString()??""; string provider=root.GetProperty("provider").GetString()??"";
        string model=root.GetProperty("model").GetString()??""; string effective=root.GetProperty("effectiveFromUtc").GetString()??"";
        string currency=root.GetProperty("currency").GetString()??""; string source=root.GetProperty("source").GetString()??"";
        var off=root.GetProperty("rates").GetProperty("offPeak"); var peak=root.GetProperty("rates").GetProperty("peak");
        decimal Rate(JsonElement e,string key)=>e.GetProperty(key).GetDecimal();
        var oHit=Rate(off,"cacheHitUsdPerMillion"); var oMiss=Rate(off,"cacheMissUsdPerMillion"); var oOut=Rate(off,"outputUsdPerMillion");
        var pHit=Rate(peak,"cacheHitUsdPerMillion"); var pMiss=Rate(peak,"cacheMissUsdPerMillion"); var pOut=Rate(peak,"outputUsdPerMillion");
        if(id.Length is < 4 or > 100 || provider.Length is < 2 or > 40 || model.Length is < 2 or > 120 || currency!="USD" || !DateTimeOffset.TryParse(effective,out var effectiveAt) || !effectiveAt.Offset.Equals(TimeSpan.Zero) ||
            new[]{oHit,oMiss,oOut,pHit,pMiss,pOut}.Any(x=>x<0) || string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("Price snapshot schema/rates are invalid.");
        using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="INSERT INTO ai_pricing_snapshots VALUES($id,$provider,$model,$effective,$ohit,$omiss,$oout,$phit,$pmiss,$pout,'USD',$source,$json,$now);";
        cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$provider",provider);cmd.Parameters.AddWithValue("$model",model);cmd.Parameters.AddWithValue("$effective",effectiveAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$ohit",oHit);cmd.Parameters.AddWithValue("$omiss",oMiss);cmd.Parameters.AddWithValue("$oout",oOut);cmd.Parameters.AddWithValue("$phit",pHit);cmd.Parameters.AddWithValue("$pmiss",pMiss);cmd.Parameters.AddWithValue("$pout",pOut);
        cmd.Parameters.AddWithValue("$source",source);cmd.Parameters.AddWithValue("$json",json);cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
    }

    private (string Id,decimal OffPeakHit,decimal OffPeakMiss,decimal OffPeakOutput,decimal PeakHit,decimal PeakMiss,decimal PeakOutput)? ResolvePricing(string provider,string model,DateTimeOffset at)
    {
        using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT id,cache_hit_usd_per_million,cache_miss_usd_per_million,output_usd_per_million,peak_cache_hit_usd_per_million,peak_cache_miss_usd_per_million,peak_output_usd_per_million FROM ai_pricing_snapshots WHERE provider=$provider AND model=$model AND effective_from_utc<=$at ORDER BY effective_from_utc DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$provider",provider);cmd.Parameters.AddWithValue("$model",model);cmd.Parameters.AddWithValue("$at",at.ToUniversalTime().ToString("O")); using var r=cmd.ExecuteReader();
        if(!r.Read()||Enumerable.Range(1,6).Any(r.IsDBNull))return null;
        return(r.GetString(0),(decimal)r.GetDouble(1),(decimal)r.GetDouble(2),(decimal)r.GetDouble(3),(decimal)r.GetDouble(4),(decimal)r.GetDouble(5),(decimal)r.GetDouble(6));
    }

    public string Dashboard()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(CASE WHEN date(timestamp_utc,'localtime')=date('now','localtime') THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN strftime('%Y-%m',timestamp_utc,'localtime')=strftime('%Y-%m','now','localtime') THEN 1 ELSE 0 END),0),COUNT(*),COALESCE(SUM(input_tokens),0),COALESCE(SUM(output_tokens),0),COALESCE(SUM(CASE WHEN date(timestamp_utc,'localtime')=date('now','localtime') THEN calculated_cost_usd ELSE 0 END),0),COALESCE(SUM(CASE WHEN date(timestamp_utc,'localtime')=date('now','localtime') THEN calculated_cost_sar ELSE 0 END),0),COALESCE(SUM(CASE WHEN strftime('%Y-%m',timestamp_utc,'localtime')=strftime('%Y-%m','now','localtime') THEN calculated_cost_usd ELSE 0 END),0),COALESCE(SUM(CASE WHEN strftime('%Y-%m',timestamp_utc,'localtime')=strftime('%Y-%m','now','localtime') THEN calculated_cost_sar ELSE 0 END),0),COALESCE(SUM(calculated_cost_usd),0),COALESCE(SUM(calculated_cost_sar),0) FROM ai_usage_ledger;";
        using var r=cmd.ExecuteReader(); r.Read();
        var headline=string.Format(CultureInfo.InvariantCulture,"Today: {0} requests • ${1:0.000000} USD / {2:0.000000} SAR\nThis month: {3} requests • ${4:0.000000} USD / {5:0.000000} SAR\nAll time: {6} requests • ${7:0.000000} USD / {8:0.000000} SAR\nTokens: {9} input / {10} output\nLocal API cost = 0 | Rates: {11}",r.GetInt64(0),r.GetDouble(5),r.GetDouble(6),r.GetInt64(1),r.GetDouble(7),r.GetDouble(8),r.GetInt64(2),r.GetDouble(9),r.GetDouble(10),r.GetInt64(3),r.GetInt64(4),PricingId);
        r.Close(); cmd.CommandText="SELECT provider,model,SUM(request_count),COALESCE(SUM(input_tokens),0)+COALESCE(SUM(output_tokens),0),COALESCE(SUM(calculated_cost_usd),0),COALESCE(SUM(calculated_cost_sar),0) FROM ai_usage_ledger GROUP BY provider,model ORDER BY provider,model;";
        using var groups=cmd.ExecuteReader(); var detail=new StringBuilder(headline);
        while(groups.Read()) detail.AppendLine().AppendFormat(CultureInfo.InvariantCulture,"{0}/{1}: {2} requests • {3} tokens • ${4:0.000000} / {5:0.000000} SAR",groups.GetString(0),groups.GetString(1),groups.GetInt64(2),groups.GetInt64(3),groups.GetDouble(4),groups.GetDouble(5));
        groups.Close(); cmd.CommandText="SELECT operation_type,SUM(request_count),COALESCE(SUM(audio_duration_seconds),0),COALESCE(SUM(calculated_cost_usd),0),COALESCE(SUM(calculated_cost_sar),0) FROM ai_usage_ledger GROUP BY operation_type ORDER BY operation_type;";
        using var operations=cmd.ExecuteReader();
        while(operations.Read()) detail.AppendLine().AppendFormat(CultureInfo.InvariantCulture,"{0}: {1} requests • {2}s audio • ${3:0.000000} USD / {4:0.000000} SAR",operations.GetString(0),operations.GetInt64(1),operations.GetInt64(2),operations.GetDouble(3),operations.GetDouble(4));
        return detail.ToString();
    }

    public string ExportMetadataCsv()
    {
        using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT timestamp_utc,meeting_id,report_id,provider,model,operation_type,request_id,input_tokens,cache_hit_tokens,cache_miss_tokens,output_tokens,reasoning_tokens,total_tokens,elapsed_ms,success,calculated_cost_usd,calculated_cost_sar,cost_quality,pricing_snapshot_id FROM ai_usage_ledger ORDER BY timestamp_utc;";
        using var r=cmd.ExecuteReader(); var b=new StringBuilder("timestamp_utc,meeting_id,report_id,provider,model,operation,request_id,input,cache_hit,cache_miss,output,reasoning,total,elapsed_ms,success,cost_usd,cost_sar,cost_quality,pricing_snapshot\r\n");
        while(r.Read()) b.AppendJoin(',',Enumerable.Range(0,r.FieldCount).Select(i=>Csv(r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)??""))).Append("\r\n"); return b.ToString();
    }
    public string SummaryForMeeting(string meetingId, string? reportId = null)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT operation_type,provider,model,SUM(request_count),COALESCE(SUM(audio_duration_seconds),0),COALESCE(SUM(input_tokens),0),COALESCE(SUM(cache_hit_tokens),0),COALESCE(SUM(cache_miss_tokens),0),COALESCE(SUM(output_tokens),0),COALESCE(SUM(total_tokens),0),COALESCE(SUM(elapsed_ms),0),COALESCE(SUM(calculated_cost_usd),0),COALESCE(SUM(calculated_cost_sar),0),GROUP_CONCAT(DISTINCT cost_quality),GROUP_CONCAT(DISTINCT pricing_snapshot_id) FROM ai_usage_ledger WHERE meeting_id=$meeting GROUP BY operation_type,provider,model ORDER BY operation_type,provider,model;";
        cmd.Parameters.AddWithValue("$meeting",meetingId); cmd.Parameters.AddWithValue("$report",(object?)reportId??DBNull.Value);
        using var r=cmd.ExecuteReader(); var b=new StringBuilder();
        decimal usdTotal = 0, sarTotal = 0;
        while(r.Read())
        {
            usdTotal += Convert.ToDecimal(r.GetDouble(11), CultureInfo.InvariantCulture); sarTotal += Convert.ToDecimal(r.GetDouble(12), CultureInfo.InvariantCulture);
            b.AppendFormat(CultureInfo.InvariantCulture,"{0}: {1}/{2} • requests/chunks {3} • audio {4}s • tokens in/cache hit/cache miss/out/total {5}/{6}/{7}/{8}/{9} • {10}ms • ${11:0.000000} USD / {12:0.000000} SAR • {13} • pricing {14}\n",r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetInt64(4),r.GetInt64(5),r.GetInt64(6),r.GetInt64(7),r.GetInt64(8),r.GetInt64(9),r.GetInt64(10),r.GetDouble(11),r.GetDouble(12),r.GetString(13),r.IsDBNull(14)?"n/a":r.GetString(14));
        }
        return b.Length==0?"No new cost — cached report or no AI request recorded.":"Meeting total: $"+usdTotal.ToString("0.000000",CultureInfo.InvariantCulture)+" USD / "+sarTotal.ToString("0.000000",CultureInfo.InvariantCulture)+" SAR\n"+b.ToString().TrimEnd();
    }
    private static void AddNullable(SqliteCommand c,string name,int? value)=>c.Parameters.AddWithValue(name,(object?)value??DBNull.Value);
    private static void EnsureColumn(SqliteConnection c, SqliteTransaction transaction, string table, string column, string type)
    {
        using var check = c.CreateCommand(); check.Transaction = transaction; check.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = check.ExecuteReader();
        while (reader.Read()) if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close(); using var alter = c.CreateCommand(); alter.Transaction = transaction; alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type};"; alter.ExecuteNonQuery();
    }
    private static string Csv(string v)=>"\""+v.Replace("\"","\"\"")+"\"";
}

public static class AiUsageContext
{
    private static readonly AsyncLocal<(string? MeetingId,string? ReportId,string Operation)> Slot=new();
    public static (string? MeetingId,string? ReportId,string Operation) Current=>Slot.Value;
    public static IDisposable Push(string? meetingId,string? reportId,string operation){var old=Slot.Value;Slot.Value=(meetingId,reportId,operation);return new Pop(()=>Slot.Value=old);}
    private sealed class Pop(Action restore):IDisposable{public void Dispose()=>restore();}
}
