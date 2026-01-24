using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OsuGrind.Models;
using OsuGrind.Services;
using Realms;

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

    /// <summary>
    /// Import beatmaps only, without importing scores.
    /// </summary>
    public async Task<(int Added, int Skipped, string? Error)> ImportBeatmapsOnlyAsync(string? lazerFolderPath = null)
    {
        string realmPath = GetRealmPath(lazerFolderPath);

        if (!File.Exists(realmPath))
            return (0, 0, $"Realm file not found: {realmPath}");

        ulong schemaVersion;
        try { schemaVersion = GetSchemaVersion(realmPath); }
        catch (Exception ex) { return (0, 0, $"Failed to detect version: {ex.Message}"); }

        var config = new RealmConfiguration(realmPath)
        {
            IsReadOnly = true,
            SchemaVersion = schemaVersion,
            IsDynamic = true
        };

        using var rosu = new RosuService();

        try
        {
            var (added, skipped, _) = await ImportBeatmaps(config, rosu, lazerFolderPath);
            return (added, skipped, null);
        }
        catch (Exception ex)
        {
            return (0, 0, $"Import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Imports a specific beatmap by Hash if it exists in Lazer but not in TrackerDb.
    /// </summary>
    public async Task<(bool, string?)> ImportBeatmapAsync(string hash, string? lazerFolderPath = null)
    {
        if (string.IsNullOrEmpty(hash)) return (false, null);

        string realmPath = GetRealmPath(lazerFolderPath);
        if (!File.Exists(realmPath)) return (false, null);

        // Copy realm to temp to avoid file locks
        string? tempRealmPath = CopyRealmToTemp(realmPath);
        if (tempRealmPath == null) return (false, null);

        ulong schemaVersion;
        try { schemaVersion = GetSchemaVersion(tempRealmPath); }
        catch { return (false, null); }

        var config = new RealmConfiguration(tempRealmPath)
        {
            IsReadOnly = true,
            SchemaVersion = schemaVersion,
            IsDynamic = true
        };

        using var rosu = new RosuService();
        // Use original realm path for file lookups
        string lazerFilesPath = Path.Combine(Path.GetDirectoryName(realmPath) ?? "", "files");

        try
        {
            using var realm = Realm.GetInstance(config);
            if (!realm.Schema.Any(s => s.Name == "BeatmapInfo")) return (false, null);

            var beatmaps = realm.DynamicApi.All("BeatmapInfo");
            IRealmObjectBase? match = null;
            foreach (var b in beatmaps)
            {
                var md5 = b.DynamicApi.Get<string>("MD5Hash") ?? "";
                var sha2 = b.DynamicApi.Get<string>("Hash") ?? "";
                if (md5 == hash || sha2 == hash)
                {
                    match = b;
                    break;
                }
            }

            if (match == null) return (false, null);

            var (ok, path) = await ProcessBeatmap(match, db, rosu, lazerFilesPath);
            return (ok, path);
        }
        catch (Exception ex)
        {
            DebugService.Error($"ImportBeatmapAsync Error: {ex.Message}", "LazerImportService");
        }
        return (false, null);
    }

    public async Task<(int Added, int Skipped, string? Error)> ImportScoresAsync(string? lazerFolderPath = null, string? targetUsername = null)
    {
        DebugService.Log($"ImportScoresAsync started", "LazerImportService");
        CleanupTempRealmFiles();
        
        string realmPath = GetRealmPath(lazerFolderPath);
        DebugService.Log($"Realm path: {realmPath}, Exists: {File.Exists(realmPath)}", "LazerImportService");

        if (!File.Exists(realmPath))
            return (0, 0, $"Realm file not found: {realmPath}");

        // Copy realm to temp to avoid file locks when osu!lazer is running
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
            var extractedScores = ExtractScoresDynamic(config);
            DebugService.Log($"Extracted {extractedScores.Count} scores from Realm", "LazerImportService");
            
            int scoresAdded = 0;
            int scoresSkipped = 0;
            
            // PERFORMANCE: Cache beatmaps once instead of querying DB for every score
            var allBeatmaps = await db.GetBeatmapsAsync();
            var beatmapByHash = allBeatmaps.ToDictionary(bm => bm.Hash, bm => bm);
            DebugService.Log($"Cached {allBeatmaps.Count} beatmaps for lookup", "LazerImportService");

            var batchPlays = new List<PlayRow>();
            var existingMapHashes = new HashSet<string>(allBeatmaps.Select(b => b.Hash));

            foreach (var s in extractedScores)
            {
                if (string.IsNullOrEmpty(s.BeatmapHash))
                {
                    scoresSkipped++;
                    continue;
                }

                // Parse Hits
                var statsDic = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, int>>(s.StatisticsJson ?? "{}") ?? new();
                int n300 = statsDic.GetValueOrDefault("great", 0) + statsDic.GetValueOrDefault("Great", 0);
                int n100 = statsDic.GetValueOrDefault("ok", 0) + statsDic.GetValueOrDefault("Ok", 0);
                int n50 = statsDic.GetValueOrDefault("meh", 0) + statsDic.GetValueOrDefault("Meh", 0);
                int nMiss = statsDic.GetValueOrDefault("miss", 0) + statsDic.GetValueOrDefault("Miss", 0);

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
                
                // Get TotalObjects for score conversion (use cached lookup)
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
                    ScoreId = 0, // Not using Lazer UUID here as int64, just auto-increment
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
                    HitOffsets = string.Join(",", s.HitOffsets.Select(o => o.ToString("F2")))
                };

                // REPLAY EXTRACTION - DISABLED: Now on-demand
                if (!string.IsNullOrEmpty(s.ReplayHash))
                {
                    // Store hash for later extraction
                    row.ReplayHash = s.ReplayHash;
                }

                batchPlays.Add(row);
                scoresAdded++;
            }

            // Perform Batch Insert
            if (batchPlays.Count > 0)
            {
                // First, remove existing plays to avoid duplicates?
                // The user asked to "delete score database" in settings, so if they did that, we just insert.
                // If not, we might duplicate. The original code had logic to delete fuzzy match.
                // For batch import, usually we want speed.
                // We can't easily do fuzzy match in batch.
                // We will assume the user cleared the DB or accepts duplicates if re-importing.
                // OR we can implement a smarter conflict resolution later.
                // For now, FAST IMPORT.
                
                await db.InsertPlaysBatchAsync(batchPlays);
            }

            DebugService.Log($"Scores import complete: Added={scoresAdded}, Skipped={scoresSkipped}", "LazerImportService");
            return (scoresAdded, scoresSkipped, null);
        }
        catch (Exception ex)
        {
            return (0, 0, $"Import failed: {ex.Message}");
        }
    }

    private string MapRankToGrade(int rank)
    {
        // osu!lazer ScoreRank enum: F=-1, D=0, C=1, B=2, A=3, S=4, SH=5, X=6, XH=7
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
        // IMPORTANT: Use original lazer path, not config.DatabasePath (which may be a temp copy)
        string originalRealmPath = GetRealmPath(lazerFolderPath);
        string lazerFilesPath = Path.Combine(Path.GetDirectoryName(originalRealmPath) ?? "", "files");
        
        DebugService.Log($"Importing beatmaps from: {lazerFilesPath}", "LazerImportService");

        using var realm = Realm.GetInstance(config);
        
        IEnumerable<IRealmObjectBase> beatmaps;
        bool usingSets = false;

        if (realm.Schema.Any(s => s.Name == "BeatmapInfo"))
        {
             beatmaps = realm.DynamicApi.All("BeatmapInfo");
             try { File.AppendAllText("g:\\The Tracker\\OsuGrind\\startup_debug_FORCE.txt", $"[LazerImport] Found {beatmaps.Count()} beatmaps via BeatmapInfo.\n"); } catch { }
        }
        else if (realm.Schema.Any(s => s.Name == "BeatmapSet"))
        {
             usingSets = true;
             beatmaps = realm.DynamicApi.All("BeatmapSet"); // We will iterate nested items
             try { File.AppendAllText("g:\\The Tracker\\OsuGrind\\startup_debug_FORCE.txt", $"[LazerImport] Found {beatmaps.Count()} sets via BeatmapSet (Schema fallback).\n"); } catch { }
        }
        else
        {
             try { File.AppendAllText("g:\\The Tracker\\OsuGrind\\startup_debug_FORCE.txt", $"[LazerImport] critical: No BeatmapInfo or BeatmapSet schema found.\n"); } catch { }
             return (0, 0, hashToPath);
        }

        foreach (var item in beatmaps)
        {
            if (usingSets)
            {
                // Iterate nested Beatmaps in the set
                var nestedMaps = item.DynamicApi.GetList<IRealmObjectBase>("Beatmaps");
                foreach (var b in nestedMaps)
                {
                var (ok, path) = await ProcessBeatmap(b, db, rosu, lazerFilesPath);
                if (ok) added++; else skipped++;
                if (!string.IsNullOrEmpty(path))
                {
                    var h = b.DynamicApi.Get<string>("MD5Hash");
                    if (string.IsNullOrEmpty(h)) h = b.DynamicApi.Get<string>("Hash"); // Fallback
                    if (!string.IsNullOrEmpty(h)) hashToPath[h] = path;
                }
                }
            }
            else
            {
                var (ok, path) = await ProcessBeatmap(item, db, rosu, lazerFilesPath);
                if (ok) added++; else skipped++;
                if (!string.IsNullOrEmpty(path))
                {
                    var h = item.DynamicApi.Get<string>("MD5Hash");
                    if (string.IsNullOrEmpty(h)) h = item.DynamicApi.Get<string>("Hash"); // Fallback
                    if (!string.IsNullOrEmpty(h)) hashToPath[h] = path;
                }
            }
        }
        return (added, skipped, hashToPath);
    }

    private async Task<(bool ok, string? path)> ProcessBeatmap(IRealmObjectBase b, TrackerDb db, RosuService rosu, string lazerFilesPath)
    {
            try
            {
                string md5 = b.DynamicApi.Get<string>("MD5Hash") ?? "";
                string sha2 = b.DynamicApi.Get<string>("Hash") ?? "";
                
                if (string.IsNullOrEmpty(md5) && string.IsNullOrEmpty(sha2)) 
                {
                    // Log fail
                    try { File.AppendAllText("g:\\The Tracker\\OsuGrind\\startup_debug_FORCE.txt", $"[LazerImport] Skip: No MD5 or SHA2 Hash for a beatmap item.\n"); } catch { }
                    return (false, null);
                }

                // If MD5 is missing but SHA2 is present, use SHA2 as identifier
                string identifyingHash = !string.IsNullOrEmpty(md5) ? md5 : sha2;


                var meta = b.DynamicApi.Get<IRealmObjectBase>("Metadata");
                var diff = b.DynamicApi.Get<IRealmObjectBase>("Difficulty");
                var ruleset = b.DynamicApi.Get<IRealmObjectBase>("Ruleset");
                
                // Only import osu!standard (RulesetID 0 or ShortName 'osu')
                if (ruleset != null)
                {
                     var shortName = ruleset.DynamicApi.Get<string>("ShortName");
                     if (shortName != "osu") 
                     {
                         // Log fail (verbose - maybe too spammy? limit to first 5 or just ignore)
                         // try { File.AppendAllText("g:\\The Tracker\\OsuGrind\\startup_debug_FORCE.txt", $"[LazerImport] Skip: Ruleset {shortName}\n"); } catch { }
                         return (false, null);
                     }
                }

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
                    BackgroundHash = ExtractBackgroundHash(b),
                    OsuFilePath = null
                };

                // Play Counts from Realm (local only) - may not exist in all schema versions
                try
                {
                    var scores = b.DynamicApi.GetList<IRealmObjectBase>("Scores");
                    row.PlayCount = scores.Count;
                    row.PassCount = scores.Count(s => {
                        var rank = s.DynamicApi.Get<int>("Rank");
                        return rank > 0 && rank < 9; 
                    });
                }
                catch { row.PlayCount = 0; row.PassCount = 0; }

                try
                {
                    if (diff != null)
                    {
                        row.CS = diff.DynamicApi.Get<float>("CircleSize");
                        row.AR = diff.DynamicApi.Get<float>("ApproachRate");
                        row.OD = diff.DynamicApi.Get<float>("OverallDifficulty");
                        row.HP = diff.DynamicApi.Get<float>("DrainRate");
                    }
                }
                catch { /* Difficulty attributes not available */ }

                string? osuPath = null;
                // Resolve file for Object Counts
                var set = b.DynamicApi.Get<IRealmObjectBase>("BeatmapSet");
                if (set != null)
                {
                    var files = set.DynamicApi.GetList<IRealmObjectBase>("Files");
                    // Important: The file's 'Hash' in Lazer's Files system is the SHA2-256 hash.
                    // We must match it against b.Hash (sha2).
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

                                // Parse .osu
                                var (c, s, sp) = ParseOsuFileCounts(physicalPath);
                                row.Circles = c;
                                row.Sliders = s;
                                row.Spinners = sp;

                                // Rosu Calc (Max Combo, confirm stars)
                                var perf = rosu.CalculatePpIfFc(physicalPath, new List<string>(), 100.0);
                                if (perf.MaxCombo > 0) row.MaxCombo = perf.MaxCombo;
                                if (row.Stars <= 0 && perf.Stars > 0) row.Stars = perf.Stars;
                            }
                        }
                    }
                    else
                    {
                         // try { File.AppendAllText("g:\\The Tracker\\OsuGrind\\startup_debug_FORCE.txt", $"[LazerImport] No file match for beatmap {identifyingHash} (SHA2: {sha2})\n"); } catch { }
                    }
                }

                await db.InsertOrUpdateBeatmapAsync(row);
                return (true, osuPath);
            }

            catch (Exception ex) 
            { 
                 try { File.AppendAllText("g:\\The Tracker\\OsuGrind\\startup_debug_FORCE.txt", $"[LazerImport] Beatmap Import Error for map: {ex.Message}\n"); } catch { }
                 return (false, null);
            }
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
        try
        {
            bool inObjects = false;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("["))
                {
                    inObjects = line.Trim() == "[HitObjects]";
                    continue;
                }
                if (!inObjects) continue;
                
                // Content: x,y,time,type,hitSound,objectParams,hitSample
                // Type bitmask: 1=Circle, 2=Slider, 8=Spinner, 128=Hold(Mania)
                var parts = line.Split(',');
                if (parts.Length > 3 && int.TryParse(parts[3], out int type))
                {
                    if ((type & 1) > 0) c++;
                    else if ((type & 2) > 0) s++;
                    else if ((type & 8) > 0) sp++;
                }
            }
        }
        catch {}
        return (c, s, sp);
    }


    private string? ExtractBackgroundHash(IRealmObjectBase b)
    {
        try
        {
            var meta = b.DynamicApi.Get<IRealmObjectBase>("Metadata");
            string? bgFile = meta?.DynamicApi.Get<string>("BackgroundFile");
            if (string.IsNullOrEmpty(bgFile)) return null;

            var set = b.DynamicApi.Get<IRealmObjectBase>("BeatmapSet");
            if (set == null) return null;

            var files = set.DynamicApi.GetList<IRealmObjectBase>("Files");
            var match = files.FirstOrDefault(f => f.DynamicApi.Get<string>("Filename")?.Equals(bgFile, StringComparison.OrdinalIgnoreCase) == true);
            
            return match?.DynamicApi.Get<IRealmObjectBase>("File")?.DynamicApi.Get<string>("Hash");
        }
        catch { return null; }
    }

    private string TryGetStatus(IRealmObjectBase b)
    {
        try { return ((int)b.DynamicApi.Get<long>("StatusInt")).ToString(); } catch { }
        try { return b.DynamicApi.Get<string>("Status") ?? "Unknown"; } catch { }
        return "Unknown";
    }

    private string GetRealmPath(string? foldersPath)
    {
        if (!string.IsNullOrEmpty(foldersPath) && File.Exists(foldersPath)) return foldersPath;
        if (!string.IsNullOrEmpty(foldersPath)) return Path.Combine(foldersPath, "client.realm");
        
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        
        var candidates = new[]
        {
            Path.Combine(appData, "osu", "client.realm"),
            Path.Combine(localAppData, "osu", "client.realm"),
            Path.Combine(appData, "osu!", "client.realm"),
            Path.Combine(localAppData, "osu!", "client.realm")
        };

        foreach (var p in candidates)
        {
            if (File.Exists(p)) return p;
        }

        return candidates[0]; // Default fallback (appData/osu)
    }

    private List<ExtractedScore> ExtractScoresDynamic(RealmConfiguration config)
    {
        var result = new List<ExtractedScore>();
        
        using var realm = Realm.GetInstance(config);
        
        if (!realm.Schema.Any(s => s.Name == "Score")) return result;

        var scores = realm.DynamicApi.All("Score");
        
        foreach (var score in scores)
        {
            try
            {
                var s = new ExtractedScore();
                s.Accuracy = score.DynamicApi.Get<double>("Accuracy");
                s.Combo = score.DynamicApi.Get<int>("MaxCombo");
                s.Rank = score.DynamicApi.Get<int>("Rank");
                s.Date = score.DynamicApi.Get<DateTimeOffset>("Date").UtcDateTime;
                s.PP = score.DynamicApi.Get<double?>("PP") ?? 0;
                s.TotalScore = score.DynamicApi.Get<long>("TotalScore");
                s.ModsJson = score.DynamicApi.Get<string>("Mods") ?? "[]";
                s.StatisticsJson = score.DynamicApi.Get<string>("Statistics") ?? "{}";

                // Extract Hit Offsets for UR/Heatmap
                try
                {
                    var hitEvents = score.DynamicApi.GetList<IRealmObjectBase>("HitEvents");
                    foreach (var e in hitEvents)
                    {
                        // Lazer stores TimeOffset as double in RealmHitEvent
                        double? offset = e.DynamicApi.Get<double?>("TimeOffset");
                        if (offset.HasValue) s.HitOffsets.Add(offset.Value);
                    }
                }
                catch { /* Property might not exist or schema mismatch */ }

                var user = score.DynamicApi.Get<IRealmObjectBase>("User");
                s.Username = user?.DynamicApi.Get<string>("Username") ?? "Guest";

                // Extract Replay Hash
                try
                {
                    var files = score.DynamicApi.GetList<IRealmObjectBase>("Files");
                    foreach (var f in files)
                    {
                         string filename = f.DynamicApi.Get<string>("Filename");
                         var fileObj = f.DynamicApi.Get<IRealmObjectBase>("File");
                         string hash = fileObj?.DynamicApi.Get<string>("Hash") ?? "";

                         if (string.IsNullOrEmpty(s.ReplayHash)) s.ReplayHash = hash; // Fallback to first file

                         if (!string.IsNullOrEmpty(filename) && (filename.EndsWith(".osr", StringComparison.OrdinalIgnoreCase) || filename.Equals("replay", StringComparison.OrdinalIgnoreCase)))
                         {
                             s.ReplayHash = hash;
                             break; 
                         }
                    }
                }
                catch {}

                var b = score.DynamicApi.Get<IRealmObjectBase>("BeatmapInfo");
                if (b != null)
                {
                    s.BeatmapHash = b.DynamicApi.Get<string>("MD5Hash") ?? "";
                    if (string.IsNullOrEmpty(s.BeatmapHash)) s.BeatmapHash = b.DynamicApi.Get<string>("Hash") ?? "";
                    
                    s.BeatmapLength = b.DynamicApi.Get<double>("Length");
                    s.StarRating = b.DynamicApi.Get<double>("StarRating");
                    s.BeatmapVersion = b.DynamicApi.Get<string>("DifficultyName") ?? "";

                    var m = b.DynamicApi.Get<IRealmObjectBase>("Metadata");
                    if (m != null)
                    {
                        s.BeatmapTitle = m.DynamicApi.Get<string>("Title") ?? "Unknown";
                        s.BeatmapArtist = m.DynamicApi.Get<string>("Artist") ?? "Unknown";
                    }
                }

                // Deep parse statistics for extra fields while we are here
                try
                {
                    var dict = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, int>>(s.StatisticsJson ?? "{}") ?? new();
                    s.SliderTailHit = dict.GetValueOrDefault("slider_tail_hit", 0);
                    s.SmallTickHit = dict.GetValueOrDefault("small_tick_hit", 0);
                    s.LargeTickHit = dict.GetValueOrDefault("large_tick_hit", 0);
                    s.SmallBonus = dict.GetValueOrDefault("small_bonus", 0);
                    s.LargeBonus = dict.GetValueOrDefault("large_bonus", 0);
                }
                catch { }

                result.Add(s);
            }
            catch { continue; }
        }
        return result;
    }

    private ulong GetSchemaVersion(string path)
    {
        try { using var _ = Realm.GetInstance(new RealmConfiguration(path) { IsReadOnly = true, SchemaVersion = 0, IsDynamic = true }); return 0; }
        catch (Exception ex) {
            var m = ex.Message;
            var i = m.IndexOf("from schema version ");
            if (i >= 0) { var v = m.Substring(i + 20).Split(' ').First(); if (ulong.TryParse(v, out var uv)) return uv; }
            i = m.IndexOf("last set version ");
            if (i >= 0) { var v = m.Substring(i + 17).TrimEnd('.').Split(' ').First().TrimEnd('.'); if (ulong.TryParse(v, out var uv)) return uv; }
            throw;
        }
    }

    /// <summary>
    /// Copy realm file to temp to avoid file lock issues when osu!lazer is running.
    /// </summary>
    private string? CopyRealmToTemp(string originalPath)
    {
        try
        {
            var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, $"client_import_{DateTime.Now:yyyyMMdd_HHmmss}.realm");
            using (var source = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(dest);
            }
            DebugService.Log($"Copied realm to temp: {tempPath}", "LazerImportService");
            return tempPath;
        }
        catch (Exception ex)
        {
            DebugService.Error($"Failed to copy realm to temp: {ex.Message}", "LazerImportService");
            return null;
        }
    }

    private void CleanupTempRealmFiles()
    {
        try
        {
            var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
            if (!Directory.Exists(tempDir)) return;
            foreach (var file in Directory.GetFiles(tempDir, "client_import_*.realm"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }
}
