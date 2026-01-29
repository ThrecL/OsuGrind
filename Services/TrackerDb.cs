using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using OsuGrind.Models;

namespace OsuGrind.Services;

public record TotalStats(int totalPlays, long totalTimeMs);
public record DailyAverageStats(double avgDailyAcc, double avgDailyPP);
public record AnalyticsSummary(int TotalPlays, long TotalDurationMs, double AvgPP, double AvgAccuracy, double AvgUR, double AvgKeyRatio, int PassCount, DateTime? LastPlayedUtc);

public class DailyStats
{
    public string Date { get; set; } = "";
    public int PlayCount { get; set; }
    public int PassCount { get; set; }
    public double TotalPP { get; set; }
    public double TotalAccuracy { get; set; }
    public long TotalDurationMs { get; set; }
    public double AvgUR { get; set; }
    public double AvgKeyRatio { get; set; }
    public double AvgPP => PlayCount > 0 ? TotalPP / PlayCount : 0;
    public double AvgAcc => PlayCount > 0 ? TotalAccuracy / PlayCount : 0;
}

public class TrackerDb : IDisposable
{
    private readonly string _dbPath;
    private static bool _initialized = false;
    private static readonly object _initLock = new object();

    private const string PlaysSelectQuery = """
        SELECT p.id, p.created_at_utc, p.outcome, p.duration_ms, p.beatmap, p.mods, p.stars, p.accuracy, p.score, p.combo, p.count300, p.count100, p.count50, p.misses, p.pp, p.notes, p.beatmap_hash, p.rank, p.hit_offsets, p.timeline, p.pp_timeline, p.aim_offsets, b.background_hash, p.ur, p.replay_file, p.replay_hash,
               b.title, b.artist, b.version, b.cs, b.ar, b.od, b.hp, b.bpm, p.cursor_offsets, p.map_path, p.hit_errors, p.key_ratio
        FROM plays p
        LEFT JOIN beatmaps b ON p.beatmap_hash = b.hash
        """;

    private bool _playsHasScoreId = false;

    public TrackerDb(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsuGrind", "osugrind.sqlite");
        lock (_initLock)
        {
            if (!_initialized)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_dbPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    EnsureTablesCreated().GetAwaiter().GetResult();
                    _initialized = true;
                }
                catch (Exception ex) { Console.WriteLine($"[DB] Init Error: {ex.Message}"); }
            }
        }
        
        // Detect schema features
        try {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(plays)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) {
                if (reader.GetString(1).Equals("score_id", StringComparison.OrdinalIgnoreCase)) _playsHasScoreId = true;
            }
        } catch { }
    }

    private SqliteConnection Open() { var conn = new SqliteConnection($"Data Source={_dbPath}"); conn.Open(); return conn; }

    private async Task EnsureTablesCreated()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS plays (id INTEGER PRIMARY KEY AUTOINCREMENT, created_at_utc TEXT NOT NULL, outcome TEXT NOT NULL, duration_ms INTEGER NOT NULL DEFAULT 0, beatmap TEXT NOT NULL, beatmap_hash TEXT, mods TEXT NOT NULL, stars REAL, accuracy REAL NOT NULL, score INTEGER NOT NULL, combo INTEGER NOT NULL, count300 INTEGER NOT NULL, count100 INTEGER NOT NULL, count50 INTEGER NOT NULL, misses INTEGER NOT NULL, pp REAL NOT NULL DEFAULT 0, rank TEXT NOT NULL DEFAULT '', hit_offsets TEXT NOT NULL DEFAULT '', timeline TEXT NOT NULL DEFAULT '', aim_offsets TEXT NOT NULL DEFAULT '', cursor_offsets TEXT NOT NULL DEFAULT '', replay_file TEXT NOT NULL DEFAULT '', replay_hash TEXT NOT NULL DEFAULT '', pp_timeline TEXT NOT NULL DEFAULT '', map_path TEXT NOT NULL DEFAULT '', ur REAL NOT NULL DEFAULT 0, hit_errors TEXT, notes TEXT NOT NULL DEFAULT '', key_press_avg REAL DEFAULT 0, key_ratio REAL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS beatmaps (hash TEXT PRIMARY KEY, title TEXT, artist TEXT, mapper TEXT, version TEXT, cs REAL, ar REAL, od REAL, hp REAL, bpm REAL, length_ms REAL, circles INTEGER, sliders INTEGER, spinners INTEGER, max_combo INTEGER, stars REAL, status TEXT, play_count INTEGER, pass_count INTEGER, last_played_utc TEXT, background_hash TEXT, osu_file_path TEXT);
        """;
        await cmd.ExecuteNonQueryAsync();

        // MIGRATION: If score_id exists from a previous version, ensure it doesn't cause NOT NULL failures
        // We can't easily drop NOT NULL in SQLite, but we can check if it exists.
        // Actually, the simplest fix is to always provide it in the INSERT if it exists,
        // but better yet, let's try to detect it.
        
        var cols = new[] {
            ("plays", "pp_timeline", "TEXT NOT NULL DEFAULT ''"), ("plays", "ur", "REAL NOT NULL DEFAULT 0"), ("plays", "hit_errors", "TEXT"), ("plays", "notes", "TEXT NOT NULL DEFAULT ''"), ("plays", "key_ratio", "REAL DEFAULT 0"), ("plays", "replay_hash", "TEXT NOT NULL DEFAULT ''"), ("plays", "map_path", "TEXT NOT NULL DEFAULT ''"), ("beatmaps", "osu_file_path", "TEXT"), ("beatmaps", "background_hash", "TEXT")
        };
        foreach (var (t, c, ty) in cols) await EnsureColumnExistsAsync(conn, t, c, ty);
    }

    private async Task EnsureColumnExistsAsync(SqliteConnection conn, string table, string column, string type)
    {
        bool exists = false;
        using (var cmd = conn.CreateCommand()) { cmd.CommandText = $"PRAGMA table_info({table});"; using var reader = await cmd.ExecuteReaderAsync(); while (await reader.ReadAsync()) if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) { exists = true; break; } }
        if (!exists) { using var cmd = conn.CreateCommand(); cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};"; try { await cmd.ExecuteNonQueryAsync(); } catch (Exception ex) { if (!ex.Message.Contains("duplicate column name")) DebugService.Error($"Failed to add column {column} to {table}: {ex.Message}", "Database"); } }
    }

    public async Task<long> InsertPlayAsync(PlayRow row)
    {
        using var conn = Open(); using var cmd = conn.CreateCommand();
        string cols = "created_at_utc, outcome, duration_ms, beatmap, beatmap_hash, mods, stars, accuracy, score, combo, count300, count100, count50, misses, pp, rank, hit_offsets, timeline, pp_timeline, aim_offsets, cursor_offsets, ur, replay_file, replay_hash, map_path, hit_errors, key_ratio, notes";
        string vals = "$created_at_utc, $outcome, $duration_ms, $beatmap, $beatmap_hash, $mods, $stars, $accuracy, $score, $combo, $count300, $count100, $count50, $misses, $pp, $rank, $hit_offsets, $timeline, $pp_timeline, $aim_offsets, $cursor_offsets, $ur, $replay_file, $replay_hash, $map_path, $hit_errors, $key_ratio, $notes";
        if (_playsHasScoreId) { cols = "score_id, " + cols; vals = "0, " + vals; }
        
        cmd.CommandText = $"INSERT INTO plays ({cols}) VALUES ({vals}); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$created_at_utc", row.CreatedAtUtc.Kind == DateTimeKind.Utc ? row.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture) : row.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); 
        cmd.Parameters.AddWithValue("$outcome", row.Outcome); cmd.Parameters.AddWithValue("$duration_ms", row.DurationMs); cmd.Parameters.AddWithValue("$beatmap", row.Beatmap); cmd.Parameters.AddWithValue("$beatmap_hash", row.BeatmapHash ?? (object)DBNull.Value); cmd.Parameters.AddWithValue("$mods", row.Mods); cmd.Parameters.AddWithValue("$stars", row.Stars ?? (object)DBNull.Value); cmd.Parameters.AddWithValue("$accuracy", row.Accuracy); cmd.Parameters.AddWithValue("$score", row.Score); cmd.Parameters.AddWithValue("$combo", row.Combo); cmd.Parameters.AddWithValue("$count300", row.Count300); cmd.Parameters.AddWithValue("$count100", row.Count100); cmd.Parameters.AddWithValue("$count50", row.Count50); cmd.Parameters.AddWithValue("$misses", row.Misses); cmd.Parameters.AddWithValue("$pp", row.PP); cmd.Parameters.AddWithValue("$rank", row.Rank ?? ""); cmd.Parameters.AddWithValue("$hit_offsets", row.HitOffsets); cmd.Parameters.AddWithValue("$timeline", row.TimelineJson); cmd.Parameters.AddWithValue("$pp_timeline", row.PpTimelineJson ?? ""); cmd.Parameters.AddWithValue("$aim_offsets", row.AimOffsetsJson); cmd.Parameters.AddWithValue("$cursor_offsets", row.CursorOffsetsJson); cmd.Parameters.AddWithValue("$ur", row.UR); cmd.Parameters.AddWithValue("$replay_file", row.ReplayFile ?? ""); cmd.Parameters.AddWithValue("$replay_hash", row.ReplayHash ?? ""); cmd.Parameters.AddWithValue("$map_path", row.MapPath ?? ""); cmd.Parameters.AddWithValue("$hit_errors", row.HitErrorsJson ?? (object)DBNull.Value); cmd.Parameters.AddWithValue("$key_ratio", row.KeyRatio); cmd.Parameters.AddWithValue("$notes", row.Notes ?? "");
        var result = await cmd.ExecuteScalarAsync(); return result != null ? (long)result : 0;
    }

    public async Task UpdatePlayAsync(PlayRow row)
    {
        using var conn = Open(); using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE plays SET ur = $ur, hit_errors = $hit_errors, key_ratio = $key_ratio, replay_file = $replay_file, replay_hash = $replay_hash WHERE id = $id";
        cmd.Parameters.AddWithValue("$ur", row.UR); cmd.Parameters.AddWithValue("$hit_errors", row.HitErrorsJson ?? (object)DBNull.Value); cmd.Parameters.AddWithValue("$key_ratio", row.KeyRatio); cmd.Parameters.AddWithValue("$replay_file", row.ReplayFile ?? ""); cmd.Parameters.AddWithValue("$replay_hash", row.ReplayHash ?? ""); cmd.Parameters.AddWithValue("$id", row.Id);
        await cmd.ExecuteNonQueryAsync();
    }

    private PlayRow MapPlayRow(SqliteDataReader reader)
    {
        try {
            var createdStr = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var created = DateTime.TryParse(createdStr, null, DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.UtcNow;
            return new PlayRow {
                Id = reader.GetInt64(0), CreatedAtUtc = created.Kind == DateTimeKind.Utc ? created : created.ToUniversalTime(),
                Outcome = reader.IsDBNull(2) ? "fail" : reader.GetString(2), DurationMs = reader.IsDBNull(3) ? 0 : reader.GetInt32(3), Beatmap = reader.IsDBNull(4) ? "Unknown" : reader.GetString(4), Mods = reader.IsDBNull(5) ? "NM" : reader.GetString(5), Stars = reader.IsDBNull(6) ? null : reader.GetDouble(6), Accuracy = reader.IsDBNull(7) ? 0 : reader.GetDouble(7), Score = reader.IsDBNull(8) ? 0 : reader.GetInt64(8), Combo = reader.IsDBNull(9) ? 0 : reader.GetInt32(9), Count300 = reader.IsDBNull(10) ? 0 : reader.GetInt32(10), Count100 = reader.IsDBNull(11) ? 0 : reader.GetInt32(11), Count50 = reader.IsDBNull(12) ? 0 : reader.GetInt32(12), Misses = reader.IsDBNull(13) ? 0 : reader.GetInt32(13), PP = reader.IsDBNull(14) ? 0 : reader.GetDouble(14), Notes = reader.IsDBNull(15) ? "" : reader.GetString(15), BeatmapHash = reader.IsDBNull(16) ? "" : reader.GetString(16), Rank = reader.IsDBNull(17) ? null : reader.GetString(17), HitOffsets = reader.IsDBNull(18) ? "" : reader.GetString(18), TimelineJson = reader.IsDBNull(19) ? "" : reader.GetString(19), PpTimelineJson = reader.IsDBNull(20) ? "" : reader.GetString(20), AimOffsetsJson = reader.IsDBNull(21) ? "" : reader.GetString(21), BackgroundPath = reader.IsDBNull(22) ? null : reader.GetString(22), UR = reader.IsDBNull(23) ? 0 : reader.GetDouble(23), ReplayFile = reader.IsDBNull(24) ? "" : reader.GetString(24), ReplayHash = reader.IsDBNull(25) ? "" : reader.GetString(25), Title = reader.IsDBNull(26) ? "" : reader.GetString(26), Artist = reader.IsDBNull(27) ? "" : reader.GetString(27), Difficulty = reader.IsDBNull(28) ? "" : reader.GetString(28), CS = reader.IsDBNull(29) ? null : reader.GetDouble(29), AR = reader.IsDBNull(30) ? null : reader.GetDouble(30), OD = reader.IsDBNull(31) ? null : reader.GetDouble(31), HP = reader.IsDBNull(32) ? null : reader.GetDouble(32), BPM = reader.IsDBNull(33) ? null : reader.GetDouble(33), CursorOffsetsJson = reader.IsDBNull(34) ? "" : reader.GetString(34), MapPath = reader.IsDBNull(35) ? "" : reader.GetString(35), HitErrorsJson = reader.IsDBNull(36) ? null : reader.GetString(36), KeyRatio = reader.IsDBNull(37) ? 0 : reader.GetDouble(37)
            };
        } catch { return new PlayRow { Beatmap = "Error Mapping Data" }; }
    }

    public async Task<List<PlayRow>> FetchRecentAsync(int limit) { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = $"{PlaysSelectQuery} WHERE p.score > 0 ORDER BY p.created_at_utc DESC LIMIT $limit"; cmd.Parameters.AddWithValue("$limit", limit); var list = new List<PlayRow>(); using var reader = await cmd.ExecuteReaderAsync(); while (await reader.ReadAsync()) list.Add(MapPlayRow(reader)); return list; }
    public async Task<List<PlayRow>> FetchPlaysByLocalDayAsync(string localDateYMD) { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = $"{PlaysSelectQuery} WHERE p.score > 0 AND date(p.created_at_utc, 'localtime') = $d ORDER BY p.created_at_utc DESC"; cmd.Parameters.AddWithValue("$d", localDateYMD); var list = new List<PlayRow>(); using var reader = await cmd.ExecuteReaderAsync(); while (await reader.ReadAsync()) list.Add(MapPlayRow(reader)); return list; }
    public async Task<PlayRow?> GetPlayAsync(long id) { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = $"{PlaysSelectQuery} WHERE p.id = $id LIMIT 1"; cmd.Parameters.AddWithValue("$id", id); using var reader = await cmd.ExecuteReaderAsync(); if (await reader.ReadAsync()) return MapPlayRow(reader); return null; }

    public async Task UpdatePlayReplayFileAsync(long id, string path) { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE plays SET replay_file = $path WHERE id = $id"; cmd.Parameters.AddWithValue("$path", path); cmd.Parameters.AddWithValue("$id", id); await cmd.ExecuteNonQueryAsync(); }
    public async Task UpdateCursorOffsetsAsync(long id, string json) { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE plays SET cursor_offsets = $json WHERE id = $id"; cmd.Parameters.AddWithValue("$json", json); cmd.Parameters.AddWithValue("$id", id); await cmd.ExecuteNonQueryAsync(); }
    public async Task UpdatePlayNotesAsync(long id, string notes) { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE plays SET notes = $notes WHERE id = $id"; cmd.Parameters.AddWithValue("$notes", notes); cmd.Parameters.AddWithValue("$id", id); await cmd.ExecuteNonQueryAsync(); }

    public async Task<List<object[]>?> GetPpTimelineAsync(long id) { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT pp_timeline FROM plays WHERE id = $id LIMIT 1"; cmd.Parameters.AddWithValue("$id", id); var result = await cmd.ExecuteScalarAsync(); if (result == null || result == DBNull.Value) return null; var json = (string)result; if (string.IsNullOrEmpty(json)) return null; try { using var doc = JsonDocument.Parse(json); var list = new List<object[]>(); foreach (var element in doc.RootElement.EnumerateArray()) { if (element.ValueKind == JsonValueKind.Number) list.Add(new object[] { element.GetDouble() }); else if (element.ValueKind == JsonValueKind.Array) { var innerList = new List<object>(); foreach (var sub in element.EnumerateArray()) { if (sub.ValueKind == JsonValueKind.Number) innerList.Add(sub.GetDouble()); else innerList.Add(sub.ToString()); } list.Add(innerList.ToArray()); } } return list; } catch { return null; } }

    public async Task<TotalStats> GetTotalStatsAsync() { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT COUNT(1), SUM(duration_ms) FROM plays"; using var reader = await cmd.ExecuteReaderAsync(); if (await reader.ReadAsync()) return new TotalStats(reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1)); return new TotalStats(0, 0); }
    public async Task<DailyAverageStats> GetDailyAverageStatsAsync() { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT AVG(accuracy), AVG(pp) FROM plays WHERE created_at_utc >= datetime('now', '-24 hours')"; using var reader = await cmd.ExecuteReaderAsync(); if (await reader.ReadAsync()) return new DailyAverageStats(reader.IsDBNull(0) ? 0 : reader.GetDouble(0), reader.IsDBNull(1) ? 0 : reader.GetDouble(1)); return new DailyAverageStats(0, 0); }
    public async Task<int> GetPlaysTodayCountAsync() { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM plays WHERE score > 0 AND date(created_at_utc, 'localtime') = date('now', 'localtime')"; var result = await cmd.ExecuteScalarAsync(); return Convert.ToInt32(result ?? 0); }

    public async Task<GoalProgress> GetTodayGoalProgressAsync(double starThreshold) { 
        using var conn = Open(); using var cmd = conn.CreateCommand(); 
        cmd.CommandText = "SELECT COUNT(*), SUM(count300 + count100 + count50 + misses), SUM(CASE WHEN stars >= $star THEN 1 ELSE 0 END), SUM(pp) FROM plays WHERE score > 0 AND date(created_at_utc, 'localtime') = date('now', 'localtime')"; 
        cmd.Parameters.AddWithValue("$star", starThreshold); 
        using var reader = await cmd.ExecuteReaderAsync(); 
        if (await reader.ReadAsync()) return new GoalProgress(reader.IsDBNull(0) ? 0 : reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2), reader.IsDBNull(3) ? 0 : reader.GetDouble(3)); 
        return new GoalProgress(0, 0, 0, 0); 
    }
    public record GoalProgress(int Plays, int Hits, int StarPlays, double TotalPP);

    public async Task<List<DailyStats>> GetHourlyStatsTodayAsync() 
    { 
        using var conn = Open(); 
        using var cmd = conn.CreateCommand(); 
        cmd.CommandText = "SELECT strftime('%H:00', created_at_utc, 'localtime') as h, COUNT(1), SUM(CASE WHEN outcome = 'pass' THEN 1 ELSE 0 END), SUM(pp), SUM(duration_ms), SUM(accuracy), AVG(ur), AVG(key_ratio) FROM plays WHERE score > 0 AND date(created_at_utc, 'localtime') = date('now', 'localtime') GROUP BY h ORDER BY h ASC"; 
        
        var results = new Dictionary<string, DailyStats>();
        for (int i = 0; i < 24; i++) results[$"{i:D2}:00"] = new DailyStats { Date = $"{i:D2}:00" };

        using var reader = await cmd.ExecuteReaderAsync(); 
        while (await reader.ReadAsync()) 
        {
            var h = reader.GetString(0);
            results[h] = new DailyStats { 
                Date = h, 
                PlayCount = reader.GetInt32(1), 
                PassCount = reader.GetInt32(2), 
                TotalPP = reader.IsDBNull(3) ? 0 : reader.GetDouble(3), 
                TotalDurationMs = reader.IsDBNull(4) ? 0 : reader.GetInt64(4), 
                TotalAccuracy = reader.IsDBNull(5) ? 0 : reader.GetDouble(5), 
                AvgUR = reader.IsDBNull(6) ? 0 : reader.GetDouble(6), 
                AvgKeyRatio = reader.IsDBNull(7) ? 0 : reader.GetDouble(7) 
            }; 
        }
        return results.Values.OrderBy(v => v.Date).ToList(); 
    }

    public async Task<int> GetPlayStreakAsync()
    {
        int targetPlays = SettingsManager.Current.GoalPlays;
        int targetHits = SettingsManager.Current.GoalHits;
        double targetStars = SettingsManager.Current.GoalStars;
        int targetPP = SettingsManager.Current.GoalPP;
        
        bool hasGoals = targetPlays > 0 || targetHits > 0 || targetStars > 0 || targetPP > 0;
        if (!hasGoals) return 0;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT date(created_at_utc, 'localtime') as d, COUNT(1) as plays, SUM(count300 + count100 + count50 + misses) as hits, SUM(CASE WHEN stars >= $starThreshold THEN 1 ELSE 0 END) as star_plays, SUM(pp) as total_pp FROM plays WHERE score > 0 GROUP BY d ORDER BY d DESC";
        cmd.Parameters.AddWithValue("$starThreshold", targetStars);

        var history = new Dictionary<string, (int Plays, int Hits, int StarPlays, double TotalPP)>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            history[reader.GetString(0)] = (
                reader.GetInt32(1), 
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2), 
                reader.IsDBNull(3) ? 0 : reader.GetInt32(3), 
                reader.IsDBNull(4) ? 0 : reader.GetDouble(4)
            );
        }

        bool IsGoalMet(string dateStr) {
            if (!history.TryGetValue(dateStr, out var stats)) return false;
            if (targetPlays > 0 && stats.Plays < targetPlays) return false;
            if (targetHits > 0 && stats.Hits < targetHits) return false;
            if (targetStars > 0 && stats.StarPlays < 1) return false;
            if (targetPP > 0 && stats.TotalPP < targetPP) return false;
            return true;
        }

        DateTime today = DateTime.Now.Date;
        string todayStr = today.ToString("yyyy-MM-dd");
        
        int streak = 0;
        if (IsGoalMet(todayStr)) streak = 1;

        DateTime check = today.AddDays(-1);
        while (IsGoalMet(check.ToString("yyyy-MM-dd")))
        {
            streak++;
            check = check.AddDays(-1);
        }
        return streak;
    }

    public async Task<List<PlayRow>> GetTopPlaysAsync(int limit = 100) { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = $"{PlaysSelectQuery} WHERE p.score > 0 ORDER BY p.pp DESC LIMIT $limit"; cmd.Parameters.AddWithValue("$limit", limit); var list = new List<PlayRow>(); using var reader = await cmd.ExecuteReaderAsync(); while (await reader.ReadAsync()) list.Add(MapPlayRow(reader)); return list; }

    public async Task<AnalyticsSummary> GetAnalyticsSummaryAsync(int days)
    {
        using var conn = Open(); using var cmd = conn.CreateCommand(); string timeFilter = ""; if (days == -1) timeFilter = "AND date(created_at_utc, 'localtime') = date('now', 'localtime')"; else if (days > 0) timeFilter = $"AND created_at_utc >= datetime('now', '-{days} days')";
        cmd.CommandText = $"SELECT COUNT(1), SUM(duration_ms), AVG(pp), AVG(accuracy), AVG(ur), AVG(key_ratio), SUM(CASE WHEN outcome = 'pass' THEN 1 ELSE 0 END), MAX(created_at_utc) FROM plays WHERE score > 0 {timeFilter}";
        using var reader = await cmd.ExecuteReaderAsync(); if (await reader.ReadAsync()) return new AnalyticsSummary(reader.IsDBNull(0) ? 0 : reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1), reader.IsDBNull(2) ? 0 : reader.GetDouble(2), reader.IsDBNull(3) ? 0 : reader.GetDouble(3), reader.IsDBNull(4) ? 0 : reader.GetDouble(4), reader.IsDBNull(5) ? 0 : reader.GetDouble(5), reader.IsDBNull(6) ? 0 : reader.GetInt32(6), reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7), null, DateTimeStyles.RoundtripKind));
        return new AnalyticsSummary(0, 0, 0, 0, 0, 0, 0, null);
    }

    public async Task<List<DailyStats>> GetDailyStatsAsync(int days)
    {
        using var conn = Open(); using var cmd = conn.CreateCommand(); string timeFilter = ""; if (days == -1) timeFilter = "AND date(created_at_utc, 'localtime') = date('now', 'localtime')"; else if (days > 0) timeFilter = $"AND created_at_utc >= datetime('now', '-{days} days')";
        cmd.CommandText = $"SELECT date(created_at_utc, 'localtime') as d, COUNT(1), SUM(CASE WHEN outcome = 'pass' THEN 1 ELSE 0 END), SUM(pp), SUM(duration_ms), SUM(accuracy), AVG(ur), AVG(key_ratio) FROM plays WHERE score > 0 {timeFilter} GROUP BY d ORDER BY d ASC";
        var list = new List<DailyStats>(); using var reader = await cmd.ExecuteReaderAsync(); while (await reader.ReadAsync()) list.Add(new DailyStats { Date = reader.GetString(0), PlayCount = reader.GetInt32(1), PassCount = reader.GetInt32(2), TotalPP = reader.IsDBNull(3) ? 0 : reader.GetDouble(3), TotalDurationMs = reader.IsDBNull(4) ? 0 : reader.GetInt64(4), TotalAccuracy = reader.IsDBNull(5) ? 0 : reader.GetDouble(5), AvgUR = reader.IsDBNull(6) ? 0 : reader.GetDouble(6), AvgKeyRatio = reader.IsDBNull(7) ? 0 : reader.GetDouble(7) }); return list; }

    public async Task<Dictionary<string, int>> GetMonthPlayCountsAsync(int year, int month) { using var conn = Open(); using var cmd = conn.CreateCommand(); string monthStr = $"{year}-{month:D2}-%"; cmd.CommandText = "SELECT date(created_at_utc, 'localtime') as d, COUNT(*) FROM plays WHERE score > 0 AND date(created_at_utc, 'localtime') LIKE $m GROUP BY d"; cmd.Parameters.AddWithValue("$m", monthStr); var dict = new Dictionary<string, int>(); using var reader = await cmd.ExecuteReaderAsync(); while (await reader.ReadAsync()) { var date = reader.IsDBNull(0) ? "" : reader.GetString(0); if (!string.IsNullOrEmpty(date)) dict[date] = reader.GetInt32(1); } return dict; }

    public async Task MigrateAsync() { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "DELETE FROM plays WHERE score <= 0; DROP INDEX IF EXISTS idx_plays_created_map; DROP INDEX IF EXISTS idx_plays_created_hash_score; DROP INDEX IF EXISTS idx_plays_created_hash; DROP INDEX IF EXISTS idx_plays_dedup; DELETE FROM plays WHERE id NOT IN (SELECT MIN(id) FROM plays GROUP BY created_at_utc, beatmap_hash, score); CREATE UNIQUE INDEX IF NOT EXISTS idx_plays_dedup ON plays(created_at_utc, beatmap_hash, score);"; await cmd.ExecuteNonQueryAsync(); }

    public async Task<List<BeatmapRow>> GetBeatmapsAsync() 
    { 
        using var conn = Open(); 
        using var cmd = conn.CreateCommand(); 
        cmd.CommandText = "SELECT b.hash, b.title, b.artist, b.mapper, b.version, b.cs, b.ar, b.od, b.hp, b.bpm, b.length_ms, b.stars, b.status, b.background_hash, b.circles, b.sliders, b.spinners, b.max_combo, b.play_count, b.pass_count, b.last_played_utc, MAX(p.pp) as highest_pp FROM beatmaps b LEFT JOIN plays p ON b.hash = p.beatmap_hash GROUP BY b.hash"; 
        var list = new List<BeatmapRow>(); 
        using var reader = await cmd.ExecuteReaderAsync(); 
        while (await reader.ReadAsync()) 
        {
            DateTime? lastPlayed = null;
            if (!reader.IsDBNull(20)) { DateTime.TryParse(reader.GetString(20), null, DateTimeStyles.RoundtripKind, out var lp); lastPlayed = lp; }
            list.Add(new BeatmapRow { 
                Hash = reader.GetString(0), Title = reader.IsDBNull(1) ? "" : reader.GetString(1), Artist = reader.IsDBNull(2) ? "" : reader.GetString(2), Mapper = reader.IsDBNull(3) ? "" : reader.GetString(3), Version = reader.IsDBNull(4) ? "" : reader.GetString(4), 
                CS = reader.IsDBNull(5) ? 0 : reader.GetDouble(5), AR = reader.IsDBNull(6) ? 0 : reader.GetDouble(6), OD = reader.IsDBNull(7) ? 0 : reader.GetDouble(7), HP = reader.IsDBNull(8) ? 0 : reader.GetDouble(8), BPM = reader.IsDBNull(9) ? 0 : reader.GetDouble(9), 
                LengthMs = reader.IsDBNull(10) ? 0 : reader.GetDouble(10), Stars = reader.IsDBNull(11) ? 0 : reader.GetDouble(11), Status = reader.IsDBNull(12) ? "" : reader.GetString(12), BackgroundHash = reader.IsDBNull(13) ? null : reader.GetString(13), 
                Circles = reader.IsDBNull(14) ? 0 : reader.GetInt32(14), Sliders = reader.IsDBNull(15) ? 0 : reader.GetInt32(15), Spinners = reader.IsDBNull(16) ? 0 : reader.GetInt32(16), MaxCombo = reader.IsDBNull(17) ? 0 : reader.GetInt32(17), 
                PlayCount = reader.IsDBNull(18) ? 0 : reader.GetInt32(18), PassCount = reader.IsDBNull(19) ? 0 : reader.GetInt32(19), LastPlayedUtc = lastPlayed,
                HighestPP = reader.IsDBNull(21) ? 0 : reader.GetDouble(21)
            }); 
        }
        return list; 
    }

    public async Task InsertOrUpdateBeatmapAsync(BeatmapRow b)
    {
        using var conn = Open(); using var cmd = conn.CreateCommand(); string updateClause = "title=excluded.title, artist=excluded.artist, mapper=excluded.mapper, version=excluded.version, cs=excluded.cs, ar=excluded.ar, od=excluded.od, hp=excluded.hp, bpm=excluded.bpm, length_ms=excluded.length_ms, stars=excluded.stars, status=excluded.status, background_hash=excluded.background_hash, circles=excluded.circles, sliders=excluded.sliders, spinners=excluded.spinners, max_combo=excluded.max_combo, play_count=excluded.play_count, pass_count=excluded.pass_count, last_played_utc=excluded.last_played_utc"; if (!string.IsNullOrEmpty(b.OsuFilePath)) updateClause += ", osu_file_path=excluded.osu_file_path";
        cmd.CommandText = $"INSERT INTO beatmaps (hash, title, artist, mapper, version, cs, ar, od, hp, bpm, length_ms, stars, status, background_hash, circles, sliders, spinners, max_combo, play_count, pass_count, last_played_utc, osu_file_path) VALUES ($hash, $title, $artist, $mapper, $version, $cs, $ar, $od, $hp, $bpm, $len, $stars, $status, $bg, $circles, $sliders, $spinners, $max_combo, $pc, $passc, $lp, $osupath) ON CONFLICT(hash) DO UPDATE SET {updateClause};";
        cmd.Parameters.AddWithValue("$hash", b.Hash); cmd.Parameters.AddWithValue("$title", b.Title); cmd.Parameters.AddWithValue("$artist", b.Artist); cmd.Parameters.AddWithValue("$mapper", b.Mapper); cmd.Parameters.AddWithValue("$version", b.Version); cmd.Parameters.AddWithValue("$cs", b.CS); cmd.Parameters.AddWithValue("$ar", b.AR); cmd.Parameters.AddWithValue("$od", b.OD); cmd.Parameters.AddWithValue("$hp", b.HP); cmd.Parameters.AddWithValue("$bpm", b.BPM); cmd.Parameters.AddWithValue("$len", b.LengthMs); cmd.Parameters.AddWithValue("$stars", b.Stars); cmd.Parameters.AddWithValue("$status", b.Status); cmd.Parameters.AddWithValue("$bg", b.BackgroundHash ?? (object)DBNull.Value); cmd.Parameters.AddWithValue("$circles", b.Circles); cmd.Parameters.AddWithValue("$sliders", b.Sliders); cmd.Parameters.AddWithValue("$spinners", b.Spinners); cmd.Parameters.AddWithValue("$max_combo", b.MaxCombo); cmd.Parameters.AddWithValue("$pc", b.PlayCount); cmd.Parameters.AddWithValue("$passc", b.PassCount); cmd.Parameters.AddWithValue("$lp", b.LastPlayedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value); cmd.Parameters.AddWithValue("$osupath", b.OsuFilePath ?? (object)DBNull.Value); await cmd.ExecuteNonQueryAsync();
    }

    public async Task InsertOrUpdateBeatmapBatchAsync(IEnumerable<BeatmapRow> beatmaps)
    {
        using var conn = Open();
        using var transaction = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        
        string updateClause = "title=excluded.title, artist=excluded.artist, mapper=excluded.mapper, version=excluded.version, cs=excluded.cs, ar=excluded.ar, od=excluded.od, hp=excluded.hp, bpm=excluded.bpm, length_ms=excluded.length_ms, stars=excluded.stars, status=excluded.status, background_hash=excluded.background_hash, circles=excluded.circles, sliders=excluded.sliders, spinners=excluded.spinners, max_combo=excluded.max_combo, play_count=excluded.play_count, pass_count=excluded.pass_count, last_played_utc=excluded.last_played_utc, osu_file_path=excluded.osu_file_path";
        cmd.CommandText = $"INSERT INTO beatmaps (hash, title, artist, mapper, version, cs, ar, od, hp, bpm, length_ms, stars, status, background_hash, circles, sliders, spinners, max_combo, play_count, pass_count, last_played_utc, osu_file_path) VALUES ($hash, $title, $artist, $mapper, $version, $cs, $ar, $od, $hp, $bpm, $len, $stars, $status, $bg, $circles, $sliders, $spinners, $max_combo, $pc, $passc, $lp, $osupath) ON CONFLICT(hash) DO UPDATE SET {updateClause};";
        
        var pHash = cmd.Parameters.Add("$hash", SqliteType.Text);
        var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
        var pArtist = cmd.Parameters.Add("$artist", SqliteType.Text);
        var pMapper = cmd.Parameters.Add("$mapper", SqliteType.Text);
        var pVersion = cmd.Parameters.Add("$version", SqliteType.Text);
        var pCS = cmd.Parameters.Add("$cs", SqliteType.Real);
        var pAR = cmd.Parameters.Add("$ar", SqliteType.Real);
        var pOD = cmd.Parameters.Add("$od", SqliteType.Real);
        var pHP = cmd.Parameters.Add("$hp", SqliteType.Real);
        var pBPM = cmd.Parameters.Add("$bpm", SqliteType.Real);
        var pLen = cmd.Parameters.Add("$len", SqliteType.Real);
        var pStars = cmd.Parameters.Add("$stars", SqliteType.Real);
        var pStatus = cmd.Parameters.Add("$status", SqliteType.Text);
        var pBG = cmd.Parameters.Add("$bg", SqliteType.Text);
        var pCircles = cmd.Parameters.Add("$circles", SqliteType.Integer);
        var pSliders = cmd.Parameters.Add("$sliders", SqliteType.Integer);
        var pSpinners = cmd.Parameters.Add("$spinners", SqliteType.Integer);
        var pMaxCombo = cmd.Parameters.Add("$max_combo", SqliteType.Integer);
        var pPC = cmd.Parameters.Add("$pc", SqliteType.Integer);
        var pPassC = cmd.Parameters.Add("$passc", SqliteType.Integer);
        var pLP = cmd.Parameters.Add("$lp", SqliteType.Text);
        var pOsuPath = cmd.Parameters.Add("$osupath", SqliteType.Text);

        foreach (var b in beatmaps)
        {
            pHash.Value = b.Hash;
            pTitle.Value = b.Title;
            pArtist.Value = b.Artist;
            pMapper.Value = b.Mapper;
            pVersion.Value = b.Version;
            pCS.Value = b.CS;
            pAR.Value = b.AR;
            pOD.Value = b.OD;
            pHP.Value = b.HP;
            pBPM.Value = b.BPM;
            pLen.Value = b.LengthMs;
            pStars.Value = b.Stars;
            pStatus.Value = b.Status;
            pBG.Value = b.BackgroundHash ?? (object)DBNull.Value;
            pCircles.Value = b.Circles;
            pSliders.Value = b.Sliders;
            pSpinners.Value = b.Spinners;
            pMaxCombo.Value = b.MaxCombo;
            pPC.Value = b.PlayCount;
            pPassC.Value = b.PassCount;
            pLP.Value = b.LastPlayedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value;
            pOsuPath.Value = b.OsuFilePath ?? (object)DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task InsertPlaysBatchAsync(IEnumerable<PlayRow> plays)
    {
        using var conn = Open(); using var transaction = conn.BeginTransaction(); using var cmd = conn.CreateCommand(); cmd.Transaction = transaction; 
        string cols = "created_at_utc, outcome, duration_ms, beatmap, beatmap_hash, mods, stars, accuracy, score, combo, count300, count100, count50, misses, pp, rank, hit_offsets, timeline, pp_timeline, aim_offsets, cursor_offsets, ur, replay_file, replay_hash, hit_errors, key_ratio, notes";
        string vals = "$created_at_utc, $outcome, $duration_ms, $beatmap, $beatmap_hash, $mods, $stars, $accuracy, $score, $combo, $count300, $count100, $count50, $misses, $pp, $rank, $hit_offsets, $timeline, $pp_timeline, $aim_offsets, $cursor_offsets, $ur, $replay_file, $replay_hash, $hit_errors, $key_ratio, $notes";
        if (_playsHasScoreId) { cols = "score_id, " + cols; vals = "0, " + vals; }
        cmd.CommandText = $"INSERT OR IGNORE INTO plays ({cols}) VALUES ({vals});";
        var pCreated = cmd.Parameters.Add("$created_at_utc", SqliteType.Text); var pOutcome = cmd.Parameters.Add("$outcome", SqliteType.Text); var pDuration = cmd.Parameters.Add("$duration_ms", SqliteType.Integer); var pBeatmap = cmd.Parameters.Add("$beatmap", SqliteType.Text); var pHash = cmd.Parameters.Add("$beatmap_hash", SqliteType.Text); var pMods = cmd.Parameters.Add("$mods", SqliteType.Text); var pStars = cmd.Parameters.Add("$stars", SqliteType.Real); var pAcc = cmd.Parameters.Add("$accuracy", SqliteType.Real); var pScore = cmd.Parameters.Add("$score", SqliteType.Integer); var pCombo = cmd.Parameters.Add("$combo", SqliteType.Integer); var p300 = cmd.Parameters.Add("$count300", SqliteType.Integer); var p100 = cmd.Parameters.Add("$count100", SqliteType.Integer); var p50 = cmd.Parameters.Add("$count50", SqliteType.Integer); var pMiss = cmd.Parameters.Add("$misses", SqliteType.Integer); var pPP = cmd.Parameters.Add("$pp", SqliteType.Real); var pRank = cmd.Parameters.Add("$rank", SqliteType.Text); var pHit = cmd.Parameters.Add("$hit_offsets", SqliteType.Text); var pTime = cmd.Parameters.Add("$timeline", SqliteType.Text); var pPpTimeline = cmd.Parameters.Add("$pp_timeline", SqliteType.Text); var pAimOffsets = cmd.Parameters.Add("$aim_offsets", SqliteType.Text); var pCursorOffsets = cmd.Parameters.Add("$cursor_offsets", SqliteType.Text); var pUR = cmd.Parameters.Add("$ur", SqliteType.Real); var pReplay = cmd.Parameters.Add("$replay_file", SqliteType.Text); var pReplayHash = cmd.Parameters.Add("$replay_hash", SqliteType.Text); var pHitErrors = cmd.Parameters.Add("$hit_errors", SqliteType.Text); var pKeyRatio = cmd.Parameters.Add("$key_ratio", SqliteType.Real); var pNotes = cmd.Parameters.Add("$notes", SqliteType.Text);
        foreach (var row in plays) { pCreated.Value = row.CreatedAtUtc.Kind == DateTimeKind.Utc ? row.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture) : row.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture); pOutcome.Value = row.Outcome; pDuration.Value = row.DurationMs; pBeatmap.Value = row.Beatmap; pHash.Value = row.BeatmapHash ?? (object)DBNull.Value; pMods.Value = row.Mods; pStars.Value = row.Stars ?? (object)DBNull.Value; pAcc.Value = row.Accuracy; pScore.Value = row.Score; pCombo.Value = row.Combo; p300.Value = row.Count300; p100.Value = row.Count100; p50.Value = row.Count50; pMiss.Value = row.Misses; pPP.Value = row.PP; pRank.Value = row.Rank ?? ""; pHit.Value = row.HitOffsets; pTime.Value = row.TimelineJson; pPpTimeline.Value = row.PpTimelineJson ?? ""; pAimOffsets.Value = row.AimOffsetsJson; pCursorOffsets.Value = row.CursorOffsetsJson; pUR.Value = row.UR; pReplay.Value = row.ReplayFile ?? ""; pReplayHash.Value = row.ReplayHash ?? ""; pHitErrors.Value = row.HitErrorsJson ?? (object)DBNull.Value; pKeyRatio.Value = row.KeyRatio; pNotes.Value = row.Notes ?? ""; await cmd.ExecuteNonQueryAsync(); }
        await transaction.CommitAsync();
    }

    public async Task<HashSet<string>> GetExistingScoreSignaturesAsync() { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT beatmap_hash, score, created_at_utc FROM plays"; var signatures = new HashSet<string>(); using var reader = await cmd.ExecuteReaderAsync(); while (await reader.ReadAsync()) signatures.Add($"{(reader.IsDBNull(0) ? "" : reader.GetString(0))}|{reader.GetInt64(1)}|{(reader.IsDBNull(2) ? "" : reader.GetString(2))}"); return signatures; }
    public async Task DeletePlayAsync(long id) { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "DELETE FROM plays WHERE id = $id"; cmd.Parameters.AddWithValue("$id", id); await cmd.ExecuteNonQueryAsync(); }
    public async Task DeleteAllScoresAsync() { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "DELETE FROM plays"; await cmd.ExecuteNonQueryAsync(); }
    public async Task DeleteAllBeatmapsAsync() { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "DELETE FROM beatmaps"; await cmd.ExecuteNonQueryAsync(); }
    public async Task<string?> GetMapHashByMetadataAsync(string artist, string title) { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT hash FROM beatmaps WHERE artist = $a AND title = $t LIMIT 1"; cmd.Parameters.AddWithValue("$a", artist); cmd.Parameters.AddWithValue("$t", title); var result = await cmd.ExecuteScalarAsync(); return result == DBNull.Value || result == null ? null : (string)result; }
    public async Task<HashSet<string>> GetExistingBeatmapHashesAsync() { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT hash FROM beatmaps"; var hashes = new HashSet<string>(); using var reader = await cmd.ExecuteReaderAsync(); while (await reader.ReadAsync()) hashes.Add(reader.GetString(0)); return hashes; }
    public async Task<object> DumpPlaysAsync() { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT created_at_utc, score, beatmap, id FROM plays LIMIT 50"; var list = new List<object>(); using var reader = await cmd.ExecuteReaderAsync(); while (await reader.ReadAsync()) list.Add(new { created = reader.GetString(0), score = reader.GetInt64(1), beatmap = reader.GetString(2), id = reader.GetInt64(3) }); return list; }
    public async Task<string?> GetPpTimelineJsonAsync(long id) { using var conn = Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT pp_timeline FROM plays WHERE id = $id LIMIT 1"; cmd.Parameters.AddWithValue("$id", id); var result = await cmd.ExecuteScalarAsync(); return result == null || result == DBNull.Value ? null : (string)result; }

    public void Dispose() { }
}
