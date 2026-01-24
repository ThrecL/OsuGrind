using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;

namespace OsuGrind.Services;

/// <summary>
/// Mod category colors based on osu!lazer's mod grouping
/// </summary>
public static class ModIconHelper
{
    // Color scheme from osu!lazer mod categories
    public static readonly System.Windows.Media.Color DifficultyReduction = System.Windows.Media.Color.FromRgb(0x88, 0xCC, 0x88); // Green
    public static readonly System.Windows.Media.Color DifficultyIncrease = System.Windows.Media.Color.FromRgb(0xFF, 0x44, 0x44); // Red
    public static readonly System.Windows.Media.Color Automation = System.Windows.Media.Color.FromRgb(0x66, 0xCC, 0xFF);          // Blue
    public static readonly System.Windows.Media.Color Conversion = System.Windows.Media.Color.FromRgb(0xBB, 0x88, 0xFF);          // Purple
    public static readonly System.Windows.Media.Color Fun = System.Windows.Media.Color.FromRgb(0xFF, 0x88, 0xCC);                 // Pink

    // Mod acronym to filename mapping
    private static readonly Dictionary<string, string> ModFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Difficulty Reduction
        ["EZ"] = "mod-easy.png",
        ["NF"] = "mod-no-fail.png",
        ["HT"] = "mod-half-time.png",
        ["DC"] = "mod-daycore.png",
        
        // Difficulty Increase
        ["HR"] = "mod-hard-rock.png",
        ["SD"] = "mod-sudden-death.png",
        ["PF"] = "mod-perfect.png",
        ["DT"] = "mod-double-time.png",
        ["NC"] = "mod-nightcore.png",
        ["HD"] = "mod-hidden.png",
        ["FL"] = "mod-flashlight.png",
        ["BL"] = "mod-blinds.png",
        ["ST"] = "mod-strict-tracking.png",
        ["AC"] = "mod-accuracy-challenge.png",
        ["FI"] = "mod-fade-in.png",
        
        // Automation
        ["AT"] = "mod-autoplay.png",
        ["CN"] = "mod-cinema.png",
        ["RX"] = "mod-relax.png",
        ["AP"] = "mod-autopilot.png",
        ["SO"] = "mod-spun-out.png",
        
        // Conversion
        ["TP"] = "mod-target-practice.png",
        ["DA"] = "mod-difficulty-adjust.png",
        ["CL"] = "mod-classic.png",
        ["RD"] = "mod-random.png",
        ["MR"] = "mod-mirror.png",
        ["AL"] = "mod-alternate.png",
        ["SG"] = "mod-single-tap.png",
        
        // Fun
        ["TR"] = "mod-transform.png",
        ["WG"] = "mod-wiggle.png",
        ["SI"] = "mod-spin-in.png",
        ["GR"] = "mod-grow.png",
        ["DF"] = "mod-deflate.png",
        ["WU"] = "mod-wind-up.png",
        ["WD"] = "mod-wind-down.png",
        ["TC"] = "mod-traceable.png",
        ["BR"] = "mod-barrel-roll.png",
        ["AD"] = "mod-approach-different.png",
        ["BM"] = "mod-bloom.png",
        ["BB"] = "mod-bubbles.png",
        ["DP"] = "mod-depth.png",
        ["FF"] = "mod-floating-fruits.png",
        ["FR"] = "mod-freeze-frame.png",
        ["IN"] = "mod-invert.png",
        ["MG"] = "mod-magnetised.png",
        ["MF"] = "mod-moving-fast.png",
        ["MD"] = "mod-muted.png",
        ["NR"] = "mod-no-release.png",
        ["NS"] = "mod-no-scope.png",
        ["RP"] = "mod-repel.png",
        ["SR"] = "mod-simplified-rhythm.png",
        ["SW"] = "mod-swap.png",
        ["SY"] = "mod-synesthesia.png",
        ["AS"] = "mod-adaptive-speed.png",
        ["CS"] = "mod-constant-speed.png",
        ["HO"] = "mod-hold-off.png",
        
        // Keys
        ["1K"] = "mod-one-key.png",
        ["2K"] = "mod-two-key.png",
        ["3K"] = "mod-three-key.png",
        ["4K"] = "mod-four-keys.png",
        ["5K"] = "mod-five-keys.png",
        ["6K"] = "mod-six-keys.png",
        ["7K"] = "mod-seven-keys.png",
        ["8K"] = "mod-eight-keys.png",
        ["9K"] = "mod-nine-keys.png",
        ["10K"] = "mod-ten-keys.png",
        ["DS"] = "mod-dual-stages.png",
        
        // Other
        ["TD"] = "mod-touch-device.png",
        ["SV2"] = "mod-score-v2.png",
        ["NM"] = "mod-no-mod.png",
    };

    // Mod category assignments
    private static readonly HashSet<string> ReductionMods = new(StringComparer.OrdinalIgnoreCase) 
        { "EZ", "NF", "HT", "DC" };
    private static readonly HashSet<string> IncreaseMods = new(StringComparer.OrdinalIgnoreCase) 
        { "HR", "SD", "PF", "DT", "NC", "HD", "FL", "BL", "ST", "AC", "FI" };
    private static readonly HashSet<string> AutomationMods = new(StringComparer.OrdinalIgnoreCase) 
        { "AT", "CN", "RX", "AP", "SO" };
    private static readonly HashSet<string> ConversionMods = new(StringComparer.OrdinalIgnoreCase) 
        { "TP", "DA", "CL", "RD", "MR", "AL", "SG" };
    private static readonly HashSet<string> FunMods = new(StringComparer.OrdinalIgnoreCase) 
        { "TR", "WG", "SI", "GR", "DF", "WU", "WD", "TC", "BR", "AD", "BM", "BB", "DP", "FF", "FR", "IN", "MG", "MF", "MD", "NR", "NS", "RP", "SR", "SW", "SY", "AS", "CS", "HO" };

    /// <summary>
    /// Get mod icon file path for a given acronym
    /// </summary>
    public static string? GetModIconPath(string acronym)
    {
        if (ModFileNames.TryGetValue(acronym.ToUpperInvariant(), out var fileName))
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Mods", fileName);
            return File.Exists(path) ? path : null;
        }
        return null;
    }

    /// <summary>
    /// Get category color for a mod acronym
    /// </summary>
    public static System.Windows.Media.Color GetModColor(string acronym)
    {
        var upper = acronym.ToUpperInvariant();
        if (ReductionMods.Contains(upper)) return DifficultyReduction;
        if (IncreaseMods.Contains(upper)) return DifficultyIncrease;
        if (AutomationMods.Contains(upper)) return Automation;
        if (ConversionMods.Contains(upper)) return Conversion;
        if (FunMods.Contains(upper)) return Fun;
        return Colors.Gray;
    }

    /// <summary>
    /// Get hex color string for a mod acronym
    /// </summary>
    public static string GetModColorHex(string acronym)
    {
        var color = GetModColor(acronym);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>
    /// Parse a mods string like "HDDT" or "HDDTHR" into individual acronyms
    /// </summary>
    public static List<string> ParseModsString(string mods)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(mods) || mods == "NM")
            return result;

        // Known two-letter and three-letter mods
        var knownMods = new[] { "EZ", "NF", "HT", "DC", "HR", "SD", "PF", "DT", "NC", "HD", "FL", "BL", 
            "ST", "AC", "FI", "AT", "CN", "RX", "AP", "SO", "TP", "DA", "CL", "RD", "MR", "AL", "SG",
            "TR", "WG", "SI", "GR", "DF", "WU", "WD", "TC", "BR", "AD", "TD", "SV2",
            "BM", "BB", "DP", "FF", "FR", "IN", "MG", "MF", "MD", "NR", "NS", "RP", "SR", "SW", "SY", "AS", "CS", "HO",
            "1K", "2K", "3K", "4K", "5K", "6K", "7K", "8K", "9K", "10K", "DS" };

        var remaining = mods.ToUpperInvariant();
        
        // First try 3-letter mods
        foreach (var mod in knownMods.Where(m => m.Length == 3))
        {
            while (remaining.Contains(mod))
            {
                result.Add(mod);
                remaining = remaining.Replace(mod, "");
            }
        }
        
        // Then 2-letter mods
        foreach (var mod in knownMods.Where(m => m.Length == 2))
        {
            while (remaining.Contains(mod))
            {
                result.Add(mod);
                remaining = remaining.Replace(mod, "");
            }
        }

        return result;
    }
}
