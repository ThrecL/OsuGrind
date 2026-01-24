using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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

        public bool ComboSoundsEnabled { get; set; } = true;
        public bool AchievementSoundsEnabled { get; set; } = true;
        public bool PassSoundEnabled { get; set; } = true;
        public bool FailSoundEnabled { get; set; } = true;
        public bool DebugLoggingEnabled { get; set; } = false;
        public string? AccessToken { get; set; }
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

        if (dict.TryGetValue("comboSoundsEnabled", out var cse)) _current.ComboSoundsEnabled = IsTrue(cse);
        if (dict.TryGetValue("achievementSoundsEnabled", out var ase)) _current.AchievementSoundsEnabled = IsTrue(ase);
        if (dict.TryGetValue("passSoundEnabled", out var pse)) _current.PassSoundEnabled = IsTrue(pse);
        if (dict.TryGetValue("failSoundEnabled", out var fse)) _current.FailSoundEnabled = IsTrue(fse);
        if (dict.TryGetValue("debugLoggingEnabled", out var dle)) {
            _current.DebugLoggingEnabled = IsTrue(dle);
            DebugService.IsEnabled = _current.DebugLoggingEnabled;
        }
        if (dict.TryGetValue("accessToken", out var at)) _current.AccessToken = at?.ToString();
        
        Save();
    }

    private static bool IsTrue(object? val)
    {
        if (val == null) return false;
        if (val is bool b) return b;
        if (val is JsonElement je) return je.ValueKind == JsonValueKind.True;
        return val.ToString()?.ToLower() == "true";
    }
}
