using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using OsuGrind.Services;

namespace OsuGrind.LiveReading;

public static class Offsets
{
    private static Dictionary<string, Dictionary<string, int>> _offsets = new();
    private static Dictionary<string, Dictionary<string, string>> _stringOffsets = new();
    private static string _version = "Unknown";
    private static readonly object _lock = new();
    private static string? _offsetsFilePath;

    private const string TosuOffsetsUrl = "https://raw.githubusercontent.com/tosuapp/tosu/refs/heads/master/packages/tosu/src/assets/offsets.json";

    public static string Version => _version;
    public static bool IsLoaded => _offsets.Count > 0;

    public static void Load(string? basePath = null)
    {
        lock (_lock)
        {
            try
            {
                var possiblePaths = new[]
                {
                    Path.Combine(basePath ?? AppDomain.CurrentDomain.BaseDirectory, "Resources", "LiveReading", "offsets.json"),
                    Path.Combine(basePath ?? AppDomain.CurrentDomain.BaseDirectory, "offsets.json"),
                    "Resources/LiveReading/offsets.json",
                    "offsets.json"
                };

                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        _offsetsFilePath = path;
                        var json = File.ReadAllText(path);
                        if (string.IsNullOrWhiteSpace(json) || json.Contains("Initial"))
                        {
                            Task.Run(async () => await FetchFromTosuAsync()).Wait(10000);
                        }
                        else
                        {
                            ParseOffsets(json);
                            DebugService.Log($"Loaded offsets (Version: {_version})", "Offsets");
                        }
                        return;
                    }
                }
                _offsetsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "LiveReading", "offsets.json");
                Task.Run(async () => await FetchFromTosuAsync()).Wait(10000);
            }
            catch (Exception ex) { DebugService.Log($"Error loading offsets: {ex.Message}", "Offsets"); }
        }
    }

    private static void ParseOffsets(string json)
    {
        _offsets.Clear();
        _stringOffsets.Clear();
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "OsuVersion") { _version = prop.Value.GetString() ?? "Unknown"; continue; }
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var classOffsets = new Dictionary<string, int>();
                var classStrings = new Dictionary<string, string>();
                foreach (var field in prop.Value.EnumerateObject())
                {
                    if (field.Value.ValueKind == JsonValueKind.Number) classOffsets[field.Name] = field.Value.GetInt32();
                    else if (field.Value.ValueKind == JsonValueKind.String) classStrings[field.Name] = field.Value.GetString() ?? "";
                }
                _offsets[prop.Name] = classOffsets;
                _stringOffsets[prop.Name] = classStrings;
            }
        }
    }

    public static int Get(string className, string fieldName, int defaultValue = 0)
    {
        lock (_lock)
        {
            if (_offsets.TryGetValue(className, out var classOffsets) && classOffsets.TryGetValue(fieldName, out var offset)) return offset;
            var backing = $"<{fieldName}>k__BackingField";
            if (_offsets.TryGetValue(className, out classOffsets) && classOffsets.TryGetValue(backing, out offset)) return offset;
            return defaultValue;
        }
    }

    public static string GetString(string className, string fieldName, string defaultValue = "")
    {
        lock (_lock)
        {
            if (_stringOffsets.TryGetValue(className, out var classStrings) && classStrings.TryGetValue(fieldName, out var val)) return val;
            return defaultValue;
        }
    }

    public static async Task<bool> FetchFromTosuAsync()
    {
        try
        {
            using var client = new HttpClient();
            var json = await client.GetStringAsync(TosuOffsetsUrl);
            lock (_lock) { ParseOffsets(json); }
            _offsetsFilePath ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "LiveReading", "offsets.json");
            var dir = Path.GetDirectoryName(_offsetsFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_offsetsFilePath, json);
            return true;
        }
        catch { return false; }
    }

    // Accessors
    public static class OsuGame { public static int osuLogo => Get("osu.Game.OsuGame", "osuLogo"); public static int ScreenStack => Get("osu.Game.OsuGame", "ScreenStack"); }
    public static class OsuGameBase { public static int API => Get("osu.Game.OsuGameBase", "API"); public static int MultiplayerClient => Get("osu.Game.OsuGameBase", "MultiplayerClient"); public static int Beatmap => Get("osu.Game.OsuGameBase", "Beatmap"); public static int beatmapClock => Get("osu.Game.OsuGameBase", "beatmapClock"); public static int realm => Get("osu.Game.OsuGameBase", "realm"); public static int SpectatorClient => Get("osu.Game.OsuGameBase", "SpectatorClient"); }
    public static class OsuGameDesktop { public static int AvailableMods => Get("osu.Desktop.OsuGameDesktop", "AvailableMods"); public static int SelectedMods => Get("osu.Desktop.OsuGameDesktop", "SelectedMods"); public static int Ruleset => Get("osu.Desktop.OsuGameDesktop", "Ruleset"); }
    public static class SoloSongSelect { public static int game => Get("osu.Game.Screens.SelectV2.SoloSongSelect", "game"); }
    public static class Player { public static int api => Get("osu.Game.Screens.Play.Player", "api"); public static int Score => Get("osu.Game.Screens.Play.Player", "Score"); public static int ScoreProcessor => Get("osu.Game.Screens.Play.Player", "ScoreProcessor"); public static int HealthProcessor => Get("osu.Game.Screens.Play.Player", "HealthProcessor"); public static int HUDOverlay => Get("osu.Game.Screens.Play.Player", "HUDOverlay"); }
    public static class SubmittingPlayer { public static int api => Get("osu.Game.Screens.Play.SubmittingPlayer", "api"); public static int spectatorClient => Get("osu.Game.Screens.Play.SubmittingPlayer", "spectatorClient"); }
    public static class PlayerLoader { public static int osuLogo => Get("osu.Game.Screens.Play.PlayerLoader", "osuLogo"); }
    public static class SpectatorPlayer { public static int score => Get("osu.Game.Screens.Play.SpectatorPlayer", "score"); }
    public static class HUDOverlay { public static int mainComponents => Get("osu.Game.Screens.Play.HUDOverlay", "mainComponents", 0x90); }
    public static class SoloResultsScreen { public static int api => Get("osu.Game.Screens.Ranking.SoloResultsScreen", "api"); public static int SelectedScore => Get("osu.Game.Screens.Ranking.SoloResultsScreen", "SelectedScore"); }
    public static class Editor { public static int realm => Get("osu.Game.Screens.Edit.Editor", "realm"); public static int api => Get("osu.Game.Screens.Edit.Editor", "api"); }
    public static class Multiplayer { public static int client => Get("osu.Game.Screens.OnlinePlay.Multiplayer.Multiplayer", "client"); }
    public static class APIAccess { public static int game => Get("osu.Game.Online.API.APIAccess", "game"); }
    public static class OnlineMultiplayerClient { public static int IsConnected => Get("osu.Game.Online.Multiplayer.OnlineMultiplayerClient", "IsConnected"); }
    public static class MultiplayerClient { public static int room => Get("osu.Game.Online.Multiplayer.MultiplayerClient", "room"); public static int APIRoom => Get("osu.Game.Online.Multiplayer.MultiplayerClient", "APIRoom"); }
    public static class ScoreInfo { public static int TotalScore => Get("osu.Game.Scoring.ScoreInfo", "TotalScore"); public static int MaxCombo => Get("osu.Game.Scoring.ScoreInfo", "MaxCombo"); public static int Combo => Get("osu.Game.Scoring.ScoreInfo", "Combo"); public static int Accuracy => Get("osu.Game.Scoring.ScoreInfo", "Accuracy"); public static int ModsJson => Get("osu.Game.Scoring.ScoreInfo", "ModsJson"); public static int Date => Get("osu.Game.Scoring.ScoreInfo", "Date"); public static int statistics => Get("osu.Game.Scoring.ScoreInfo", "statistics"); public static int maximumStatistics => Get("osu.Game.Scoring.ScoreInfo", "maximumStatistics"); }
    public static class BeatmapManagerWorkingBeatmap { public static int BeatmapInfo => Get("osu.Game.Beatmaps.WorkingBeatmapCache+BeatmapManagerWorkingBeatmap", "BeatmapInfo"); public static int BeatmapSetInfo => Get("osu.Game.Beatmaps.WorkingBeatmapCache+BeatmapManagerWorkingBeatmap", "BeatmapSetInfo"); }
    public static class BeatmapInfo { public static int MD5Hash => Get("osu.Game.Beatmaps.BeatmapInfo", "MD5Hash"); public static int Metadata => Get("osu.Game.Beatmaps.BeatmapInfo", "Metadata"); public static int Difficulty => Get("osu.Game.Beatmaps.BeatmapInfo", "Difficulty"); public static int DifficultyName => Get("osu.Game.Beatmaps.BeatmapInfo", "DifficultyName"); public static int TotalObjectCount => Get("osu.Game.Beatmaps.BeatmapInfo", "TotalObjectCount"); public static int StatusInt => Get("osu.Game.Beatmaps.BeatmapInfo", "StatusInt"); public static int Hash => Get("osu.Game.Beatmaps.BeatmapInfo", "Hash"); public static int Length => Get("osu.Game.Beatmaps.BeatmapInfo", "Length"); }
    public static class BeatmapMetadata { public static int Title => Get("osu.Game.Beatmaps.BeatmapMetadata", "Title"); public static int Artist => Get("osu.Game.Beatmaps.BeatmapMetadata", "Artist"); public static int BackgroundFile => Get("osu.Game.Beatmaps.BeatmapMetadata", "BackgroundFile"); }
    public static class BeatmapDifficulty { public static int DrainRate => Get("osu.Game.Beatmaps.BeatmapDifficulty", "DrainRate"); public static int CircleSize => Get("osu.Game.Beatmaps.BeatmapDifficulty", "CircleSize"); public static int OverallDifficulty => Get("osu.Game.Beatmaps.BeatmapDifficulty", "OverallDifficulty"); public static int ApproachRate => Get("osu.Game.Beatmaps.BeatmapDifficulty", "ApproachRate"); }
    public static class OsuScoreProcessor { public static int Combo => Get("osu.Game.Rulesets.Osu.Scoring.OsuScoreProcessor", "Combo"); }
    public static class ScoreProcessor { public static int hitEvents => Get("osu.Game.Rulesets.Scoring.ScoreProcessor", "hitEvents"); }
    public static class OsuHealthProcessor { public static int Health => Get("osu.Game.Rulesets.Osu.Scoring.OsuHealthProcessor", "Health"); }
    public static class RulesetInfo { public static int OnlineID => Get("osu.Game.Rulesets.RulesetInfo", "OnlineID"); }
    public static class SkinnableContainer { public static int components => Get("osu.Game.Skinning.SkinnableContainer", "components", 0x38); }
    public static class BindableList { public static int list => Get("osu.Framework.Bindables.BindableList", "list", 0x18); }
    public static class RollingCounter { public static int current => Get("osu.Game.Graphics.UserInterface.RollingCounter", "current", 0x80); }
    public static class Bindable { public static int Value => Get("osu.Framework.Bindables.Bindable", "Value", 0x10); }
    public static class FramedBeatmapClock { public static int finalClockSource => Get("osu.Game.Beatmaps.FramedBeatmapClock", "finalClockSource"); public static int rate => Get("osu.Game.Beatmaps.FramedBeatmapClock", "rate", 544); }
    public static class FramedClock { public static int CurrentTime => Get("osu.Framework.Timing.FramedClock", "CurrentTime"); }
    public static class ScreenStack { public static int stack => Get("osu.Framework.Screens.ScreenStack", "stack"); }
    public static class BeatmapSetInfo { public static int Files => Get("osu.Game.Beatmaps.BeatmapSetInfo", "Files", 0x20); }
    public static class RealmFile { public static int Hash => Get("Realm.RealmFile", "Hash", 0x18); }
    
    public static class ExternalLinkOpener { public static int api => Get("osu.Game.Online.Chat.ExternalLinkOpener", "api"); }
    public static class RealmNamedFileUsage { public static int File => Get("Realm.RealmNamedFileUsage", "File", 0x18); public static int Filename => Get("Realm.RealmNamedFileUsage", "Filename", 0x20); }

    public static class Stable
    {
        public static string BaseSignature => GetString("Stable", "BaseSignature", "F8 01 74 04 83 65");
        public static string StatusSignature => GetString("Stable", "StatusSignature", "48 83 F8 04 73 1E");
        
        public static int ReplayFlagOffset => Get("Stable", "ReplayFlagOffset", 0x2A);
        
        public static int BeatmapPtrOffset => Get("Stable", "BeatmapPtrOffset", -12);
        public static int StatusPtrOffset => Get("Stable", "StatusPtrOffset", -60);
        public static int PlayTimePtrOffset => Get("Stable", "PlayTimePtrOffset", 100);
        public static int PlayTimeValueOffset => Get("Stable", "PlayTimeValueOffset", -16);
        
        public static int RulesetRulesetPtrOffset => Get("Stable", "RulesetRulesetPtrOffset", 11);
        public static int RulesetRulesetPtrAdd => Get("Stable", "RulesetRulesetPtrAdd", 4);

        public static int GameplayBaseOffset => Get("Stable", "GameplayBaseOffset", 104);
        public static int ScoreBaseOffset => Get("Stable", "ScoreBaseOffset", 56);
        public static int HPBarBaseOffset => Get("Stable", "HPBarBaseOffset", 64);
        public static int AccuracyBaseOffset => Get("Stable", "AccuracyBaseOffset", 72);

        public static int ScoreOffset => Get("Stable", "ScoreOffset", 120);
        public static int ComboOffset => Get("Stable", "ComboOffset", 148);
        public static int MaxComboOffset => Get("Stable", "MaxComboOffset", 104);
        public static int AccuracyOffset => Get("Stable", "AccuracyOffset", 20); // Tool says { 72, 20 }
        
        public static int Hit300Offset => Get("Stable", "Hit300Offset", 138);
        public static int Hit100Offset => Get("Stable", "Hit100Offset", 136);
        public static int Hit50Offset => Get("Stable", "Hit50Offset", 140);
        public static int HitMissOffset => Get("Stable", "HitMissOffset", 146);
        public static int HitGekiOffset => Get("Stable", "HitGekiOffset", 142);
        public static int HitKatuOffset => Get("Stable", "HitKatuOffset", 144);
    }
}
