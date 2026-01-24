using System;
using System.Collections.Generic;

namespace OsuGrind.Services;

public static class SkillBadgeHelper
{
    private static readonly Dictionary<string, string> TitleColors = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Hardy", "#464ac1" },
        { "Tenacious", "#ff0066" },
        { "Swift", "#fcc013" },
        { "Perceptive", "#24d8fe" },
        { "Volcanic", "#ef525b" },
        { "Furious", "#f8095c" },
        { "Sturdy", "#1bad58" },
        { "Adventurous", "#79de4f" },
        { "Adamant", "#4dceff" },
        { "Spirited", "#d0dc05" },
        { "Berserk", "#b00106" },
        { "Fearless", "#a8157d" },
        { "Frantic", "#468c00" },
        { "Volatile", "#dc4ad2" },
        { "Versatile", "#e9ce14" },
        { "Ambitious", "#46d1a7" },
        { "Sage", "#1baec0" },
        { "Sharpshooter", "#9b1400" },
        { "Psychic", "#66d9b7" },
        { "Pirate", "#d90606" },
        { "Seer", "#1368bd" },
        { "Sniper", "#519216" },
        { "Daredevil", "#c01900" }
    };

    public static string GetColorHex(string title)
    {
        if (TitleColors.TryGetValue(title, out var color))
            return color;
        return "#888888"; // Default grey for unknown titles
    }
}
