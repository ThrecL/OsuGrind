using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OsuGrind.Models;

public sealed class PlayRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; set; }
    public long ScoreId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public string PlayedAtLocal => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    // pass|fail|quit
    public string Outcome { get; set; } = "pass";
    public int DurationMs { get; set; }
    public string DurationDisplay => TimeSpan.FromMilliseconds(Math.Max(0, DurationMs)).ToString(@"mm\:ss");

    public string BeatmapHash { get; set; } = "";
    public string Beatmap { get; set; } = "";
    public string Mods { get; set; } = "NM";
    public double? Stars { get; set; }

    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Difficulty { get; set; } = "";

    // Map attributes (from beatmaps table)
    public double? CS { get; set; }
    public double? AR { get; set; }
    public double? OD { get; set; }
    public double? HP { get; set; }
    public double? BPM { get; set; }

    private double? accuracy;
    public double Accuracy
    {
        get => accuracy ?? CalculateAccuracy(Count300, Count100, Count50, Misses);
        set => accuracy = value;
    }
    public string AccuracyDisplay => (Accuracy * 100.0).ToString("0.00") + "%";
    public long Score { get; set; }
    public int Combo { get; set; }
    public int Count300 { get; set; }
    public int Count100 { get; set; }
    public int Count50 { get; set; }
    public int Misses { get; set; }
    public double PP { get; set; }

    // Advanced Analytics
    public string HitOffsets { get; set; } = "";
    public string TimelineJson { get; set; } = "";
    public string PpTimelineJson { get; set; } = "";
    public string AimOffsetsJson { get; set; } = "";
    public string CursorOffsetsJson { get; set; } = "";

    // Enhanced Analytics
    public string? HitErrorsJson { get; set; } // JSON array of offsets
    public double KeyRatio { get; set; } // Top1 / (Top1 + Top2)

    // UI specific
    public string? BackgroundPath { get; set; }
    public string? MapPath { get; set; }
    public string? ReplayFile { get; set; }
    public string ReplayHash { get; set; } = "";

    private double? _ur;
    public double UR 
    { 
        get => _ur ?? CalculateUR();
        set => _ur = value;
    }

    public string URColor => UR < 80 ? "#00FFFF" : (UR < 120 ? "#00FF88" : "#FFAA00");

    public string RankColor => Rank switch
    {
        "X" or "XH" => "#00FFFF",
        "S" or "SH" => "#FFD700",
        "A" => "#00FF88",
        "B" => "#66AAFF",
        "C" => "#BB88FF",
        "D" => "#888888",
        _ => "#555555"
    };

    public string DifficultyColor
    {
        get
        {
            double s = Stars ?? 0;
            if (s < 2.0) return "#4FC0FF"; // Blue
            if (s < 2.7) return "#4FFFD5"; // Green
            if (s < 4.0) return "#F6F05C"; // Yellow
            if (s < 5.3) return "#FF8068"; // Pink
            if (s < 6.5) return "#FF3C78"; // Purple
            return "#6563DE";            // Black/Dark Purple
        }
    }

    public string BeatmapArtist => GetBeatmapPart(0);
    public string BeatmapTitle => GetBeatmapPart(1);
    public string BeatmapVersion => GetBeatmapPart(2);

    private string GetBeatmapPart(int part)
    {
        // Format: Artist - Title [Version]
        var firstSplit = Beatmap.Split(" [", 2);
        var mainPart = firstSplit[0];
        var versionPart = firstSplit.Length > 1 ? firstSplit[1].TrimEnd(']') : "";

        var artistTitleSplit = mainPart.Split(" - ", 2);
        string artist, title;

        if (artistTitleSplit.Length > 1)
        {
            artist = artistTitleSplit[0];
            title = artistTitleSplit[1];
        }
        else
        {
            artist = ""; 
            title = mainPart;
        }

        return part switch
        {
            0 => artist,
            1 => title,
            2 => versionPart,
            _ => ""
        };
    }

    public void EnsureMetadata()
    {
        if (string.IsNullOrEmpty(Title) && !string.IsNullOrEmpty(Beatmap))
        {
            Title = BeatmapTitle;
            Artist = BeatmapArtist;
            Difficulty = BeatmapVersion;
        }
    }
    public List<string> ModsList => (Mods ?? "NM").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(m => m.Trim()).ToList();

    public List<double> HitOffsetPoints => string.IsNullOrEmpty(HitOffsets) ? new() : 
        HitOffsets.Split(',', StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => double.TryParse(s, out var d) ? d : 0)
                  .ToList();
    
    public IEnumerable<HitErrorTick> HitErrorTicks
    {
        get
        {
            if (string.IsNullOrEmpty(HitOffsets)) yield break;
            
            // Define hit window range for visualization (e.g. +/- 60ms)
            // Anything outside this will be clamped or hidden
            double range = 60.0;
            double width = 200.0; // Fixed width matching XAML
            
            foreach (var part in HitOffsets.Split(','))
            {
                if (double.TryParse(part, out var offset))
                {
                    // Map -range..+range to 0..1
                    double normalized = (offset + range) / (2 * range);
                    normalized = Math.Clamp(normalized, 0, 1);
                    
                    double pixelX = normalized * width;
                    
                    yield return new HitErrorTick 
                    { 
                        Offset = offset,
                        Margin = $"{pixelX:0},0,0,0",
                        Color = Math.Abs(offset) < 20 ? "#4FFFD5" : (Math.Abs(offset) < 40 ? "#F6F05C" : "#FF3C78")
                    };
                }
            }
        }
    }

    public class HitErrorTick
    {
        public double Offset { get; set; }
        public string Margin { get; set; } = "100,0,0,0";
        public string Color { get; set; } = "White";
    }

    public string Timestamp => CreatedAtUtc.ToLocalTime().ToString("M/d/yyyy, h:mm:ss tt");

    private double CalculateUR()
    {
        if (string.IsNullOrEmpty(HitOffsets)) return 0;
        try
        {
            var offsets = HitOffsets.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.TryParse(s, out var d) ? d : 0)
                .ToList();
            if (offsets.Count == 0) return 0;
            double avg = offsets.Average();
            double sumSq = offsets.Sum(d => Math.Pow(d - avg, 2));
            return Math.Sqrt(sumSq / offsets.Count) * 10;
        }
        catch { return 0; }
    }

    private string notes = "";
    public string Notes
    {
        get => notes;
        set
        {
            if (value == notes) return;
            notes = value ?? "";
            OnPropertyChanged();
        }
    }

    public static PlayRow FromCompleted(CompletedPlay c)
    {
        return new PlayRow
        {
            ScoreId = c.ScoreId,
            CreatedAtUtc = c.CreatedAtUtc,
            Outcome = c.Outcome,
            DurationMs = c.DurationMs,
            BeatmapHash = c.BeatmapHash,
            Beatmap = c.Beatmap,
            Mods = c.Mods,
            Stars = c.Stars,
            // Accuracy is computed
            Score = c.Score,
            Combo = c.MaxCombo,
            Count300 = c.Count300,
            Count100 = c.Count100,
            Count50 = c.Count50,
            Misses = c.Misses,
            PP = c.PP,
            HitOffsets = c.HitOffsets,
            TimelineJson = c.TimelineJson,
            PpTimelineJson = c.PpTimelineJson,
            UR = c.UR,
            HitErrorsJson = c.HitErrorsJson,
            KeyRatio = c.KeyRatio,
            CursorOffsetsJson = c.CursorOffsetsJson,
            ReplayFile = c.ReplayFile,
            MapPath = c.MapPath,
            ReplayHash = "", // CompletedPlay might not have ReplayHash populated yet, usually from import
            Notes = "",
        };
    }

    private string? rank;
    public string? Rank
    {
        get => rank ?? CalculateRank(Count300, Count100, Count50, Misses, Outcome);
        set => rank = value;
    }

    private string CalculateRank(int n300, int n100, int n50, int nMiss, string outcome)
    {
        if (outcome == "fail") return "F";

        int total = n300 + n100 + n50 + nMiss;
        if (total == 0) return "F";

        double p300 = (double)n300 / total;
        double p50 = (double)n50 / total;
        bool silver = Mods.Contains("HD") || Mods.Contains("FL");

        if (Accuracy >= 1.0) return silver ? "XH" : "X";
        if (p300 > 0.9 && p50 <= 0.01 && nMiss == 0) return silver ? "SH" : "S";
        if ((p300 > 0.8 && nMiss == 0) || p300 > 0.9) return "A";
        if ((p300 > 0.7 && nMiss == 0) || p300 > 0.8) return "B";
        if (p300 > 0.7) return "C";

        return "D";
    }

    private static double CalculateAccuracy(int n300, int n100, int n50, int nMiss)
    {
        int total = n300 + n100 + n50 + nMiss;
        if (total == 0) return 0;
        return (300.0 * n300 + 100.0 * n100 + 50.0 * n50) / (300.0 * total);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
