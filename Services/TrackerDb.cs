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

public class TrackerDb
{
    private readonly string _dbPath;

    public TrackerDb(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OsuGrind",
            "osugrind.sqlite"
        );
        EnsureTablesCreated().Wait();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private async Task EnsureTablesCreated()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS plays (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          score_id INTEGER NOT NULL DEFAULT 0,
          created_at_utc TEXT NOT NULL,
          outcome TEXT NOT NULL,
          duration_ms INTEGER NOT NULL,
          beatmap TEXT NOT NULL,
          beatmap_hash TEXT,
          mods TEXT NOT NULL,
          stars REAL,
          accuracy REAL NOT NULL,
          score INTEGER NOT NULL,
          combo INTEGER NOT NULL,
          count300 INTEGER NOT NULL,
          count100 INTEGER NOT NULL,
          count50 INTEGER NOT NULL,
          misses INTEGER NOT NULL,
          pp REAL NOT NULL DEFAULT 0,
          rank TEXT NOT NULL DEFAULT '',
          hit_offsets TEXT NOT NULL DEFAULT '',
          timeline TEXT NOT NULL DEFAULT '',
          aim_offsets TEXT NOT NULL DEFAULT '',
          cursor_offsets TEXT NOT NULL DEFAULT '',
          replay_file TEXT NOT NULL DEFAULT '',
           replay_hash TEXT NOT NULL DEFAULT '',
           pp_timeline TEXT NOT NULL DEFAULT '',
           map_path TEXT NOT NULL DEFAULT ''
         );
        CREATE TABLE IF NOT EXISTS beatmaps (
          hash TEXT PRIMARY KEY,
          title TEXT,
          artist TEXT,
          mapper TEXT,
          version TEXT,
          cs REAL,
          ar REAL,
          od REAL,
          hp REAL,
          bpm REAL,
          length_ms REAL,
          circles INTEGER,
          sliders INTEGER,
          spinners INTEGER,
          max_combo INTEGER,
          stars REAL,
          status TEXT,
          play_count INTEGER,
          pass_count INTEGER,
          last_played_utc TEXT,
          background_hash TEXT
        );
        """;
        await cmd.ExecuteNonQueryAsync();

        // Migrations
        await EnsureColumnAsync(conn, "plays", "pp_timeline", "ALTER TABLE plays ADD COLUMN pp_timeline TEXT NOT NULL DEFAULT '';");
        await EnsureColumnAsync(conn, "plays", "duration_ms", "ALTER TABLE plays ADD COLUMN duration_ms INTEGER NOT NULL DEFAULT 0;");
        await EnsureColumnAsync(conn, "plays", "stars", "ALTER TABLE plays ADD COLUMN stars REAL;");
        await EnsureColumnAsync(conn, "plays", "pp", "ALTER TABLE plays ADD COLUMN pp REAL NOT NULL DEFAULT 0;");
        await EnsureColumnAsync(conn, "plays", "beatmap_hash", "ALTER TABLE plays ADD COLUMN beatmap_hash TEXT;");
        await EnsureColumnAsync(conn, "plays", "rank", "ALTER TABLE plays ADD COLUMN rank TEXT NOT NULL DEFAULT '';");
        await EnsureColumnAsync(conn, "plays", "hit_offsets", "ALTER TABLE plays ADD COLUMN hit_offsets TEXT NOT NULL DEFAULT '';");
        await EnsureColumnAsync(conn, "plays", "map_path", "ALTER TABLE plays ADD COLUMN map_path TEXT NOT NULL DEFAULT '';");
        await EnsureColumnAsync(conn, "plays", "timeline", "ALTER TABLE plays ADD COLUMN timeline TEXT NOT NULL DEFAULT '';");
        await EnsureColumnAsync(conn, "plays", "aim_offsets", "ALTER TABLE plays ADD COLUMN aim_offsets TEXT NOT NULL DEFAULT '';");
        await EnsureColumnAsync(conn, "plays", "cursor_offsets", "ALTER TABLE plays ADD COLUMN cursor_offsets TEXT NOT NULL DEFAULT '';");
        await EnsureColumnAsync(conn, "plays", "replay_file", "ALTER TABLE plays ADD COLUMN replay_file TEXT NOT NULL DEFAULT '';");

        await EnsureColumnAsync(conn, "plays", "replay_hash", "ALTER TABLE plays ADD COLUMN replay_hash TEXT NOT NULL DEFAULT '';");
        await EnsureColumnAsync(conn, "beatmaps", "background_hash", "ALTER TABLE beatmaps ADD COLUMN background_hash TEXT;");
        await EnsureColumnAsync(conn, "beatmaps", "osu_file_path", "ALTER TABLE beatmaps ADD COLUMN osu_file_path TEXT;");
        await EnsureColumnAsync(conn, "beatmaps", "play_count", "ALTER TABLE beatmaps ADD COLUMN play_count INTEGER;");
        await EnsureColumnAsync(conn, "beatmaps", "pass_count", "ALTER TABLE beatmaps ADD COLUMN pass_count INTEGER;");
        await EnsureColumnAsync(conn, "beatmaps", "last_played_utc", "ALTER TABLE beatmaps ADD COLUMN last_played_utc TEXT;");
    }

    private async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string ddl)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        }
        reader.Close();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InsertPlayAsync(PlayRow row)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
        INSERT OR IGNORE INTO plays (score_id, created_at_utc, outcome, duration_ms, beatmap, beatmap_hash, mods, stars, accuracy, score, combo, count300, count100, count50, misses, pp, rank, hit_offsets, timeline, pp_timeline, aim_offsets, cursor_offsets, ur, replay_file, replay_hash, map_path)
        VALUES ($score_id, $created_at_utc, $outcome, $duration_ms, $beatmap, $beatmap_hash, $mods, $stars, $accuracy, $score, $combo, $count300, $count100, $count50, $misses, $pp, $rank, $hit_offsets, $timeline, $pp_timeline, $aim_offsets, $cursor_offsets, $ur, $replay_file, $replay_hash, $map_path);
        """;

        cmd.Parameters.AddWithValue("$score_id", row.ScoreId);
        cmd.Parameters.AddWithValue("$created_at_utc", row.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$outcome", row.Outcome);
        cmd.Parameters.AddWithValue("$duration_ms", row.DurationMs);
        cmd.Parameters.AddWithValue("$beatmap", row.Beatmap);
        cmd.Parameters.AddWithValue("$beatmap_hash", row.BeatmapHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$mods", row.Mods);
        cmd.Parameters.AddWithValue("$stars", row.Stars ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$accuracy", row.Accuracy);
        cmd.Parameters.AddWithValue("$score", row.Score);
        cmd.Parameters.AddWithValue("$combo", row.Combo);
        cmd.Parameters.AddWithValue("$count300", row.Count300);
        cmd.Parameters.AddWithValue("$count100", row.Count100);
        cmd.Parameters.AddWithValue("$count50", row.Count50);
        cmd.Parameters.AddWithValue("$misses", row.Misses);
        cmd.Parameters.AddWithValue("$pp", row.PP);
        cmd.Parameters.AddWithValue("$rank", row.Rank ?? "");
        cmd.Parameters.AddWithValue("$hit_offsets", row.HitOffsets);
        cmd.Parameters.AddWithValue("$timeline", row.TimelineJson);
        cmd.Parameters.AddWithValue("$pp_timeline", row.PpTimelineJson);
        cmd.Parameters.AddWithValue("$aim_offsets", row.AimOffsetsJson);
        cmd.Parameters.AddWithValue("$cursor_offsets", row.CursorOffsetsJson);
        cmd.Parameters.AddWithValue("$ur", row.UR);
        cmd.Parameters.AddWithValue("$replay_file", row.ReplayFile ?? "");
        cmd.Parameters.AddWithValue("$replay_hash", row.ReplayHash ?? "");
        cmd.Parameters.AddWithValue("$map_path", row.MapPath ?? "");
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<PlayRow>> FetchRecentAsync(int limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{PlaysSelectQuery} ORDER BY p.created_at_utc DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<PlayRow>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapPlayRow(reader));
        return list;
    }

    public async Task<List<PlayRow>> FetchPlaysRangeAsync(DateTime startUtc, DateTime endUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{PlaysSelectQuery} WHERE p.created_at_utc >= $s AND p.created_at_utc < $e ORDER BY p.created_at_utc DESC";
        cmd.Parameters.AddWithValue("$s", startUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$e", endUtc.ToString("O", CultureInfo.InvariantCulture));
        var list = new List<PlayRow>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(MapPlayRow(reader));
        return list;
    }

    public async Task<PlayRow?> GetPlayAsync(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{PlaysSelectQuery} WHERE p.id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync()) return MapPlayRow(reader);
        return null;
    }

    public async Task UpdatePlayReplayFileAsync(long id, string path)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE plays SET replay_file = $path WHERE id = $id";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateCursorOffsetsAsync(long scoreId, string json)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE plays SET cursor_offsets = $json WHERE score_id = $id";
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$id", scoreId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<object[]>?> GetPpTimelineAsync(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pp_timeline FROM plays WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        var result = await cmd.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value) return null;
        var json = (string)result;
        if (string.IsNullOrEmpty(json)) return null;
        try 
        { 
            using var doc = JsonDocument.Parse(json);
            var list = new List<object[]>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Number)
                {
                    // Compatibility with old double-only timeline
                    list.Add(new object[] { element.GetDouble() });
                }
                else if (element.ValueKind == JsonValueKind.Array)
                {
                    var innerList = new List<object>();
                    foreach (var sub in element.EnumerateArray())
                    {
                        if (sub.ValueKind == JsonValueKind.Number) innerList.Add(sub.GetDouble());
                        else innerList.Add(sub.ToString());
                    }
                    list.Add(innerList.ToArray());
                }
            }
            return list;
        }
        catch { return null; }
    }

    public async Task<TotalStats> GetTotalStatsAsync()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1), SUM(duration_ms) FROM plays";
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new TotalStats(reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1));
        }
        return new TotalStats(0, 0);
    }

    public async Task<DailyAverageStats> GetDailyAverageStatsAsync()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AVG(accuracy), AVG(pp) 
            FROM plays 
            WHERE created_at_utc >= datetime('now', '-24 hours')
        """;
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new DailyAverageStats(reader.IsDBNull(0) ? 0 : reader.GetDouble(0), reader.IsDBNull(1) ? 0 : reader.GetDouble(1));
        }
        return new DailyAverageStats(0, 0);
    }

    public async Task<AnalyticsSummary> GetAnalyticsSummaryAsync(int days)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        string timeFilter = days > 0 ? "WHERE created_at_utc >= datetime('now', $offset)" : "";
        cmd.CommandText = $"""
            SELECT 
                COUNT(1),
                SUM(duration_ms),
                AVG(pp),
                AVG(accuracy)
            FROM plays 
            {timeFilter}
        """;
        if (days > 0) cmd.Parameters.AddWithValue("$offset", $"-{days} days");

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new AnalyticsSummary(
                reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                reader.IsDBNull(3) ? 0 : reader.GetDouble(3)
            );
        }
        return new AnalyticsSummary(0, 0, 0, 0);
    }

    public async Task<List<DailyStats>> GetDailyStatsAsync(int days)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        string timeFilter = days > 0 ? "WHERE created_at_utc >= datetime('now', $offset)" : "";
        cmd.CommandText = $"""
            SELECT date(created_at_utc, 'localtime') as d, 
                   COUNT(1), 
                   SUM(CASE WHEN outcome = 'pass' THEN 1 ELSE 0 END),
                   SUM(pp),
                   SUM(duration_ms),
                   SUM(accuracy)
            FROM plays 
            {timeFilter}
            GROUP BY d
            ORDER BY d ASC
        """;
        if (days > 0) cmd.Parameters.AddWithValue("$offset", $"-{days} days");
        var list = new List<DailyStats>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new DailyStats
            {
                Date = reader.GetString(0),
                PlayCount = reader.GetInt32(1),
                PassCount = reader.GetInt32(2),
                TotalPP = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                TotalDurationMs = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                TotalAccuracy = reader.IsDBNull(5) ? 0 : reader.GetDouble(5)
            });
        }
        return list;
    }


    public async Task MigrateAsync()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP INDEX IF EXISTS idx_plays_created_map;";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "DELETE FROM plays WHERE id NOT IN (SELECT MIN(id) FROM plays GROUP BY created_at_utc, beatmap_hash);";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_plays_created_hash ON plays(created_at_utc, beatmap_hash);";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_plays_score_id_nonzero ON plays(score_id) WHERE score_id != 0;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<BeatmapRow>> GetBeatmapsAsync()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hash, title, artist, mapper, version, cs, ar, od, hp, bpm, length_ms, stars, status, background_hash, circles, sliders, spinners, max_combo, play_count, pass_count, last_played_utc FROM beatmaps";
        var list = new List<BeatmapRow>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new BeatmapRow
            {
                Hash = reader.GetString(0),
                Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Artist = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Mapper = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Version = reader.IsDBNull(4) ? "" : reader.GetString(4),
                CS = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                AR = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                OD = reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
                HP = reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
                BPM = reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
                LengthMs = reader.IsDBNull(10) ? 0 : reader.GetDouble(10),
                Stars = reader.IsDBNull(11) ? 0 : reader.GetDouble(11),
                Status = reader.IsDBNull(12) ? "" : reader.GetString(12),
                BackgroundHash = reader.IsDBNull(13) ? null : reader.GetString(13),
                Circles = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                Sliders = reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
                Spinners = reader.IsDBNull(16) ? 0 : reader.GetInt32(16),
                MaxCombo = reader.IsDBNull(17) ? 0 : reader.GetInt32(17),
                PlayCount = reader.IsDBNull(18) ? 0 : reader.GetInt32(18),
                PassCount = reader.IsDBNull(19) ? 0 : reader.GetInt32(19),
                LastPlayedUtc = reader.IsDBNull(20) ? null : DateTime.Parse(reader.GetString(20), null, DateTimeStyles.RoundtripKind)
            });
        }
        return list;
    }

    public async Task InsertOrUpdateBeatmapAsync(BeatmapRow b)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        string updateClause = """
            title=excluded.title, artist=excluded.artist, mapper=excluded.mapper, version=excluded.version,
            cs=excluded.cs, ar=excluded.ar, od=excluded.od, hp=excluded.hp, bpm=excluded.bpm,
            length_ms=excluded.length_ms, stars=excluded.stars, status=excluded.status, background_hash=excluded.background_hash,
            circles=excluded.circles, sliders=excluded.sliders, spinners=excluded.spinners, max_combo=excluded.max_combo,
            play_count=excluded.play_count, pass_count=excluded.pass_count, last_played_utc=excluded.last_played_utc
        """;

        if (!string.IsNullOrEmpty(b.OsuFilePath))
        {
            updateClause += ", osu_file_path=excluded.osu_file_path";
        }

        cmd.CommandText = $"""
            INSERT INTO beatmaps (hash, title, artist, mapper, version, cs, ar, od, hp, bpm, length_ms, stars, status, background_hash, circles, sliders, spinners, max_combo, play_count, pass_count, last_played_utc, osu_file_path)
            VALUES ($hash, $title, $artist, $mapper, $version, $cs, $ar, $od, $hp, $bpm, $len, $stars, $status, $bg, $circles, $sliders, $spinners, $max_combo, $pc, $passc, $lp, $osupath)
            ON CONFLICT(hash) DO UPDATE SET {updateClause};
        """;

        cmd.Parameters.AddWithValue("$hash", b.Hash);
        cmd.Parameters.AddWithValue("$title", b.Title);
        cmd.Parameters.AddWithValue("$artist", b.Artist);
        cmd.Parameters.AddWithValue("$mapper", b.Mapper);
        cmd.Parameters.AddWithValue("$version", b.Version);
        cmd.Parameters.AddWithValue("$cs", b.CS);
        cmd.Parameters.AddWithValue("$ar", b.AR);
        cmd.Parameters.AddWithValue("$od", b.OD);
        cmd.Parameters.AddWithValue("$hp", b.HP);
        cmd.Parameters.AddWithValue("$bpm", b.BPM);
        cmd.Parameters.AddWithValue("$len", b.LengthMs);
        cmd.Parameters.AddWithValue("$stars", b.Stars);
        cmd.Parameters.AddWithValue("$status", b.Status);
        cmd.Parameters.AddWithValue("$bg", b.BackgroundHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$circles", b.Circles);
        cmd.Parameters.AddWithValue("$sliders", b.Sliders);
        cmd.Parameters.AddWithValue("$spinners", b.Spinners);
        cmd.Parameters.AddWithValue("$max_combo", b.MaxCombo);
        cmd.Parameters.AddWithValue("$pc", b.PlayCount);
        cmd.Parameters.AddWithValue("$passc", b.PassCount);
        cmd.Parameters.AddWithValue("$lp", b.LastPlayedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$osupath", b.OsuFilePath ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InsertPlaysBatchAsync(IEnumerable<PlayRow> plays)
    {
        using var conn = Open();
        using var transaction = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;

        cmd.CommandText = """
        INSERT OR IGNORE INTO plays (score_id, created_at_utc, outcome, duration_ms, beatmap, beatmap_hash, mods, stars, accuracy, score, combo, count300, count100, count50, misses, pp, rank, hit_offsets, timeline, aim_offsets, cursor_offsets, ur, replay_file, replay_hash)
        VALUES ($score_id, $created_at_utc, $outcome, $duration_ms, $beatmap, $beatmap_hash, $mods, $stars, $accuracy, $score, $combo, $count300, $count100, $count50, $misses, $pp, $rank, $hit_offsets, $timeline, $aim_offsets, $cursor_offsets, $ur, $replay_file, $replay_hash);
        """;


        var pScoreId = cmd.Parameters.Add("$score_id", SqliteType.Integer);
        var pCreated = cmd.Parameters.Add("$created_at_utc", SqliteType.Text);
        var pOutcome = cmd.Parameters.Add("$outcome", SqliteType.Text);
        var pDuration = cmd.Parameters.Add("$duration_ms", SqliteType.Integer);
        var pBeatmap = cmd.Parameters.Add("$beatmap", SqliteType.Text);
        var pHash = cmd.Parameters.Add("$beatmap_hash", SqliteType.Text);
        var pMods = cmd.Parameters.Add("$mods", SqliteType.Text);
        var pStars = cmd.Parameters.Add("$stars", SqliteType.Real);
        var pAcc = cmd.Parameters.Add("$accuracy", SqliteType.Real);
        var pScore = cmd.Parameters.Add("$score", SqliteType.Integer);
        var pCombo = cmd.Parameters.Add("$combo", SqliteType.Integer);
        var p300 = cmd.Parameters.Add("$count300", SqliteType.Integer);
        var p100 = cmd.Parameters.Add("$count100", SqliteType.Integer);
        var p50 = cmd.Parameters.Add("$count50", SqliteType.Integer);
        var pMiss = cmd.Parameters.Add("$misses", SqliteType.Integer);
        var pPP = cmd.Parameters.Add("$pp", SqliteType.Real);
        var pRank = cmd.Parameters.Add("$rank", SqliteType.Text);
        var pHit = cmd.Parameters.Add("$hit_offsets", SqliteType.Text);
        var pTime = cmd.Parameters.Add("$timeline", SqliteType.Text);
        var pAimOffsets = cmd.Parameters.Add("$aim_offsets", SqliteType.Text);
        var pCursorOffsets = cmd.Parameters.Add("$cursor_offsets", SqliteType.Text);
        var pUR = cmd.Parameters.Add("$ur", SqliteType.Real);
        var pReplay = cmd.Parameters.Add("$replay_file", SqliteType.Text);
        var pReplayHash = cmd.Parameters.Add("$replay_hash", SqliteType.Text);

        foreach (var row in plays)
        {
            pScoreId.Value = row.ScoreId;
            pCreated.Value = row.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture);
            pOutcome.Value = row.Outcome;
            pDuration.Value = row.DurationMs;
            pBeatmap.Value = row.Beatmap;
            pHash.Value = row.BeatmapHash ?? (object)DBNull.Value;
            pMods.Value = row.Mods;
            pStars.Value = row.Stars ?? (object)DBNull.Value;
            pAcc.Value = row.Accuracy;
            pScore.Value = row.Score;
            pCombo.Value = row.Combo;
            p300.Value = row.Count300;
            p100.Value = row.Count100;
            p50.Value = row.Count50;
            pMiss.Value = row.Misses;
            pPP.Value = row.PP;
            pRank.Value = row.Rank ?? "";
            pHit.Value = row.HitOffsets;
            pTime.Value = row.TimelineJson;
            pAimOffsets.Value = row.AimOffsetsJson;
            pCursorOffsets.Value = row.CursorOffsetsJson;
            pUR.Value = row.UR;
            pReplay.Value = row.ReplayFile ?? "";
            pReplayHash.Value = row.ReplayHash ?? "";

            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<string?> GetMapHashByMetadataAsync(string artist, string title)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hash FROM beatmaps WHERE artist = $a AND title = $t LIMIT 1";
        cmd.Parameters.AddWithValue("$a", artist);
        cmd.Parameters.AddWithValue("$t", title);
        var result = await cmd.ExecuteScalarAsync();
        return result == DBNull.Value || result == null ? null : (string)result;
    }

    private const string PlaysSelectQuery = """
        SELECT p.id, p.score_id, p.created_at_utc, p.outcome, p.duration_ms, p.beatmap, p.mods, p.stars, p.accuracy, p.score, p.combo, p.count300, p.count100, p.count50, p.misses, p.pp, p.notes, p.beatmap_hash, p.rank, p.hit_offsets, p.timeline, p.pp_timeline, p.aim_offsets, b.background_hash, p.ur, p.replay_file, p.replay_hash,
               b.title, b.artist, b.version, b.cs, b.ar, b.od, b.hp, b.bpm, p.cursor_offsets, p.map_path
        FROM plays p
        LEFT JOIN beatmaps b ON p.beatmap_hash = b.hash
        """;

    private PlayRow MapPlayRow(SqliteDataReader reader)
    {
        var createdStr = reader.GetString(2);
        var created = DateTime.TryParse(createdStr, null, DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.UtcNow;

        return new PlayRow
        {
            Id = reader.GetInt64(0),
            ScoreId = reader.GetInt64(1),
            CreatedAtUtc = created.Kind == DateTimeKind.Utc ? created : created.ToUniversalTime(),
            Outcome = reader.GetString(3),
            DurationMs = reader.GetInt32(4),
            Beatmap = reader.GetString(5),
            Mods = reader.GetString(6),
            Stars = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            Accuracy = reader.GetDouble(8),
            Score = reader.GetInt64(9),
            Combo = reader.GetInt32(10),
            Count300 = reader.GetInt32(11),
            Count100 = reader.GetInt32(12),
            Count50 = reader.GetInt32(13),
            Misses = reader.GetInt32(14),
            PP = reader.GetDouble(15),
            Notes = reader.IsDBNull(16) ? "" : reader.GetString(16),
            BeatmapHash = reader.IsDBNull(17) ? "" : reader.GetString(17),
            Rank = reader.IsDBNull(18) ? null : reader.GetString(18),
            HitOffsets = reader.IsDBNull(19) ? "" : reader.GetString(19),
            TimelineJson = reader.IsDBNull(20) ? "" : reader.GetString(20),
            PpTimelineJson = reader.IsDBNull(21) ? "" : reader.GetString(21),
            AimOffsetsJson = reader.IsDBNull(22) ? "" : reader.GetString(22),
            BackgroundPath = reader.IsDBNull(23) ? null : reader.GetString(23),
            UR = reader.GetDouble(24),
            ReplayFile = reader.IsDBNull(25) ? "" : reader.GetString(25),
            ReplayHash = reader.IsDBNull(26) ? "" : reader.GetString(26),
            Title = reader.IsDBNull(27) ? "" : reader.GetString(27),
            Artist = reader.IsDBNull(28) ? "" : reader.GetString(28),
            Difficulty = reader.IsDBNull(29) ? "" : reader.GetString(29),
            CS = reader.IsDBNull(30) ? null : reader.GetDouble(30),
            AR = reader.IsDBNull(31) ? null : reader.GetDouble(31),
            OD = reader.IsDBNull(32) ? null : reader.GetDouble(32),
            HP = reader.IsDBNull(33) ? null : reader.GetDouble(33),
            BPM = reader.IsDBNull(34) ? null : reader.GetDouble(34),
            CursorOffsetsJson = reader.IsDBNull(35) ? "" : reader.GetString(35),
            MapPath = reader.IsDBNull(36) ? "" : reader.GetString(36)
        };
    }

    public async Task<PlayRow?> FindSimilarPlayAsync(string beatmapHash, long score, string outcome, DateTime timeUtc, TimeSpan tolerance)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{PlaysSelectQuery} WHERE p.beatmap_hash = $hash AND p.score = $score AND p.outcome = $outcome AND p.created_at_utc BETWEEN $start AND $end LIMIT 1";
        cmd.Parameters.AddWithValue("$hash", beatmapHash);
        cmd.Parameters.AddWithValue("$score", score);
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$start", timeUtc.Subtract(tolerance).ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$end", timeUtc.Add(tolerance).ToString("O", CultureInfo.InvariantCulture));
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync()) return MapPlayRow(reader);
        return null;
    }

    public async Task<PlayRow?> GetPlayByScoreIdAsync(long scoreId)
    {
        if (scoreId == 0) return null;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{PlaysSelectQuery} WHERE p.score_id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", scoreId);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync()) return MapPlayRow(reader);
        return null;
    }

    public long GetMaxScoreForBeatmap(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return 0;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(score) FROM plays WHERE beatmap_hash = $hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        var result = cmd.ExecuteScalar();
        if (result == null || result == DBNull.Value) return 0;
        return (long)result;
    }

    public async Task<long> GetMaxScoreForBeatmapAsync(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return 0;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(score) FROM plays WHERE beatmap_hash = $hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        var result = await cmd.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value) return 0;
        return (long)result;
    }

    public async Task DeleteAllScoresAsync()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM plays";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeletePlayAsync(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM plays WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdatePlayNotesAsync(long id, string notes)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE plays SET notes = $notes WHERE id = $id";
        cmd.Parameters.AddWithValue("$notes", notes);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }


    public async Task DeleteAllBeatmapsAsync()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM beatmaps";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<HashSet<string>> GetExistingScoreSignaturesAsync()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // Fetch hash, score, date to build unique signature
        cmd.CommandText = "SELECT beatmap_hash, score, created_at_utc FROM plays";
        
        var signatures = new HashSet<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var hash = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var score = reader.GetInt64(1);
            var date = reader.IsDBNull(2) ? "" : reader.GetString(2);
            signatures.Add($"{hash}|{score}|{date}");
        }
        return signatures;
    }
}


    public record TotalStats(int totalPlays, long totalTimeMs);
    public record DailyAverageStats(double avgDailyAcc, double avgDailyPP);
    public record AnalyticsSummary(int TotalPlays, long TotalDurationMs, double AvgPP, double AvgAccuracy);
    
    public class DailyStats
    {
        public string Date { get; set; } = "";
        public int PlayCount { get; set; }
        public int PassCount { get; set; }
        public double TotalPP { get; set; }
        public double TotalAccuracy { get; set; }
        public long TotalDurationMs { get; set; }
        public double AvgPP => PlayCount > 0 ? TotalPP / PlayCount : 0;
        public double AvgAcc => PlayCount > 0 ? TotalAccuracy / PlayCount : 0;
    }

