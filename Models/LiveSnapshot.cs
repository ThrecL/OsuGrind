using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace OsuGrind.Models;

public sealed record HitCounts(int Count300, int Count100, int Count50, int Misses, int SliderTailHit = 0, int SmallTickHit = 0, int LargeTickHit = 0);

public sealed record CursorHitPoint(double time, double dx, double dy, int result);

public sealed class CompletedPlay
{
    public long ScoreId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string BeatmapHash { get; set; } = "";
    public string Beatmap { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Version { get; set; } = "";
    public string Mods { get; set; } = "NM";
    public string Outcome { get; set; } = "pass"; // pass|fail
    public int DurationMs { get; set; }
    public double? Stars { get; set; }
    public double Accuracy { get; set; }
    public long Score { get; set; }
    public int MaxCombo { get; set; }
    public int Count300 { get; set; }
    public int Count100 { get; set; }
    public int Count50 { get; set; }
    public int Misses { get; set; }
    public double PP { get; set; }
    
    // Beatmap Detail
    public double CS { get; set; }
    public double AR { get; set; }
    public double OD { get; set; }
    public double HP { get; set; }
    public double BPM { get; set; }
    public int LengthMs { get; set; }
    public int Circles { get; set; }
    public int Sliders { get; set; }
    public int Spinners { get; set; }
    public string? BackgroundHash { get; set; }
    
    // Advanced Analytics
    public string HitOffsets { get; set; } = ""; // Comma-separated or JSON list of hit errors
    public string? HitErrorsJson { get; set; }
    public double KeyRatio { get; set; }
    public string TimelineJson { get; set; } = ""; // JSON of (time, combo, isMiss)
    public string PpTimelineJson { get; set; } = ""; // JSON array of PP per passed object
    public string AimOffsetsJson { get; set; } = ""; // JSON array for aim scatter plot
    public string CursorOffsetsJson { get; set; } = ""; // JSON array for cursor heatmap
    public string ReplayFile { get; set; } = ""; // Path to the .osr replay file
    public string ReplayHash { get; set; } = ""; // MD5 of the score/replay
    public string MapPath { get; set; } = ""; // Path to the .osu beatmap file
    public double UR { get; set; }
}


public sealed class LiveSnapshot
{
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public string WindowTitle { get; set; } = "";

    public int StateNumber { get; set; }
    public bool IsPlaying { get; set; }
    public bool IsReplay { get; set; } // True if watching a replay or spectating
    public bool IsPaused { get; set; } // Explicit paused state
    public bool IsResultsReady { get; set; } // True if results screen data is fully loaded
    public bool IsPreview { get; set; } // True if snapshot contains simulated/preview data (Song Select SS)
    public bool Failed { get; set; }
    public bool? Passed { get; set; }
    public string Beatmap { get; set; } = "(unknown beatmap)";
    public string MapName => Beatmap; // Alias for JS compatibility
    public string PlayState => GetPlayState();
    
    private string GetPlayState()
    {
        // Check special flags first
        if (IsReplay) return "Replay";
        if (IsPaused && StateNumber == 2) return "Paused";
        
        // Then check state number
        return StateNumber switch
        {
            1 => "Menu",
            2 => "Playing",
            4 => "Menu",
            5 => "Song Select",
            7 => "Results",
            11 => "Multiplayer",
            _ => "Idle"
        };
    }
    public string? Artist { get; set; }
    public string? Title { get; set; }
    public string? Version { get; set; }
    public string? MD5Hash { get; set; }
    public string Mods { get; set; } = "NM";
    public string? BackgroundPath { get; set; }
    public string? BackgroundHash { get; set; }
    public string? MapPath { get; set; }
    public string? ReplayPath { get; set; }
    public string? OsuFolder { get; set; }
    public string? Grade { get; set; }
    public double? Stars { get; set; }
    public double? BaseStars { get; set; }
    public int MapMaxCombo { get; set; }
    public long? MaxScore { get; set; }
    public int? Circles { get; set; }
    public int? Sliders { get; set; }
    public int? Spinners { get; set; }
    public int? TotalObjects { get; set; }

    public double? Accuracy { get; set; }
    public long? Score { get; set; }
    public int? Combo { get; set; }
    public int? MaxCombo { get; set; }
    public int? TimeMs { get; set; }
    public int? TotalTimeMs { get; set; }
    public double? PP { get; set; }
    public double? PPIfFC { get; set; }
    public HitCounts? HitCounts { get; set; }
    public int? BPM { get; set; }
    public int? BaseBPM { get; set; }
    public int? MinBPM { get; set; }
    public int? MaxBPM { get; set; }
    public int? MostlyBPM { get; set; }
    public double? AR { get; set; }
    public double? CS { get; set; }
    public double? OD { get; set; }
    public double? HP { get; set; }
    public double? BaseAR { get; set; }
    public double? BaseCS { get; set; }
    public double? BaseOD { get; set; }
    public double? BaseHP { get; set; }
    public double? LiveHP { get; set; }
    
    public double Progress { get; set; } // Map progress 0.0 to 1.0

    public List<string>? ModsList { get; set; }

    public bool IsLazer { get; set; }
    public bool MapFileFound => !string.IsNullOrEmpty(MapPath) && File.Exists(MapPath);
    public string? ReplayHash { get; set; }
    public string? HitErrorsJson { get; set; }
    public double KeyRatio { get; set; }

    public LiveSnapshot Clone()
    {
        return new LiveSnapshot
        {
            CapturedAtUtc = this.CapturedAtUtc,
            WindowTitle = this.WindowTitle,
            StateNumber = this.StateNumber,
            IsPlaying = this.IsPlaying,
            IsReplay = this.IsReplay,
            IsPaused = this.IsPaused,
            IsResultsReady = this.IsResultsReady,
            IsPreview = this.IsPreview,
            Failed = this.Failed,
            Passed = this.Passed,
            Beatmap = this.Beatmap,
            Artist = this.Artist,
            Title = this.Title,
            Version = this.Version,
            MD5Hash = this.MD5Hash,
            Mods = this.Mods,
            BackgroundPath = this.BackgroundPath,
            BackgroundHash = this.BackgroundHash,
            MapPath = this.MapPath,
            ReplayPath = this.ReplayPath,
            OsuFolder = this.OsuFolder,
            Grade = this.Grade,
            Stars = this.Stars,
            BaseStars = this.BaseStars,
            MapMaxCombo = this.MapMaxCombo,
            MaxScore = this.MaxScore,
            Circles = this.Circles,
            Sliders = this.Sliders,
            Spinners = this.Spinners,
            TotalObjects = this.TotalObjects,
            Accuracy = this.Accuracy,
            Score = this.Score,
            Combo = this.Combo,
            MaxCombo = this.MaxCombo,
            TimeMs = this.TimeMs,
            TotalTimeMs = this.TotalTimeMs,
            PP = this.PP,
            PPIfFC = this.PPIfFC,
            HitCounts = this.HitCounts != null ? new HitCounts(
                this.HitCounts.Count300, 
                this.HitCounts.Count100, 
                this.HitCounts.Count50, 
                this.HitCounts.Misses, 
                this.HitCounts.SliderTailHit, 
                this.HitCounts.SmallTickHit, 
                this.HitCounts.LargeTickHit) : null,
            BPM = this.BPM,
            BaseBPM = this.BaseBPM,
            MinBPM = this.MinBPM,
            MaxBPM = this.MaxBPM,
            MostlyBPM = this.MostlyBPM,
            AR = this.AR,
            CS = this.CS,
            OD = this.OD,
            HP = this.HP,
            BaseAR = this.BaseAR,
            BaseCS = this.BaseCS,
            BaseOD = this.BaseOD,
            BaseHP = this.BaseHP,
            LiveHP = this.LiveHP,
            Progress = this.Progress,
            ModsList = this.ModsList != null ? new List<string>(this.ModsList) : null,
            IsLazer = this.IsLazer,
            LiveHitOffsets = new List<double>(this.LiveHitOffsets),
            LiveUR = this.LiveUR,
            ScoreDate = this.ScoreDate,
            HitErrorsJson = this.HitErrorsJson,
            KeyRatio = this.KeyRatio
        };
    }

    // Advanced Analytic Buffers (Temporary during gameplay)
    public List<double> LiveHitOffsets { get; set; } = new();
    public Dictionary<int, int> LiveHitOffsetHistogram { get; set; } = new();
    public string LiveTimelineJson { get; set; } = "";
    public string AimOffsetsJson { get; set; } = "";
    public string CursorOffsetsJson { get; set; } = "";
    public double LiveUR { get; set; }
    public List<CursorHitPoint> CursorHits { get; set; } = new();
    public DateTime? ScoreDate { get; set; }

    public CompletedPlay? Completed { get; set; }

    public static LiveSnapshot FromTosuJson(JObject j)
    {
        if (j["state"]?["number"] != null)
            return FromTosuJsonV2(j);
        return FromTosuJsonV1(j);
    }

    public static LiveSnapshot FromTosuJsonV1(JObject j)
    {
        int stateNumber = TryGetInt(j, "menu", "state") ?? -1;
        bool playing = stateNumber == 2;
        bool isResultScreen = stateNumber == 7;

        string artist = TryGetString(j, "menu", "bm", "metadata", "artist") ?? "";
        string title = TryGetString(j, "menu", "bm", "metadata", "title") ?? "(unknown beatmap)";
        string version = TryGetString(j, "menu", "bm", "metadata", "difficulty") ?? "";

        string display = title;
        if (!string.IsNullOrWhiteSpace(artist))
            display = $"{artist} - {display}";
        if (!string.IsNullOrWhiteSpace(version))
            display = $"{display} [{version}]";

        string md5 = TryGetString(j, "menu", "bm", "md5") ?? "";

        string mods = TryGetString(j, "mods", "str") ?? TryGetString(j, "gameplay", "mods", "str") ?? "NM";
        if (string.IsNullOrWhiteSpace(mods)) mods = "NM";

        double? acc = TryGetDouble(j, "gameplay", "accuracy") / 100.0;
        long? score = TryGetLong(j, "gameplay", "score");
        int? combo = TryGetInt(j, "gameplay", "combo", "current");
        int? maxCombo = TryGetInt(j, "gameplay", "combo", "max");

        int? time = TryGetInt(j, "menu", "bm", "time", "current");
        int? totalTime = TryGetInt(j, "menu", "bm", "time", "mp3");

        double? pp = TryGetDouble(j, "gameplay", "pp", "current");
        double? ppIfFc = TryGetDouble(j, "gameplay", "pp", "fc");

        string? grade = TryGetString(j, "gameplay", "hits", "grade", "current");
        string? bgPath = TryGetString(j, "menu", "bm", "path", "full");

        double? stars = TryGetDouble(j, "menu", "bm", "stats", "fullSR")
                        ?? TryGetDouble(j, "menu", "bm", "stats", "SR");

        bool healthZero = TryGetInt(j, "gameplay", "hp", "normal") == 0;
        bool noFail = mods.Contains("NF");
        bool failed = healthZero && !noFail && playing;

        int? c300 = TryGetInt(j, "gameplay", "hits", "300");

        int? c100 = TryGetInt(j, "gameplay", "hits", "100");
        int? c50 = TryGetInt(j, "gameplay", "hits", "50");
        int? miss = TryGetInt(j, "gameplay", "hits", "0");

        HitCounts? hits = null;
        if (c300 is not null || c100 is not null || c50 is not null || miss is not null)
            hits = new HitCounts(c300 ?? 0, c100 ?? 0, c50 ?? 0, miss ?? 0);

        int? bpm = (int?)(TryGetDouble(j, "menu", "bm", "stats", "BPM", "realtime")
                   ?? TryGetDouble(j, "menu", "bm", "stats", "BPM", "common")
                   ?? TryGetDouble(j, "menu", "bm", "stats", "BPM", "min")
                   ?? TryGetDouble(j, "menu", "bm", "stats", "BPM", "max")
                   ?? TryGetDouble(j, "menu", "bm", "stats", "BPM"));

        return new LiveSnapshot
        {
            StateNumber = stateNumber,
            IsPlaying = playing,
            Artist = artist,
            Title = title,
            Version = version,
            Beatmap = display,
            MD5Hash = md5,
            Mods = mods,
            Accuracy = acc,
            Score = score,
            Combo = combo,
            MaxCombo = maxCombo,
            TimeMs = time,
            TotalTimeMs = totalTime,
            PP = pp,
            PPIfFC = ppIfFc,
            Grade = grade,
            BackgroundPath = bgPath,
            Stars = stars,
            Failed = failed,
            HitCounts = hits,
            BPM = bpm
        };
    }

    public static LiveSnapshot FromTosuJsonV2(JObject j)
    {
        int stateNumber = TryGetInt(j, "state", "number") ?? -1;
        bool playing = stateNumber == 2;

        string artist = TryGetString(j, "beatmap", "artist") ?? "";
        string title = TryGetString(j, "beatmap", "title") ?? "(unknown beatmap)";
        string version = TryGetString(j, "beatmap", "version") ?? "";

        string display = title;
        if (!string.IsNullOrWhiteSpace(artist))
            display = $"{artist} - {display}";
        if (!string.IsNullOrWhiteSpace(version))
            display = $"{display} [{version}]";

        string md5 = TryGetString(j, "beatmap", "checksum") ?? "";

        string mods = TryGetString(j, "gameplay", "mods") ?? "NM";
        
        double? acc = TryGetDouble(j, "gameplay", "accuracy");
        long? score = TryGetLong(j, "gameplay", "score");
        int? combo = TryGetInt(j, "gameplay", "combo");
        int? maxCombo = TryGetInt(j, "gameplay", "maxCombo");

        int? time = TryGetInt(j, "state", "time");
        int? totalTime = TryGetInt(j, "beatmap", "duration");

        double? pp = TryGetDouble(j, "gameplay", "pp");
        double? ppIfFc = TryGetDouble(j, "gameplay", "ppIfFc");

        string? grade = TryGetString(j, "gameplay", "grade");
        string? bgPath = TryGetString(j, "beatmap", "background");

        double? stars = TryGetDouble(j, "beatmap", "stars");
        bool failed = TryGetBool(j, "gameplay", "failed") ?? false;

        int? c300 = TryGetInt(j, "gameplay", "hits", "300");
        int? c100 = TryGetInt(j, "gameplay", "hits", "100");
        int? c50 = TryGetInt(j, "gameplay", "hits", "50");
        int? miss = TryGetInt(j, "gameplay", "hits", "0");

        HitCounts? hits = null;
        if (c300 is not null || c100 is not null || c50 is not null || miss is not null)
            hits = new HitCounts(c300 ?? 0, c100 ?? 0, c50 ?? 0, miss ?? 0);

        int? bpm = (int?)TryGetDouble(j, "beatmap", "bpm");

        return new LiveSnapshot
        {
            StateNumber = stateNumber,
            IsPlaying = playing,
            Artist = artist,
            Title = title,
            Version = version,
            Beatmap = display,
            MD5Hash = md5,
            Mods = mods,
            Accuracy = acc,
            Score = score,
            Combo = combo,
            MaxCombo = maxCombo,
            TimeMs = time,
            TotalTimeMs = totalTime,
            PP = pp,
            PPIfFC = ppIfFc,
            Grade = grade,
            BackgroundPath = bgPath,
            Stars = stars,
            Failed = failed,
            HitCounts = hits,
            BPM = bpm,
            AR = TryGetDouble(j, "beatmap", "ar"),
            CS = TryGetDouble(j, "beatmap", "cs"),
            OD = TryGetDouble(j, "beatmap", "od"),
            HP = TryGetDouble(j, "beatmap", "hp")
        };
    }

    private static string? TryGetString(JObject j, params string[] keys)
    {
        JToken? token = j;
        foreach (var key in keys)
        {
            if (token == null) return null;
            token = token[key];
        }
        return token?.ToString();
    }

    private static int? TryGetInt(JObject j, params string[] keys)
    {
        string? s = TryGetString(j, keys);
        if (int.TryParse(s, out int val)) return val;
        return null;
    }

    private static long? TryGetLong(JObject j, params string[] keys)
    {
        string? s = TryGetString(j, keys);
        if (long.TryParse(s, out long val)) return val;
        return null;
    }

    private static double? TryGetDouble(JObject j, params string[] keys)
    {
        string? s = TryGetString(j, keys);
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double val)) return val;
        return null;
    }

    private static bool? TryGetBool(JObject j, params string[] keys)
    {
        string? s = TryGetString(j, keys);
        if (bool.TryParse(s, out bool val)) return val;
        return null;
    }
}
