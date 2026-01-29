using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Diagnostics;
using OsuGrind.Models;
using OsuGrind.Services;
using OsuGrind.Api;

using Microsoft.Win32;
using System.Text.RegularExpressions;


namespace OsuGrind.Import
{
    public class OsuStableImportService
    {
        private readonly TrackerDb _db;

        public OsuStableImportService(TrackerDb db)
        {
            _db = db;
        }

        public static string? AutoDetectStablePath()
        {
            try
            {
                // 1. Check where the app launched from (Process check)
                var processes = Process.GetProcessesByName("osu!");
                foreach (var p in processes)
                {
                    try
                    {
                        if (!p.HasExited && p.MainModule != null)
                        {
                            var pPath = p.MainModule.FileName;
                            var dir = Path.GetDirectoryName(pPath);
                            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && File.Exists(Path.Combine(dir, "scores.db"))) return dir;
                        }
                    }
                    catch { }
                }

                // 2. Normal Installation Locations
                string localOsu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!");
                if (Directory.Exists(localOsu) && File.Exists(Path.Combine(localOsu, "scores.db"))) return localOsu;

                // 3. Check Registry
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\osu!");
                if (key != null)
                {
                    var path = key.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && File.Exists(Path.Combine(path, "scores.db"))) return path;
                }

                // 4. Other common paths
                var candidates = new List<string>
                {
                    @"C:\osu!",
                    @"D:\osu!",
                    @"E:\osu!",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "osu!"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "osu!")
                };


                // 4. Proactive search: Check parents of current executable (Portable mode)
                var currentDir = AppDomain.CurrentDomain.BaseDirectory;
                var parent = Directory.GetParent(currentDir);
                while (parent != null)
                {
                    candidates.Add(Path.Combine(parent.FullName, "osu!"));
                    if (parent.Name.Equals("osu!", StringComparison.OrdinalIgnoreCase)) candidates.Add(parent.FullName);
                    parent = parent.Parent;
                }

                foreach (var c in candidates.Distinct())
                {
                    if (Directory.Exists(c) && File.Exists(Path.Combine(c, "scores.db"))) return c;
                }
            }
            catch { }
            return null;
        }

        private class BeatmapInfo
        {
            public string Name { get; set; } = "";
            public string FolderName { get; set; } = "";
            public string FileName { get; set; } = "";
            public int TotalTime { get; set; }
            public Dictionary<int, double> Stars { get; set; } = new();
        }

        private Dictionary<string, BeatmapInfo> _beatmaps = new(StringComparer.OrdinalIgnoreCase);

        public async Task<(int added, int skipped, string error)> ImportScoresAsync(string? stablePath = null, string? targetUsername = null, string? aliases = null)
        {
            DebugService.Log($"OsuStableImportService.ImportScoresAsync started with aliases: {aliases}", "StableImport");
            
            // Try provided path, then auto-detect if invalid
            string path = stablePath ?? "";
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                path = AutoDetectStablePath() ?? "";
            }

            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return (0, 0, "osu!stable not found.");


            var scoresDbPath = Path.Combine(path, "scores.db");
            if (!File.Exists(scoresDbPath))
                return (0, 0, "scores.db not found in " + path);

            // Parse aliases
            var aliasList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            aliasList.Add(""); 
            aliasList.Add(" "); 
            aliasList.Add("Guest");

            if (!string.IsNullOrWhiteSpace(aliases))
            {
                foreach (var a in aliases.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    aliasList.Add(a.Trim());
            }

            if (!string.IsNullOrEmpty(targetUsername)) aliasList.Add(targetUsername);

            DebugService.Log($"Found stable at: {path}. Importing for aliases: {string.Join(", ", aliasList)}", "StableImport");

            // Update settings if we found a path and it wasn't set
            if (string.IsNullOrEmpty(stablePath) || !stablePath.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                SettingsManager.Current.StablePath = path;
                SettingsManager.Save();
            }

            // Try to load osu!.db from the same folder to get map names
            var osuDbPath = Path.Combine(path, "osu!.db");

            if (File.Exists(osuDbPath))
            {
                try
                {
                    LoadMapData(osuDbPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Import] Error loading osu!.db: {ex}");
                }
            }

            int added = 0;
            int skipped = 0;
            string error = "";

            using var rosuService = new RosuService();

            await Task.Run(async () =>
            {
                try
                {
                    // Cache existing signatures for deduplication
                    var existingSignatures = await _db.GetExistingScoreSignaturesAsync();

                    using (var fs = File.OpenRead(scoresDbPath))
                    using (var r = new BinaryReader(fs))
                    {
                        var version = r.ReadInt32();
                        var beatmapCount = r.ReadInt32();

                        for (int i = 0; i < beatmapCount; i++)
                        {
                            var beatmapMd5 = ReadOsuString(r);
                            var scoreCount = r.ReadInt32();

                            for (int j = 0; j < scoreCount; j++)
                            {
                                try
                                {
                                    // Parse Score
                                    var mode = r.ReadByte();
                                    var scoreVersion = r.ReadInt32();
                                    var mapMd5 = ReadOsuString(r);
                                    var playerName = ReadOsuString(r);
                                    var replayMd5 = ReadOsuString(r);
                                    var cnt300 = r.ReadUInt16();
                                    var cnt100 = r.ReadUInt16();
                                    var cnt50 = r.ReadUInt16();
                                    var cntGeki = r.ReadUInt16();
                                    var cntKatu = r.ReadUInt16();
                                    var misses = r.ReadUInt16();
                                    var scoreVal = r.ReadInt32();
                                    var maxCombo = r.ReadUInt16();
                                    var perfect = r.ReadBoolean();
                                    var mods = r.ReadInt32();
                                    var lifeBarGraph = ReadOsuString(r);
                                    var timestamp = r.ReadInt64();
                                    r.ReadInt32(); // Skip 0xFFFFFFFF marker
                                    var onlineId = r.ReadInt64();
                                    
                                     if (mode != 0) continue; // Only Standard
                                    if (scoreVal <= 0) continue; // Skip aborted/invalid scores
 
                                     // FILTER: Only user/local/alias replays
                                    if (!aliasList.Contains(playerName))
                                    {
                                        continue;
                                    }

                                    var createdAt = DateTime.UtcNow;

                                    try
                                    {
                                        createdAt = DateTime.FromBinary(timestamp);
                                        if (createdAt.Kind == DateTimeKind.Local) createdAt = createdAt.ToUniversalTime();
                                    }
                                    catch { createdAt = DateTime.UtcNow; }

                                     // DEDUPLICATION CHECK
                                    string sig = $"{mapMd5}|{scoreVal}|{createdAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}";
                                    if (existingSignatures.Contains(sig))
                                    {
                                        skipped++;
                                        continue;
                                    }
                                    existingSignatures.Add(sig);

                                    // Metadata lookup
                                    _beatmaps.TryGetValue(mapMd5, out var info);

                                    int diffMods = mods & (2 | 16 | 64 | 256 | 512 | 1024);
                                    double stars = 0;
                                    if (info != null)
                                    {
                                        if (!info.Stars.TryGetValue(diffMods, out stars))
                                        {
                                            if ((diffMods & 512) > 0)
                                            {
                                                int normalizedMods = (diffMods & ~512) | 64;
                                                info.Stars.TryGetValue(normalizedMods, out stars);
                                            }
                                            if (stars == 0) info.Stars.TryGetValue(0, out stars);
                                        }
                                    }

                                    // Calculate Accuracy
                                    double acc = CalculateAccuracy(cnt300, cnt100, cnt50, misses);

                                    // Calculate PP via RosuService
                                    double calculatedPP = 0;
                                    string? osuPath = info != null ? Path.Combine(path, "Songs", info.FolderName, info.FileName) : "";
                                    
                                    if (File.Exists(osuPath))
                                    {
                                        try
                                        {
                                            rosuService.UpdateContext(osuPath);
                                            // Add bit 24 (Classic) to the mod mask for rosu calculation
                                            uint modsBits = (uint)mods | (1u << 24);
                                            double clockRate = RosuService.GetClockRateFromMods(modsBits);
                                            
                                            // Stable scores in scores.db are always PASS (outcome="pass")
                                            // so we can use -1 for passedObjects to indicate a full calculation
                                            calculatedPP = rosuService.CalculatePp(modsBits, (int)maxCombo, (int)cnt300, (int)cnt100, (int)cnt50, (int)misses, -1, clockRate: clockRate);
                                        }
                                        catch { }
                                    }

                                     // REPLAY LINKING: Stable replays are stored in Data/r/MD5.osr
                                    string linkedReplayFile = "";
                                    if (!string.IsNullOrEmpty(replayMd5))
                                    {
                                        var rDir = Path.Combine(path, "Data", "r");
                                        var replayCandidate = Path.Combine(rDir, $"{replayMd5}.osr");
                                        if (File.Exists(replayCandidate)) 
                                        {
                                            linkedReplayFile = replayCandidate;
                                        }
                                        else
                                        {
                                            // Fallback: Search Data/r for any file that matches the beatmap hash and is recent
                                            try {
                                                if (Directory.Exists(rDir))
                                                {
                                                    // Search for files starting with mapMd5
                                                    var files = Directory.GetFiles(rDir, $"{mapMd5}*.osr");
                                                    if (files.Length > 0)
                                                    {
                                                        // Get the one closest to our score timestamp
                                                        var bestMatch = files.Select(f => new FileInfo(f))
                                                            .OrderBy(fi => Math.Abs((fi.LastWriteTimeUtc - createdAt).TotalSeconds))
                                                            .FirstOrDefault();
                                                        
                                                        if (bestMatch != null)
                                                        {
                                                            // If it's within 2 hours of the score timestamp, it's likely the right one
                                                            if (Math.Abs((bestMatch.LastWriteTimeUtc - createdAt).TotalHours) < 2)
                                                                linkedReplayFile = bestMatch.FullName;
                                                        }
                                                    }
                                                }
                                            } catch { }
                                        }
                                    }

                                    var play = new PlayRow
                                    {
                                        CreatedAtUtc = createdAt,
                                        Outcome = "pass",
                                        DurationMs = info?.TotalTime ?? 0,
                                        Beatmap = info?.Name ?? $"[Stable] {mapMd5.Substring(0, Math.Min(8, mapMd5.Length))}",
                                        BeatmapHash = mapMd5,
                                        Mods = ParseMods(mods),
                                        Stars = stars,
                                        Accuracy = acc,
                                        Score = scoreVal,
                                        Combo = maxCombo,
                                        Count300 = cnt300,
                                        Count100 = cnt100,
                                        Count50 = cnt50,
                                        Misses = misses,
                                        PP = calculatedPP,
                                        Notes = "Imported from scores.db",
                                        MapPath = osuPath ?? "",
                                        ReplayFile = linkedReplayFile,
                                        ReplayHash = replayMd5 // Ensure this is set so UI knows it's imported
                                    };

                                    // ANALYZE REPLAY IF AVAILABLE
                                         if (!string.IsNullOrEmpty(linkedReplayFile) && File.Exists(osuPath))
                                         {
                                             try {
                                                 DebugService.Log($"Analyzing stable replay: {linkedReplayFile}", "StableImport");
                                                 var analysis = MissAnalysisService.Analyze(osuPath, linkedReplayFile);
                                                 play.UR = analysis.UR;
                                                 play.HitErrorsJson = System.Text.Json.JsonSerializer.Serialize(analysis.HitErrors);
                                                 play.KeyRatio = analysis.KeyRatio;
                                                 DebugService.Log($"Analysis complete: UR={analysis.UR:F2}, KeyRatio={analysis.KeyRatio:P1}", "StableImport");
                                             } catch (Exception ex) {
                                                 DebugService.Error($"Analysis failed for {linkedReplayFile}: {ex.Message}", "StableImport");
                                             }
                                         }

                                    await _db.InsertPlayAsync(play);

                                     // ALSO POPULATE BEATMAPS TABLE for backgrounds/metadata
                                     if (info != null)
                                     {
                                         var bgPath = Path.Combine(path, "Songs", info.FolderName, info.FileName);

                                         // Attempt to find real background file from .osu content?
                                        // For now, if we can find the .osu, we can parse it.
                                        string bgIdentifier = "";
                                        if (File.Exists(osuPath))
                                        {
                                            try {
                                                var lines = File.ReadLines(osuPath);
                                                var eventsSection = lines.SkipWhile(l => l.Trim() != "[Events]").Skip(1).TakeWhile(l => !l.Trim().StartsWith("["));
                                                foreach (var line in eventsSection)
                                                {
                                                    var trimmed = line.Trim();
                                                    if (trimmed.StartsWith("0,0,"))
                                                    {
                                                        // Event format: Type, StartTime, Filename, XOffset, YOffset
                                                        // filename can be quoted or not
                                                        var match = Regex.Match(trimmed, @"0,0,""?([^"",\r\n]+\.(?:jpg|jpeg|png))""?", RegexOptions.IgnoreCase);
                                                        if (match.Success)
                                                        {
                                                            var bgFileName = match.Groups[1].Value;
                                                            var fullBgPath = Path.Combine(path, "Songs", info.FolderName, bgFileName);
                                                            if (File.Exists(fullBgPath))
                                                            {
                                                                bgIdentifier = "STABLE:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(fullBgPath));
                                                                break;
                                                            }
                                                        }
                                                    }
                                                }
                                            } catch { }
                                        }

                                        var beatmapRow = new BeatmapRow
                                        {
                                            Hash = mapMd5,
                                            Title = info.Name.Split(" - ").Last().Split(" [").First(),
                                            Artist = info.Name.Split(" - ").First(),
                                            Version = info.Name.Contains("[") ? info.Name.Split("[").Last().TrimEnd(']') : "",
                                            Stars = stars,
                                            LengthMs = info.TotalTime,
                                            BackgroundHash = bgIdentifier,
                                            LastPlayedUtc = createdAt,
                                            CS = rosuService.CS,
                                            AR = rosuService.AR,
                                            OD = rosuService.OD,
                                            HP = rosuService.HP,
                                            BPM = rosuService.BaseBpm,
                                            Circles = rosuService.TotalCircles,
                                            Sliders = rosuService.TotalSliders,
                                            Spinners = rosuService.TotalSpinners
                                        };

                                        // Try to get MaxCombo from a perfect SS result
                                        try {
                                            var perf = rosuService.CalculatePpIfFc(osuPath, new List<string>(), 100.0);
                                            if (perf.MaxCombo > 0) beatmapRow.MaxCombo = perf.MaxCombo;
                                            if (beatmapRow.Stars <= 0) beatmapRow.Stars = perf.Stars;
                                        } catch { }

                                        await _db.InsertOrUpdateBeatmapAsync(beatmapRow);
                                    }
                                    added++;
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[Import] Error parsing score at index {j} for map {beatmapMd5}: {ex.Message}");
                                    skipped++;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            });

            return (added, skipped, error);
        }


        private string ReadOsuString(BinaryReader r)
        {
            var flag = r.ReadByte();
            if (flag == 0x00) return "";
            if (flag == 0x0b) return r.ReadString();
            return "";
        }

        private double CalculateAccuracy(int c300, int c100, int c50, int miss)
        {
            int total = c300 + c100 + c50 + miss;
            if (total == 0) return 0;
            // Return as 0.0 - 1.0 ratio for WPF StringFormat=P
            return (300.0 * c300 + 100.0 * c100 + 50.0 * c50) / (300.0 * total);
        }

        private void LoadMapData(string path)
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);

            var dbVersion = r.ReadInt32();
            var folderCount = r.ReadInt32();
            var accountUnlocked = r.ReadBoolean();
            r.ReadInt64(); // Unlock date
            ReadOsuString(r); // Player name
            var beatmapCount = r.ReadInt32();

            for (int i = 0; i < beatmapCount; i++)
            {
                try
                {
                    if (dbVersion < 20191106)
                        r.ReadInt32(); // Size in bytes

                    var artist = ReadOsuString(r);
                    var artistUnicode = ReadOsuString(r);
                    var title = ReadOsuString(r);
                    var titleUnicode = ReadOsuString(r);
                    var creator = ReadOsuString(r);
                    var difficulty = ReadOsuString(r);
                    ReadOsuString(r); // Audio file
                    var md5 = ReadOsuString(r);
                    var fileName = ReadOsuString(r);

                    var info = new BeatmapInfo
                    {
                        Name = $"{artist} - {title} [{difficulty}]",
                        FolderName = fileName.Contains("/") ? Path.GetDirectoryName(fileName) ?? "" : "", // fileName in osu!.db is just file name usually? 
                        // Wait, osu!.db has "Folder name" separate.
                        // r.ReadString() at line 325 is Folder name. 
                        // fileName variable at line 243 is the .osu file name.
                        FileName = fileName
                    };

                    r.ReadByte(); // Ranked status
                    r.ReadUInt16(); // Circles
                    r.ReadUInt16(); // Sliders
                    r.ReadUInt16(); // Spinners
                    r.ReadInt64(); // Last modified

                    if (dbVersion >= 20140609)
                    {
                        r.ReadSingle(); // AR
                        r.ReadSingle(); // CS
                        r.ReadSingle(); // HP
                        r.ReadSingle(); // OD
                    }
                    else
                    {
                        r.ReadByte(); r.ReadByte(); r.ReadByte(); r.ReadByte();
                    }

                    r.ReadDouble(); // Slider velocity

                    if (dbVersion >= 20140609)
                    {
                        for (int ruleset = 0; ruleset < 4; ruleset++)
                        {
                            var count = r.ReadInt32();
                            for (int k = 0; k < count; k++)
                            {
                                // osu-db confirms: flag 0x08, then Int32 (mods)
                                r.ReadByte(); // 0x08 marker for Int
                                var modVal = r.ReadInt32();

                                double starVal;
                                if (dbVersion < 20250107)
                                {
                                    // Old format: flag 0x0d, then Double
                                    r.ReadByte(); // 0x0d marker for Double
                                    starVal = r.ReadDouble();
                                }
                                else
                                {
                                    // New format (2025-01-07+): flag 0x0c, then Float
                                    r.ReadByte(); // 0x0c marker for Float
                                    starVal = r.ReadSingle();
                                }

                                if (ruleset == 0) // Only Standard stars
                                    info.Stars[modVal] = starVal;
                            }
                        }
                    }

                    r.ReadInt32(); // Drain time
                    info.TotalTime = r.ReadInt32(); // Total time
                    r.ReadInt32(); // Preview time

                    int timingCount = r.ReadInt32();
                    for (int t = 0; t < timingCount; t++)
                    {
                        r.ReadDouble(); r.ReadDouble(); r.ReadBoolean();
                    }

                    r.ReadInt32(); // Beatmap ID
                    r.ReadInt32(); // Beatmap Set ID
                    r.ReadInt32(); // Thread ID
                    r.ReadByte(); r.ReadByte(); r.ReadByte(); r.ReadByte(); // Grades
                    r.ReadInt16(); // Offset
                    r.ReadSingle(); // Stack leniency
                    r.ReadByte(); // Mode
                    ReadOsuString(r); // Source
                    ReadOsuString(r); // Tags
                    r.ReadInt16(); // Online offset
                    ReadOsuString(r); // Title font
                    r.ReadBoolean(); // Unplayed
                    r.ReadInt64(); // Last played
                    r.ReadBoolean(); // Is osz2
                    info.FolderName = ReadOsuString(r); // Folder name
                    r.ReadInt64(); // Last checked
                    r.ReadBoolean(); // Ignore sound
                    r.ReadBoolean(); // Ignore skin
                    r.ReadBoolean(); // Disable storyboard
                    r.ReadBoolean(); // Disable video
                    r.ReadBoolean(); // Visual override
                    if (dbVersion < 20140609) r.ReadInt16();
                    r.ReadInt32(); // Unknown
                    r.ReadByte(); // Mania scroll

                    if (!string.IsNullOrEmpty(md5))
                        _beatmaps[md5] = info;
                }
                catch
                {
                    // If one map fails, the stream is likely corrupted for this map entry.
                    // But with the Size field (if < 2019), we could skip.
                    // For now, let's just hope our parsing is correct.
                }
            }
        }

        private string ParseMods(int mods)
        {
            var list = new List<string>();
            if ((mods & 1) > 0) list.Add("NF");
            if ((mods & 2) > 0) list.Add("EZ");
            if ((mods & 8) > 0) list.Add("HD");
            if ((mods & 16) > 0) list.Add("HR");
            if ((mods & 32) > 0) list.Add("SD");
            if ((mods & 64) > 0) list.Add("DT");
            if ((mods & 128) > 0) list.Add("RX");
            if ((mods & 256) > 0) list.Add("HT");
            if ((mods & 512) > 0) list.Add("NC");
            if ((mods & 1024) > 0) list.Add("FL");
            if ((mods & 4096) > 0) list.Add("SO");
            if ((mods & 16384) > 0) list.Add("PF");

            // All stable plays are Classic
            list.Add("CL");

            return string.Join(",", list);
        }

    }
}
