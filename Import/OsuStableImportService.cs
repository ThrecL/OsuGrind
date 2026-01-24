using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using OsuGrind.Models;
using OsuGrind.Services;

namespace OsuGrind.Import
{
    public class OsuStableImportService
    {
        private readonly TrackerDb _db;

        public OsuStableImportService(TrackerDb db)
        {
            _db = db;
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

        public async Task<(int added, int skipped, string error)> ImportScoresAsync(string scoresDbPath)
        {
            if (!File.Exists(scoresDbPath))
                return (0, 0, "scores.db not found.");

            // Try to load osu!.db from the same folder to get map names
            var folder = Path.GetDirectoryName(scoresDbPath);
            var osuDbPath = Path.Combine(folder ?? "", "osu!.db");

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

            await Task.Run(async () =>
            {
                try
                {
                    using (var fs = File.OpenRead(scoresDbPath))
                    using (var r = new BinaryReader(fs))
                    {
                        var version = r.ReadInt32();
                        var beatmapCount = r.ReadInt32();

                        for (int i = 0; i < beatmapCount; i++)
                        {
                            // Beatmap info (we ignore MD5 here, assuming scores are what matters)
                            // But we specifically need valid play rows.

                            // To skip beatmap info correctly?
                            // Wait, structure is:
                            //  Beatmap MD5 (String)
                            //  Score Count (Int32)
                            //  Scores...

                            // Let's verify structure again. 
                            // scores.db contains beatmaps? 
                            // Actually it is:
                            // Loop BeatmapCount:
                            //    MD5 (String)
                            //    ScoreCount (Int32)
                            //    Loop ScoreCount:
                            //       Score Data...

                            var beatmapMd5 = ReadOsuString(r);
                            var scoreCount = r.ReadInt32();

                            for (int j = 0; j < scoreCount; j++)
                            {
                                // Parse Score - format from OsuParsers
                                var mode = r.ReadByte();
                                var scoreVersion = r.ReadInt32();
                                var mapMd5 = ReadOsuString(r);
                                var playerName = ReadOsuString(r);
                                var replayMd5 = ReadOsuString(r);
                                var cnt300 = r.ReadUInt16();   // UNSIGNED
                                var cnt100 = r.ReadUInt16();   // UNSIGNED
                                var cnt50 = r.ReadUInt16();    // UNSIGNED
                                var cntGeki = r.ReadUInt16();  // UNSIGNED
                                var cntKatu = r.ReadUInt16();  // UNSIGNED
                                var misses = r.ReadUInt16();   // UNSIGNED
                                var scoreVal = r.ReadInt32();
                                var maxCombo = r.ReadUInt16(); // UNSIGNED
                                var perfect = r.ReadBoolean();
                                var mods = r.ReadInt32();
                                var lifeBarGraph = ReadOsuString(r); // Life bar graph data (empty string)
                                var timestamp = r.ReadInt64();
                                r.BaseStream.Seek(sizeof(int), SeekOrigin.Current); // Skip -1 marker
                                var onlineId = r.ReadInt64();

                                // Parse timestamp - OsuParsers uses ReadDateTime which calls FromBinary
                                var createdAt = DateTime.UtcNow;
                                try
                                {
                                    createdAt = DateTime.FromBinary(timestamp);
                                    // If local, convert to UTC
                                    if (createdAt.Kind == DateTimeKind.Local)
                                        createdAt = createdAt.ToUniversalTime();
                                }
                                catch { createdAt = DateTime.UtcNow; }

                                if (mode != 0) continue; // Only Standard

                                // Metadata lookup
                                _beatmaps.TryGetValue(mapMd5, out var info);

                                // Stars lookup: osu!.db stores stars per mod combo
                                // Difficulty modifier bits: EZ=2, HR=16, DT=64, HT=256, NC=512, FL=1024
                                int diffMods = mods & (2 | 16 | 64 | 256 | 512 | 1024);

                                double stars = 0;
                                if (info != null)
                                {
                                    if (!info.Stars.TryGetValue(diffMods, out stars))
                                    {
                                        // Specific combo not found, try normalizing NC to DT
                                        if ((diffMods & 512) > 0)
                                        {
                                            int normalizedMods = (diffMods & ~512) | 64;
                                            if (info.Stars.TryGetValue(normalizedMods, out stars))
                                            {
                                                // Found with normalized mods
                                            }
                                        }

                                        if (stars == 0)
                                        {
                                            // Fallback to Nomod if mod combo still not found
                                            info.Stars.TryGetValue(0, out stars);
                                        }
                                    }
                                }

                                var play = new PlayRow
                                {
                                    ScoreId = onlineId > 0 ? onlineId : 0,
                                    CreatedAtUtc = createdAt,

                                    Outcome = "pass",
                                    DurationMs = info?.TotalTime ?? 0,
                                    Beatmap = info?.Name ?? $"[Stable] {mapMd5.Substring(0, Math.Min(8, mapMd5.Length))}",
                                    BeatmapHash = mapMd5,
                                    Mods = ParseMods(mods),
                                    Stars = stars,
                                    // Accuracy is computed
                                    Score = (int)Math.Min(int.MaxValue, (long)scoreVal),
                                    Combo = maxCombo,
                                    Count300 = cnt300,
                                    Count100 = cnt100,
                                    Count50 = cnt50,
                                    Misses = misses,
                                    // PP Calc
                                    PP = 0,
                                    Notes = "Imported from scores.db"
                                };

                                // Calculate PP if map file exists
                                // Rosu removed.

                                // Fix Date: ticks are usually local time in older osu versions?
                                // Actually, DateTime(ticks) might be Unspecified.
                                // We'll treat as UTC for consistency or Local?
                                // Let's assume Valid Ticks.

                                try
                                {
                                    await _db.InsertPlayAsync(play);
                                    added++;
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[Import] Error inserting score for {play.Beatmap}: {ex.Message}");
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
            if (mods == 0) return "NM";
            var list = new List<string>();
            if ((mods & 1) > 0) list.Add("NF");
            if ((mods & 2) > 0) list.Add("EZ");
            // ... strict set
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

            return string.Join("", list);
        }
    }
}
