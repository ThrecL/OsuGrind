using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using OsuGrind.Import;

namespace OsuGrind.Services;


public class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OsuGrind", "settings.json"
    );

    public class AppSettings
    {
        public string? LazerPath { get; set; }
        public string? StablePath { get; set; }

        public bool PassSoundEnabled { get; set; } = true;
        public bool FailSoundEnabled { get; set; } = true;
        public bool GoalSoundEnabled { get; set; } = true;
        public bool DebugLoggingEnabled { get; set; } = false;
        public string? AccessToken { get; set; }
        public string? Username { get; set; }
        public double PeakPP { get; set; }

        // Daily Goals
        public int GoalPlays { get; set; } = 20;
        public int GoalHits { get; set; } = 5000;
        public double GoalStars { get; set; } = 5.0;
        public int GoalPP { get; set; } = 100;
        
        public string? UniqueId { get; set; } // For anonymous tracking
    }



    private static AppSettings _current = new();
    public static AppSettings Current => _current;

    static SettingsManager()
    {
        Load();
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _current = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSettings();
                DebugService.IsEnabled = _current.DebugLoggingEnabled;
            }
            else
            {
                _current = new AppSettings();
            }

            // Auto-detect paths if missing
            if (string.IsNullOrEmpty(_current.StablePath))
            {
                _current.StablePath = OsuStableImportService.AutoDetectStablePath();
            }
            
            // Generate Unique ID if missing
            if (string.IsNullOrEmpty(_current.UniqueId))
            {
                _current.UniqueId = Guid.NewGuid().ToString();
                Save();
            }
            else if (string.IsNullOrEmpty(_current.StablePath)) // Save if path was detected but ID existed
            {
                 Save();
            }

        }
        catch
        {
            _current = new AppSettings();
        }
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            
            var json = JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    public static void UpdateFromDictionary(Dictionary<string, object> dict)
    {
        // Simple mapping
        if (dict.TryGetValue("lazerPath", out var lp)) _current.LazerPath = lp?.ToString();
        if (dict.TryGetValue("stablePath", out var sp)) _current.StablePath = sp?.ToString();

        if (dict.TryGetValue("passSoundEnabled", out var pse)) _current.PassSoundEnabled = IsTrue(pse);
        if (dict.TryGetValue("failSoundEnabled", out var fse)) _current.FailSoundEnabled = IsTrue(fse);
        if (dict.TryGetValue("goalSoundEnabled", out var gse)) _current.GoalSoundEnabled = IsTrue(gse);

        if (dict.TryGetValue("debugLoggingEnabled", out var dle)) {
            _current.DebugLoggingEnabled = IsTrue(dle);
            DebugService.IsEnabled = _current.DebugLoggingEnabled;
        }
        if (dict.TryGetValue("accessToken", out var at)) _current.AccessToken = at?.ToString();
        if (dict.TryGetValue("username", out var un)) _current.Username = un?.ToString();
        
        if (dict.TryGetValue("goalPlays", out var gp)) _current.GoalPlays = TryGetInt(gp);
        if (dict.TryGetValue("goalHits", out var gh)) _current.GoalHits = TryGetInt(gh);
        if (dict.TryGetValue("goalStars", out var gs)) _current.GoalStars = TryGetDouble(gs);
        if (dict.TryGetValue("goalPP", out var gpp)) _current.GoalPP = TryGetInt(gpp);

        Save();
    }


    private static bool IsTrue(object? val)
    {
        if (val == null) return false;
        if (val is bool b) return b;
        if (val is JsonElement je) return je.ValueKind == JsonValueKind.True;
        return val.ToString()?.ToLower() == "true";
    }

    private static int TryGetInt(object? val)
    {
        if (val == null) return 0;
        if (val is int i) return i;
        if (val is JsonElement je) return je.TryGetInt32(out int result) ? result : 0;
        int.TryParse(val.ToString(), out int r);
        return r;
    }

    private static double TryGetDouble(object? val)
    {
        if (val == null) return 0;
        if (val is double d) return d;
        if (val is float f) return f;
        if (val is JsonElement je) return je.TryGetDouble(out double result) ? result : 0;
        double.TryParse(val.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r);
        return r;
    }
}
