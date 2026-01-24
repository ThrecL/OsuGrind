using System;
using System.Collections.Generic;

namespace OsuGrind.Models;

/// <summary>
/// Monthly aggregated stats for charting.
/// </summary>
public sealed class MonthlyStats
{
    public int Year { get; init; }
    public int Month { get; init; }
    public string Label => $"{Year}-{Month:D2}";
    public int PlayCount { get; init; }
    public long TotalTimeMs { get; init; }
    public double TotalTimeHours => TotalTimeMs / 3600000.0;
    public double AvgAccuracy { get; init; }
    public double AvgMisses { get; init; }
    public double AvgCombo { get; init; }
    public double AvgStars { get; init; }
    public int PassCount { get; init; }
    public double PassRate => PlayCount > 0 ? (PassCount * 100.0 / PlayCount) : 0;
    public double TotalPP { get; init; }
}

/// <summary>
/// Stats for a specific time period comparison.
/// </summary>
public sealed class PeriodStats
{
    public string PeriodLabel { get; init; } = "";
    public int Days { get; init; }
    public int PlayCount { get; init; }
    public long TotalTimeMs { get; init; }
    public double TotalTimeHours => TotalTimeMs / 3600000.0;
    public double AvgAccuracy { get; init; }
    public double AvgCombo { get; init; }
    public double AvgMisses { get; init; }
    public int PassCount { get; init; }
    public int FailCount { get; init; }
    public int QuitCount { get; init; }
    public double PassRate => PlayCount > 0 ? (PassCount * 100.0 / PlayCount) : 0;
}

/// <summary>
/// Improvement comparison between two periods.
/// </summary>
public sealed class ImprovementComparison
{
    public string Label { get; init; } = "";
    public int CurrentDays { get; init; }
    public int PreviousDays { get; init; }
    
    // Convenience property for filtering
    public int DaysBack => CurrentDays;
    
    public PeriodStats Current { get; init; } = new();
    public PeriodStats Previous { get; init; } = new();
    
    // Improvement percentages (positive = better)
    public double AccuracyChange => Current.AvgAccuracy - Previous.AvgAccuracy;
    public double AccuracyChangePercent => Previous.AvgAccuracy > 0 
        ? ((Current.AvgAccuracy - Previous.AvgAccuracy) / Previous.AvgAccuracy) * 100 
        : 0;
    
    public double ComboChange => Current.AvgCombo - Previous.AvgCombo;
    public double ComboChangePercent => Previous.AvgCombo > 0 
        ? ((Current.AvgCombo - Previous.AvgCombo) / Previous.AvgCombo) * 100 
        : 0;
    
    public double MissChange => Previous.AvgMisses - Current.AvgMisses; // Less misses = better
    public double MissChangePercent => Previous.AvgMisses > 0 
        ? ((Previous.AvgMisses - Current.AvgMisses) / Previous.AvgMisses) * 100 
        : 0;
    
    public double PassRateChange => Current.PassRate - Previous.PassRate;
    
    // Overall improvement score (-100 to +100, weighted average)
    public double OverallImprovementScore
    {
        get
        {
            if (Previous.PlayCount < 3 || Current.PlayCount < 3) return 0;
            
            // Weights for each metric
            double accWeight = 0.4;
            double comboWeight = 0.25;
            double missWeight = 0.25;
            double passWeight = 0.1;
            
            // Normalize each metric change to a -100 to +100 scale
            double accScore = Math.Clamp(AccuracyChange * 10, -100, 100); // 1% acc = 10 points
            double comboScore = Math.Clamp(ComboChangePercent, -100, 100);
            double missScore = Math.Clamp(MissChangePercent, -100, 100);
            double passScore = Math.Clamp(PassRateChange, -100, 100);
            
            return (accScore * accWeight) + (comboScore * comboWeight) + 
                   (missScore * missWeight) + (passScore * passWeight);
        }
    }
}

/// <summary>
/// Beatmap difficulty info extracted from beatmap string.
/// </summary>
public sealed class BeatmapDifficultyInfo
{
    public string Beatmap { get; init; } = "";
    public string DifficultyName { get; init; } = "";
    public double? Stars { get; init; }
    public double? BPM { get; init; }
    public double? AR { get; init; }
    
    // Used to group similar difficulty maps
    public string DifficultyBucket
    {
        get
        {
            if (Stars is null) return "Unknown";
            return Stars.Value switch
            {
                < 2 => "1-2★",
                < 3 => "2-3★",
                < 4 => "3-4★",
                < 5 => "4-5★",
                < 6 => "5-6★",
                < 7 => "6-7★",
                < 8 => "7-8★",
                _ => "8+★"
            };
        }
    }
}

/// <summary>
/// Analysis of plays on a specific beatmap over time.
/// </summary>
public sealed class BeatmapProgressAnalysis
{
    public string Beatmap { get; init; } = "";
    public int TotalPlays { get; init; }
    public DateTime FirstPlayedUtc { get; init; }
    public DateTime LastPlayedUtc { get; init; }
    
    public double FirstAccuracy { get; init; }
    public double BestAccuracy { get; init; }
    public double LatestAccuracy { get; init; }
    
    public int FirstMisses { get; init; }
    public int BestMisses { get; init; }
    public int LatestMisses { get; init; }
    
    public int FirstCombo { get; init; }
    public int BestCombo { get; init; }
    public int LatestCombo { get; init; }
    
    // Improvement from first to best
    public double AccuracyImprovement => BestAccuracy - FirstAccuracy;
    public int MissImprovement => FirstMisses - BestMisses;
    public int ComboImprovement => BestCombo - FirstCombo;
}


