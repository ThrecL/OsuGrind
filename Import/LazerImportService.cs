using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OsuGrind.Services;
using OsuGrind.Models;
using Realms;
using System.Globalization;
using OsuGrind.Api;

namespace OsuGrind.Services;

/// <summary>
/// Imports scores from osu!lazer's client.realm database using dynamic Realm access.
/// This avoids schema conflicts by not requiring typed model classes.
/// </summary>
public class LazerImportService
{
    private readonly TrackerDb db;

    private class ExtractedScore
    {
        public string Username { get; set; } = "";
        public string RulesetShortName { get; set; } = "";
        public double Accuracy { get; set; }
        public int Combo { get; set; }
        public int Rank { get; set; }
        public DateTime Date { get; set; }
        public double PP { get; set; }
        public long TotalScore { get; set; }
        public string BeatmapHash { get; set; } = "";
        public string ReplayHash { get; set; } = "";
        public List<double> HitOffsets { get; set; } = new();
        public string BeatmapTitle { get; set; } = "Unknown";
        public string BeatmapArtist { get; set; } = "Unknown";
        public string BeatmapVersion { get; set; } = "";
        public double BeatmapLength { get; set; }
        public double StarRating { get; set; }
        public string ModsJson { get; set; } = "[]";
        public string StatisticsJson { get; set; } = "{}";
        public int SliderTailHit { get; set; }
        public int SmallTickHit { get; set; }
        public int LargeTickHit { get; set; }
        public int SmallBonus { get; set; }
        public int LargeBonus { get; set; }
    }

    public LazerImportService(TrackerDb db)
    {
        this.db = db;
    }

    public async Task<(int Added, int Skipped, string? Error)> ImportScoresAsync(string? lazerFolderPath = null, string? targetUsername = null)
    {
        DebugService.Log($"ImportScoresAsync started", "LazerImportService");
        CleanupTempRealmFiles();
        
        string realmPath = GetRealmPath(lazerFolderPath);
        string lazerFilesPath = Path.Combine(Path.GetDirectoryName(realmPath) ?? "", "files");
        DebugService.Log($"Realm path: {realmPath}", "LazerImportService");

        if (!File.Exists(realmPath))
            return (0, 0, $"Realm file not found: {realmPath}");

        // Copy realm to temp to avoid file locks
        string? tempRealmPath = CopyRealmToTemp(realmPath);
        if (tempRealmPath == null)
            return (0, 0, $"Failed to copy realm file. Is osu!lazer running? Try closing it first.");
        
        string workingPath = tempRealmPath;

        ulong schemaVersion;
        try { schemaVersion = GetSchemaVersion(workingPath); }
        catch (Exception ex) { return (0, 0, $"Failed to detect version: {ex.Message}"); }
        
        DebugService.Log($"Schema version: {schemaVersion}", "LazerImportService");

        var config = new RealmConfiguration(workingPath)
        {
            IsReadOnly = true,
            SchemaVersion = schemaVersion,
            IsDynamic = true
        };

        using var rosu = new RosuService();

        try
        {
            // 1. Import Beatmaps and get Hash-to-Path mapping
            var (mapsAdded, mapsSkipped, hashToPath) = await ImportBeatmaps(config, rosu, lazerFolderPath);
            DebugService.Log($"Beatmaps processed: Added={mapsAdded}, Skipped={mapsSkipped}", "LazerImportService");
            
            // 2. Import Scores
            var extractedScores = ExtractScoresDynamic(config, targetUsername);
            DebugService.Log($"Extracted {extractedScores.Count} scores from Realm", "LazerImportService");
            
            int scoresAdded = 0;
            int scoresSkipped = 0;
            
            // PERFORMANCE: Cache beatmaps once instead of querying DB for every score
            var allBeatmaps = await db.GetBeatmapsAsync();
            var beatmapByHash = new Dictionary<string, BeatmapRow>();
            foreach (var bm in allBeatmaps)
            {
                if (!string.IsNullOrEmpty(bm.Hash)) beatmapByHash[bm.Hash] = bm;
            }

            // DEDUPLICATION: Load existing signatures to skip duplicates
            var existingSignatures = await db.GetExistingScoreSignaturesAsync();

            var batchPlays = new List<PlayRow>();

            foreach (var s in extractedScores)
            {
                if (string.IsNullOrEmpty(s.BeatmapHash))
                {
                    scoresSkipped++;
                    continue;
                }

                // DEDUPLICATION CHECK
                string sig = $"{s.BeatmapHash}|{s.TotalScore}|{s.Date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}";
                if (existingSignatures.Contains(sig))
                {
                    scoresSkipped++;
                    continue;
                }
                existingSignatures.Add(sig);

                // Parse Hits
                var statsDic = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, int>>(s.StatisticsJson ?? "{}") ?? new();
                int n300 = statsDic.GetValueOrDefault("great", 0) + statsDic.GetValueOrDefault("Great", 0);
                int n100 = statsDic.GetValueOrDefault("ok", 0) + statsDic.GetValueOrDefault("Ok", 0);
                int n50 = statsDic.GetValueOrDefault("meh", 0) + statsDic.GetValueOrDefault("Meh", 0);
                int nMiss = statsDic.GetValueOrDefault("miss", 0) + statsDic.GetValueOrDefault("Miss", 0);

                // Analyze Lazer HitEvents for UR and HitErrors
                double lazerUR = 0;
                if (s.HitOffsets != null && s.HitOffsets.Count > 0)
                {
                    double avg = s.HitOffsets.Average();
                    double sumSq = s.HitOffsets.Sum(d => Math.Pow(d - avg, 2));
                    lazerUR = Math.Sqrt(sumSq / s.HitOffsets.Count) * 10;
                }

                // Parse Mods
                List<string> modsListAcronyms = new();
                uint modsBits = 0;
                double clockRate = 1.0;
                try
                {
                    var modsJArray = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Newtonsoft.Json.Linq.JObject>>(s.ModsJson ?? "[]");
                    if (modsJArray != null)
                    {
                        foreach (var m in modsJArray)
                        {
                            var acronym = m["acronym"]?.ToString();
                            if (!string.IsNullOrEmpty(acronym)) modsListAcronyms.Add(acronym);
                        }
                    }
                    modsBits = GetModsBits(modsListAcronyms);
                    clockRate = RosuService.GetClockRateFromMods(modsBits);
                }
                catch { }

                string modsString = modsListAcronyms.Count > 0 ? string.Join(",", modsListAcronyms) : "NM";

                // Calculate PP via Rosu if we have the file
                double calculatedPP = s.PP;
                
                int totalObjects = 0;
                if (beatmapByHash.TryGetValue(s.BeatmapHash, out var beatmap))
                {
                    totalObjects = beatmap.Circles + beatmap.Sliders + beatmap.Spinners;
                }

                if (hashToPath.TryGetValue(s.BeatmapHash, out string? osuPath) && File.Exists(osuPath))
                {
                    rosu.UpdateContext(osuPath);
                    int passedObjects = (s.Rank >= 0) ? -1 : 0; 
                    
                    var pp = rosu.CalculatePp(modsBits, s.Combo, n300, n100, n50, nMiss, passedObjects, s.SliderTailHit, s.SmallTickHit, s.LargeTickHit, clockRate);
                    if (pp > 0) calculatedPP = pp;
                }

                // Determine outcome and grade
                string outcome = s.Rank >= 0 ? "pass" : "fail";
                string grade = MapRankToGrade(s.Rank);

                // Score conversion (Standardized to Classic)
                long reportedScore = s.TotalScore;
                if (totalObjects > 0)
                {
                     double classicScore = ((Math.Pow(totalObjects, 2) * 32.57 + 100000) * reportedScore) / 1000000.0;
                     reportedScore = (long)Math.Round(classicScore);
                }

                var row = new PlayRow
                {
                    ScoreId = 0,
                    Beatmap = $"{s.BeatmapArtist} - {s.BeatmapTitle} [{s.BeatmapVersion}]",
                    BeatmapHash = s.BeatmapHash,
                    CreatedAtUtc = s.Date.ToUniversalTime(),
                    DurationMs = (int)s.BeatmapLength,
                    Outcome = outcome,
                    Rank = grade,
                    Accuracy = s.Accuracy,
                    Mods = modsString,
                    Score = reportedScore,
                    Combo = s.Combo,
                    Count300 = n300,
                    Count100 = n100,
                    Count50 = n50,
                    Misses = nMiss,
                    PP = calculatedPP,
                    Stars = s.StarRating,
                    UR = lazerUR,
                    HitErrorsJson = s.HitOffsets != null ? System.Text.Json.JsonSerializer.Serialize(s.HitOffsets) : null,
                    HitOffsets = string.Join(",", s.HitOffsets?.Select(o => o.ToString("F2")) ?? Enumerable.Empty<string>()),
                    ReplayHash = s.ReplayHash,
                    MapPath = osuPath ?? ""
                };

                // ANALYZE LAZER REPLAY FOR TAPPING STATS IF POSSIBLE
                if (!string.IsNullOrEmpty(s.ReplayHash) && !string.IsNullOrEmpty(osuPath))
                {
                    try {
                        string replayPath = Path.Combine(lazerFilesPath, s.ReplayHash.Substring(0, 1), s.ReplayHash.Substring(0, 2), s.ReplayHash);
                        if (File.Exists(replayPath))
                        {
                            DebugService.Log($"Analyzing lazer replay: {s.ReplayHash}", "LazerImportService");
                            var analysis = MissAnalysisService.Analyze(osuPath, replayPath);
                            // UR from MissAnalysis is often more consistent if beatmap is fully available
                            if (analysis.UR > 0) row.UR = analysis.UR;
                            row.KeyRatio = analysis.KeyRatio;
                            if (analysis.HitErrors.Count > 0) row.HitErrorsJson = System.Text.Json.JsonSerializer.Serialize(analysis.HitErrors);
                            DebugService.Log($"Analysis complete: UR={analysis.UR:F2}, KeyRatio={analysis.KeyRatio:P1}", "LazerImportService");
                        }
                    } catch (Exception ex) {
                         DebugService.Error($"Analysis failed for lazer replay {s.ReplayHash}: {ex.Message}", "LazerImportService");
                    }
                }

                batchPlays.Add(row);
                scoresAdded++;
            }

            if (batchPlays.Count > 0)
            {
                await db.InsertPlaysBatchAsync(batchPlays);
            }

            DebugService.Log($"Scores import complete: Added={scoresAdded}, Skipped={scoresSkipped}", "LazerImportService");
            return (scoresAdded, scoresSkipped, null);
        }
        catch (Exception ex)
        {
            DebugService.Error($"Lazer Import Exception: {ex}", "LazerImportService");
            return (0, 0, $"Import failed: {ex.Message}");
        }
    }

    private string MapRankToGrade(int rank)
    {
        return rank switch
        {
            0 => "D",
            1 => "C",
            2 => "B",
            3 => "A",
            4 => "S",
            5 => "SH",
            6 => "X",
            7 => "XH",
            _ => "F"
        };
    }

    private async Task<(int added, int skipped, Dictionary<string, string> hashToPath)> ImportBeatmaps(RealmConfiguration config, RosuService rosu, string? lazerFolderPath)
    {
        int added = 0;
        int skipped = 0;
        var hashToPath = new Dictionary<string, string>();
        string originalRealmPath = GetRealmPath(lazerFolderPath);
        string lazerFilesPath = Path.Combine(Path.GetDirectoryName(originalRealmPath) ?? "", "files");
        
        using var realm = Realm.GetInstance(config);
        
        IEnumerable<IRealmObjectBase> beatmaps;
        bool usingSets = false;

        if (realm.Schema.Any(s => s.Name == "BeatmapInfo"))
        {
             beatmaps = realm.DynamicApi.All("BeatmapInfo");
        }
        else if (realm.Schema.Any(s => s.Name == "BeatmapSet"))
        {
             usingSets = true;
             beatmaps = realm.DynamicApi.All("BeatmapSet");
        }
        else return (0, 0, hashToPath);

        foreach (var item in beatmaps)
        {
            if (usingSets)
            {
                var nestedMaps = item.DynamicApi.GetList<IRealmObjectBase>("Beatmaps");
                foreach (var b in nestedMaps)
                {
                    var (ok, path) = await ProcessBeatmap(b, rosu, lazerFilesPath);
                    if (ok) added++; else skipped++;
                    if (!string.IsNullOrEmpty(path))
                    {
                        var h = b.DynamicApi.Get<string>("MD5Hash") ?? b.DynamicApi.Get<string>("Hash");
                        if (!string.IsNullOrEmpty(h)) hashToPath[h] = path;
                    }
                }
            }
            else
            {
                var (ok, path) = await ProcessBeatmap(item, rosu, lazerFilesPath);
                if (ok) added++; else skipped++;
                if (!string.IsNullOrEmpty(path))
                {
                    var h = item.DynamicApi.Get<string>("MD5Hash") ?? item.DynamicApi.Get<string>("Hash");
                    if (!string.IsNullOrEmpty(h)) hashToPath[h] = path;
                }
            }
        }
        return (added, skipped, hashToPath);
    }

    private async Task<(bool ok, string? path)> ProcessBeatmap(IRealmObjectBase b, RosuService rosu, string lazerFilesPath)
    {
        try
        {
            string md5 = b.DynamicApi.Get<string>("MD5Hash") ?? "";
            string sha2 = b.DynamicApi.Get<string>("Hash") ?? "";
            string identifyingHash = !string.IsNullOrEmpty(md5) ? md5 : sha2;
            if (string.IsNullOrEmpty(identifyingHash)) return (false, null);

            var meta = b.DynamicApi.Get<IRealmObjectBase>("Metadata");
            var diff = b.DynamicApi.Get<IRealmObjectBase>("Difficulty");
            var ruleset = b.DynamicApi.Get<IRealmObjectBase>("Ruleset");
            
            if (ruleset != null && ruleset.DynamicApi.Get<string>("ShortName") != "osu") return (false, null);

            var row = new BeatmapRow
            {
                Hash = identifyingHash,
                Title = meta?.DynamicApi.Get<string>("Title") ?? "Unknown",
                Artist = meta?.DynamicApi.Get<string>("Artist") ?? "Unknown",
                Mapper = meta?.DynamicApi.Get<IRealmObjectBase>("Author")?.DynamicApi.Get<string>("Username") ?? "Unknown",
                Version = b.DynamicApi.Get<string>("DifficultyName") ?? "",
                Stars = b.DynamicApi.Get<double>("StarRating"),
                LengthMs = b.DynamicApi.Get<double>("Length"),
                BPM = b.DynamicApi.Get<double>("BPM"),
                Status = TryGetStatus(b),
                LastPlayedUtc = b.DynamicApi.Get<DateTimeOffset?>("LastPlayed")?.UtcDateTime ?? DateTime.MinValue,
                BackgroundHash = ExtractBackgroundHash(b)
            };

            if (diff != null)
            {
                row.CS = diff.DynamicApi.Get<float>("CircleSize");
                row.AR = diff.DynamicApi.Get<float>("ApproachRate");
                row.OD = diff.DynamicApi.Get<float>("OverallDifficulty");
                row.HP = diff.DynamicApi.Get<float>("DrainRate");
            }

            string? osuPath = null;
            var set = b.DynamicApi.Get<IRealmObjectBase>("BeatmapSet");
            if (set != null)
            {
                var files = set.DynamicApi.GetList<IRealmObjectBase>("Files");
                var match = files.FirstOrDefault(f => f.DynamicApi.Get<IRealmObjectBase>("File")?.DynamicApi.Get<string>("Hash") == sha2);
                if (match != null)
                {
                    var fileHash = match.DynamicApi.Get<IRealmObjectBase>("File")?.DynamicApi.Get<string>("Hash");
                    if (!string.IsNullOrEmpty(fileHash))
                    {
                        string physicalPath = Path.Combine(lazerFilesPath, fileHash.Substring(0, 1), fileHash.Substring(0, 2), fileHash);
                        if (File.Exists(physicalPath))
                        {
                            osuPath = physicalPath;
                            row.OsuFilePath = physicalPath;
                            var (c, s, sp) = ParseOsuFileCounts(physicalPath);
                            row.Circles = c; row.Sliders = s; row.Spinners = sp;
                            var perf = rosu.CalculatePpIfFc(physicalPath, new List<string>(), 100.0);
                            if (perf.MaxCombo > 0) row.MaxCombo = perf.MaxCombo;
                            if (row.Stars <= 0 && perf.Stars > 0) row.Stars = perf.Stars;
                        }
                    }
                }
            }

            await db.InsertOrUpdateBeatmapAsync(row);
            return (true, osuPath);
        }
        catch { return (false, null); }
    }

    private uint GetModsBits(List<string> mods)
    {
        uint bits = 0;
        foreach (var m in mods)
        {
            switch (m.ToUpper())
            {
                case "NF": bits |= 1; break;
                case "EZ": bits |= 2; break;
                case "TD": bits |= 4; break;
                case "HD": bits |= 8; break;
                case "HR": bits |= 16; break;
                case "SD": bits |= 32; break;
                case "DT": bits |= 64; break;
                case "RX": bits |= 128; break;
                case "HT": bits |= 256; break;
                case "DC": bits |= 256; break;
                case "NC": bits |= 512 | 64; break;
                case "FL": bits |= 1024; break;
                case "SO": bits |= 4096; break;
                case "AP": bits |= 8192; break;
                case "PF": bits |= 16384 | 32; break;
                case "CL": bits |= (1 << 24); break;
            }
        }
        return bits;
    }

    private (int circles, int sliders, int spinners) ParseOsuFileCounts(string path)
    {
        int c = 0, s = 0, sp = 0;
        try {
            bool inObjects = false;
            foreach (var line in File.ReadLines(path)) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("[")) { inObjects = line.Trim() == "[HitObjects]"; continue; }
                if (!inObjects) continue;
                var parts = line.Split(',');
                if (parts.Length > 3 && int.TryParse(parts[3], out int type)) {
                    if ((type & 1) > 0) c++; else if ((type & 2) > 0) s++; else if ((type & 8) > 0) sp++;
                }
            }
        } catch {}
        return (c, s, sp);
    }

    private string? ExtractBackgroundHash(IRealmObjectBase b)
    {
        try {
            var meta = b.DynamicApi.Get<IRealmObjectBase>("Metadata");
            string? bgFile = meta?.DynamicApi.Get<string>("BackgroundFile");
            if (string.IsNullOrEmpty(bgFile)) return null;
            var set = b.DynamicApi.Get<IRealmObjectBase>("BeatmapSet");
            if (set == null) return null;
            var files = set.DynamicApi.GetList<IRealmObjectBase>("Files");
            var match = files.FirstOrDefault(f => f.DynamicApi.Get<string>("Filename")?.Equals(bgFile, StringComparison.OrdinalIgnoreCase) == true);
            return match?.DynamicApi.Get<IRealmObjectBase>("File")?.DynamicApi.Get<string>("Hash");
        } catch { return null; }
    }

    private string TryGetStatus(IRealmObjectBase b)
    {
        try { return ((int)b.DynamicApi.Get<long>("StatusInt")).ToString(); } catch { }
        try { return b.DynamicApi.Get<string>("Status") ?? "Unknown"; } catch { }
        return "Unknown";
    }

    private string GetRealmPath(string? foldersPath)
    {
        if (!string.IsNullOrWhiteSpace(foldersPath)) {
            foldersPath = foldersPath.Trim();
            try {
                if (File.Exists(foldersPath)) return foldersPath;
                var p = Path.Combine(foldersPath, "client.realm");
                if (File.Exists(p)) return p;
            } catch {}
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[] {
            Path.Combine(appData, "osu", "client.realm"),
            Path.Combine(localAppData, "osu", "client.realm"),
            Path.Combine(appData, "osu!", "client.realm"),
            Path.Combine(localAppData, "osu!", "client.realm")
        };
        foreach (var p in candidates) if (File.Exists(p)) return p;
        return foldersPath ?? candidates[0];
    }

    private List<ExtractedScore> ExtractScoresDynamic(RealmConfiguration config, string? targetUsername)
    {
        var result = new List<ExtractedScore>();
        using var realm = Realm.GetInstance(config);
        if (!realm.Schema.Any(s => s.Name == "Score")) return result;
        var scores = realm.DynamicApi.All("Score");
        
        string likelyLocalUser = targetUsername;
        if (string.IsNullOrEmpty(likelyLocalUser)) {
            var counts = new Dictionary<string, int>();
            foreach (var score in scores) {
                try {
                    long onlineId = -1; try { onlineId = score.DynamicApi.Get<long>("OnlineID"); } catch {}
                    if (onlineId <= 0) {
                        var u = score.DynamicApi.Get<IRealmObjectBase>("User");
                        var name = u?.DynamicApi.Get<string>("Username");
                        if (!string.IsNullOrEmpty(name) && name != "Guest") {
                            if (!counts.ContainsKey(name)) counts[name] = 0;
                            counts[name]++;
                        }
                    }
                } catch {}
            }
            likelyLocalUser = counts.OrderByDescending(x => x.Value).FirstOrDefault().Key;
        }

        foreach (var score in scores) {
            try {
                var user = score.DynamicApi.Get<IRealmObjectBase>("User");
                string username = user?.DynamicApi.Get<string>("Username") ?? "Guest";

                // Apply the likelyLocalUser filter correctly
                if (username != "Guest" && !string.IsNullOrEmpty(likelyLocalUser) && username != likelyLocalUser) continue;

                var s = new ExtractedScore {

                    Username = username,
                    Accuracy = score.DynamicApi.Get<double>("Accuracy"),
                    Combo = score.DynamicApi.Get<int>("MaxCombo"),
                    Rank = score.DynamicApi.Get<int>("Rank"),
                    Date = score.DynamicApi.Get<DateTimeOffset>("Date").UtcDateTime,
                    PP = score.DynamicApi.Get<double?>("PP") ?? 0,
                    TotalScore = score.DynamicApi.Get<long>("TotalScore"),
                    ModsJson = score.DynamicApi.Get<string>("Mods") ?? "[]",
                    StatisticsJson = score.DynamicApi.Get<string>("Statistics") ?? "{}",
                    BeatmapHash = score.DynamicApi.Get<IRealmObjectBase>("BeatmapInfo")?.DynamicApi.Get<string>("MD5Hash") ?? score.DynamicApi.Get<IRealmObjectBase>("BeatmapInfo")?.DynamicApi.Get<string>("Hash") ?? ""
                };

                try {
                    var hitEvents = score.DynamicApi.GetList<IRealmObjectBase>("HitEvents");
                    foreach (var e in hitEvents) {
                        double? offset = e.DynamicApi.Get<double?>("TimeOffset");
                        if (offset.HasValue) s.HitOffsets.Add(offset.Value);
                    }
                } catch {}

                try {
                    var files = score.DynamicApi.GetList<IRealmObjectBase>("Files");
                    foreach (var f in files) {
                         string filename = f.DynamicApi.Get<string>("Filename");
                         if (string.IsNullOrEmpty(s.ReplayHash)) s.ReplayHash = f.DynamicApi.Get<IRealmObjectBase>("File")?.DynamicApi.Get<string>("Hash") ?? "";
                         if (!string.IsNullOrEmpty(filename) && (filename.EndsWith(".osr") || filename.Equals("replay"))) {
                             s.ReplayHash = f.DynamicApi.Get<IRealmObjectBase>("File")?.DynamicApi.Get<string>("Hash") ?? "";
                             break;
                         }
                    }
                } catch {}

                var b = score.DynamicApi.Get<IRealmObjectBase>("BeatmapInfo");
                if (b != null) {
                    s.BeatmapLength = b.DynamicApi.Get<double>("Length");
                    s.StarRating = b.DynamicApi.Get<double>("StarRating");
                    s.BeatmapVersion = b.DynamicApi.Get<string>("DifficultyName") ?? "";
                    var m = b.DynamicApi.Get<IRealmObjectBase>("Metadata");
                    if (m != null) {
                        s.BeatmapTitle = m.DynamicApi.Get<string>("Title") ?? "Unknown";
                        s.BeatmapArtist = m.DynamicApi.Get<string>("Artist") ?? "Unknown";
                    }
                }
                result.Add(s);
            } catch { continue; }
        }
        return result;
    }

    private ulong GetSchemaVersion(string path) {
        try { using var _ = Realm.GetInstance(new RealmConfiguration(path) { IsReadOnly = true, SchemaVersion = 0, IsDynamic = true }); return 0; }
        catch (Exception ex) {
            var m = ex.Message; var i = m.IndexOf("from schema version ");
            if (i >= 0) { var v = m.Substring(i + 20).Split(' ').First(); if (ulong.TryParse(v, out var uv)) return uv; }
            i = m.IndexOf("last set version ");
            if (i >= 0) { var v = m.Substring(i + 17).TrimEnd('.').Split(' ').First().TrimEnd('.'); if (ulong.TryParse(v, out var uv)) return uv; }
            throw;
        }
    }

    private string? CopyRealmToTemp(string originalPath) {
        try {
            var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, $"client_import_{DateTime.Now:yyyyMMdd_HHmmss}.realm");
            using (var source = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) source.CopyTo(dest);
            return tempPath;
        } catch { return null; }
    }

    private void CleanupTempRealmFiles() {
        try {
            var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
            if (!Directory.Exists(tempDir)) return;
            foreach (var file in Directory.GetFiles(tempDir, "client_import_*.realm")) try { File.Delete(file); } catch { }
        } catch { }
    }
}
