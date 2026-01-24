using System;

namespace OsuGrind.Models;

public class BeatmapRow
{
    public string Hash { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Mapper { get; set; } = "";
    public string Version { get; set; } = ""; // Difficulty Name

    public double CS { get; set; }
    public double AR { get; set; }
    public double OD { get; set; }
    public double HP { get; set; }
    public double BPM { get; set; }
    public double Stars { get; set; }

    public double LengthMs { get; set; }
    public int Circles { get; set; }
    public int Sliders { get; set; }
    public int Spinners { get; set; }
    public int MaxCombo { get; set; }

    public string Status { get; set; } = "unknown"; // ranked, loved, etc.

    public int PlayCount { get; set; }
    public int PassCount { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public double HighestPP { get; set; }
    public string? BackgroundHash { get; set; }
    public string? OsuFilePath { get; set; } // Path/Hash to .osu file in Lazer 'files'
}
