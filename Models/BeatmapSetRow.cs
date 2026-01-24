using System;
using System.Collections.Generic;
using System.Linq;

namespace OsuGrind.Models;

/// <summary>
/// Represents a grouped mapset containing multiple difficulties.
/// Used for hierarchical display with expand/collapse.
/// </summary>
public class BeatmapSetRow
{
    public string SetKey => $"{Title}|{Artist}|{Mapper}";
    
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Mapper { get; set; } = "";
    
    // Aggregated from children
    public double TotalPP { get; set; }
    public double MinStars { get; set; }
    public double MaxStars { get; set; }
    public int TotalPlays { get; set; }
    public int DiffCount { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    
    // Display helpers
    public string DiffDisplay => DiffCount > 1 ? $"{DiffCount} diffs" : "1 diff";
    public string StarsDisplay => DiffCount > 1 ? $"{MinStars:F2}-{MaxStars:F2} ★" : $"{MaxStars:F2} ★";
    public string PPDisplay => TotalPP > 0 ? $"{TotalPP:F0}pp" : "—";

    // UI state
    public bool IsExpanded { get; set; } = false;
    
    // Child difficulties
    public List<BeatmapRow> Difficulties { get; set; } = new();
    
    public static List<BeatmapSetRow> GroupFromBeatmaps(IEnumerable<BeatmapRow> beatmaps)
    {
        return beatmaps
            .GroupBy(b => new { b.Title, b.Artist, b.Mapper })
            .Select(g => new BeatmapSetRow
            {
                Title = g.Key.Title,
                Artist = g.Key.Artist,
                Mapper = g.Key.Mapper,
                TotalPP = g.Sum(b => b.HighestPP),
                MinStars = g.Min(b => b.Stars),
                MaxStars = g.Max(b => b.Stars),
                TotalPlays = g.Sum(b => b.PlayCount),
                DiffCount = g.Count(),
                LastPlayedUtc = g.Max(b => b.LastPlayedUtc),
                Difficulties = g.OrderByDescending(b => b.Stars).ToList()
            })
            .OrderByDescending(s => s.TotalPlays)
            .ThenByDescending(s => s.TotalPP)
            .ToList();
    }
}

