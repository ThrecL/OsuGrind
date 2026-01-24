using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Realms;
using Realms.Exceptions;

namespace OsuGrind.Services
{
    /// <summary>
    /// Service for exporting beatmaps and replays from osu!lazer's Realm database.
    /// 
    /// ╔══════════════════════════════════════════════════════════════════════════════╗
    /// ║  WARNING TO AI AGENTS: DO NOT MODIFY THE REALM CONFIGURATION IN THIS FILE!  ║
    /// ╠══════════════════════════════════════════════════════════════════════════════╣
    /// ║  The osu!lazer client uses a proprietary Realm schema that changes with      ║
    /// ║  every game update. We MUST use IsDynamic = true to bypass schema validation ║
    /// ║  because we don't have matching C# model classes for their schema.           ║
    /// ║                                                                              ║
    /// ║  NEVER change the RealmConfiguration to use:                                 ║
    /// ║    - SchemaVersion scanning loops (versions 51-150, etc.)                    ║
    /// ║    - IsReadOnly = false (causes schema validation)                           ║
    /// ║    - Any typed Realm object classes                                          ║
    /// ║                                                                              ║
    /// ║  The ONLY correct configuration is:                                          ║
    /// ║    new RealmConfiguration(path) { IsDynamic = true, IsReadOnly = true }      ║
    /// ║                                                                              ║
    /// ║  This has been broken multiple times by AI agents trying to "fix" it.        ║
    /// ╚══════════════════════════════════════════════════════════════════════════════╝
    /// </summary>
    public class RealmExportService
    {
        public static event Action<string>? OnLog;
        private static void Log(string msg) => OnLog?.Invoke($"[RealmExport] {msg}");

        private readonly string _lazerAppDataPath;
        private readonly TrackerDb _db;

        public RealmExportService()
        {
            _db = new TrackerDb();

            string roamingPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu");
            string localPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu");
            
            string? storagePath = roamingPath;

            // 1. Check Roaming storage.ini
            string storageIni = Path.Combine(roamingPath, "storage.ini");
            if (!File.Exists(storageIni)) storageIni = Path.Combine(localPath, "storage.ini");

            // Check G: drive fallback found by search
            string gDrivePath = @"G:\osu-lazer-data";
            if (Directory.Exists(gDrivePath) && File.Exists(Path.Combine(gDrivePath, "client.realm")))
            {
                storagePath = gDrivePath;
                Log($"Found likely data path on G: drive: {storagePath}");
            }

            if (File.Exists(storageIni))
            {
                Log($"Found storage.ini at {storageIni}");
                try {
                    var lines = File.ReadAllLines(storageIni);
                    foreach(var line in lines) Log($" storage.ini line: {line}");
                    var fullPathLine = lines.FirstOrDefault(l => l.StartsWith("FullPath =", StringComparison.OrdinalIgnoreCase));
                    if (fullPathLine != null)
                    {
                        var path = fullPathLine.Split('=').Last().Trim();
                        if (Directory.Exists(path))
                        {
                            storagePath = path;
                            Log($"Detected custom data path from storage.ini: {storagePath}");
                        }
                    }
                } catch (Exception ex) { Log($"Error reading storage.ini: {ex.Message}"); }
            }

            string? customPath = SettingsManager.Current.LazerPath;
            if (!string.IsNullOrEmpty(customPath) && Directory.Exists(customPath) && File.Exists(Path.Combine(customPath, "client.realm")))
            {
                _lazerAppDataPath = customPath;
                Log("Using custom path from Settings.");
            }
            else
            {
                _lazerAppDataPath = storagePath ?? roamingPath;
                
                string rFile = Path.Combine(roamingPath, "client.realm");
                string lFile = Path.Combine(localPath, "client.realm");
                
                if (File.Exists(lFile) && (!File.Exists(rFile) || new FileInfo(lFile).Length > new FileInfo(rFile).Length + 1024*1024))
                {
                    if (storagePath == roamingPath || string.IsNullOrEmpty(storagePath))
                    {
                        _lazerAppDataPath = localPath;
                        Log("Local/osu realm is significantly larger than Roaming/osu, switching fallback.");
                    }
                }
            }

            Log($"Final Lazer data path: {_lazerAppDataPath}");
            string realmFile = Path.Combine(_lazerAppDataPath, "client.realm");
            bool exists = File.Exists(realmFile);
            long size = exists ? new FileInfo(realmFile).Length : 0;
            Log($"client.realm exists: {exists} (Size: {size / 1024} KB)");
        }

        public async Task<string?> ExportBeatmapAsync(string beatmapHash)
        {
            return await Task.Run(() => ExportBeatmapInternal(beatmapHash));
        }

        private static string ResolveMetadataValue(string primary, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(primary) && !primary.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return primary;
            }

            if (!string.IsNullOrWhiteSpace(fallback) && !fallback.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return fallback;
            }

            return "Unknown";
        }

        private static dynamic? FindMetadataById(Realm realm, string metadataId)
        {
            foreach (var meta in realm.DynamicApi.All("BeatmapMetadata"))
            {
                dynamic d = meta;
                try
                {
                    if (d.Id?.ToString() == metadataId)
                    {
                        return d;
                    }
                }
                catch
                {
                    // ignore bad rows
                }
            }

            return null;
        }

        private string? ExportBeatmapInternal(string beatmapHash)
        {
            string realmPath = Path.Combine(_lazerAppDataPath, "client.realm");
            if (!File.Exists(realmPath)) return null;

            string tempRealm = Path.Combine(Path.GetTempPath(), $"export_map_{Guid.NewGuid()}.realm");
            try
            {
                // Robust copy that handles file locks
                using (var sourceStream = new FileStream(realmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var destinationStream = new FileStream(tempRealm, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    sourceStream.CopyTo(destinationStream);
                }
                Log($"Searching for beatmap {beatmapHash.Substring(0, 8)}...");

                try
                {
                    // !! CRITICAL: IsDynamic=true is REQUIRED - see class documentation !!
                    // DO NOT add SchemaVersion or change IsReadOnly to false - it will break!
                    var config = new RealmConfiguration(tempRealm) { IsDynamic = true, IsReadOnly = true };
                    using var realm = Realm.GetInstance(config);
                    
                    Log($"Realm opened in dynamic mode (schema v{realm.Config.SchemaVersion})");
                    
                    dynamic? beatmap = null;
                    // Manual scan to avoid LINQ expression tree issues with dynamic
                    foreach (var b in realm.DynamicApi.All("Beatmap"))
                    {
                        dynamic d = b;
                        string? md5 = null;
                        string? hash = null;
                        try { md5 = d.MD5Hash; } catch { }
                        try { hash = d.Hash; } catch { }
                        
                        if (md5 == beatmapHash || hash == beatmapHash)
                        {
                            beatmap = d;
                            break;
                        }
                    }

                    if (beatmap == null)
                    {
                        Log("Beatmap not found in Realm database");
                        return null;
                    }
        
                    dynamic? set = null;
                    try { set = beatmap.BeatmapSet; } catch { }
                    if (set == null)
                    {
                        Log("BeatmapSet not found for this beatmap");
                        return null;
                    }

                    string artist = "Unknown"; 
                    string title = "Unknown";
                    string artistUnicode = "Unknown";
                    string titleUnicode = "Unknown";
                    string? metadataId = null;
                    dynamic? metadata = null;
                    try { metadata = set.Metadata; } catch { }

                    if (metadata != null)
                    {
                        try { artist = metadata.Artist ?? "Unknown"; } catch {}
                        try { title = metadata.Title ?? "Unknown"; } catch {}
                        try { artistUnicode = metadata.ArtistUnicode ?? "Unknown"; } catch {}
                        try { titleUnicode = metadata.TitleUnicode ?? "Unknown"; } catch {}
                        try { metadataId = metadata.Id?.ToString(); } catch {}
                    }

                    if (metadata == null)
                    {
                        try { metadataId = set.MetadataId?.ToString(); } catch {}
                        if (string.IsNullOrWhiteSpace(metadataId))
                        {
                            try { metadataId = beatmap.MetadataId?.ToString(); } catch {}
                        }

                        if (!string.IsNullOrWhiteSpace(metadataId))
                        {
                            metadata = FindMetadataById(realm, metadataId);
                            if (metadata != null)
                            {
                                try { artist = metadata.Artist ?? "Unknown"; } catch {}
                                try { title = metadata.Title ?? "Unknown"; } catch {}
                                try { artistUnicode = metadata.ArtistUnicode ?? "Unknown"; } catch {}
                                try { titleUnicode = metadata.TitleUnicode ?? "Unknown"; } catch {}
                            }
                        }
                    }

                    try { artist = ResolveMetadataValue(artist, set.Artist ?? "Unknown"); } catch {}
                    try { title = ResolveMetadataValue(title, set.Title ?? "Unknown"); } catch {}

                    artist = ResolveMetadataValue(artist, artistUnicode);
                    title = ResolveMetadataValue(title, titleUnicode);

                    if ((artist == "Unknown" || title == "Unknown") && metadata != null)
                    {
                        try { artist = metadata?.Title?.Split(" - ").FirstOrDefault() ?? artist; } catch { }
                    }

                    var folderName = $"{artist} - {title} ({beatmapHash.Substring(0, 8)})";
                    folderName = string.Join("_", folderName.Split(Path.GetInvalidFileNameChars()));
                    
                    string exportDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rewind", "Songs", folderName);
                    Directory.CreateDirectory(exportDir);

                    Log($"Found map! Exporting to {exportDir}");

                    int fileCount = 0;
                    try
                    {
                        foreach (dynamic fileUsage in set.Files)
                        {
                            string? fname = null;
                            try { fname = fileUsage.Filename; } catch { }
                            if (!string.IsNullOrEmpty(fname))
                            {
                                string? hash = null;
                                try { hash = fileUsage.File?.Hash; } catch { }
                                if (!string.IsNullOrEmpty(hash))
                                {
                                    var sourcePath = GetLazerFilePath(hash);
                                    if (File.Exists(sourcePath))
                                    {
                                        var destPath = Path.Combine(exportDir, fname);
                                        File.Copy(sourcePath, destPath, true);
                                        fileCount++;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Log($"Error iterating files: {ex.Message}"); }
                    
                    Log($"Exported {fileCount} files");
                    if (fileCount > 0 || Directory.GetFiles(exportDir).Any()) 
                        return exportDir;
                }
                catch (Exception ex)
                {
                    Log($"Realm open/query error: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"ExportBeatmap error: {ex.Message}");
            }
            finally
            {
                if (File.Exists(tempRealm)) try { File.Delete(tempRealm); } catch { }
            }
            return null;
        }

        public async Task<string?> SearchReplayByTimeAsync(DateTime playTime, TimeSpan searchWindow, string? mapHash = null)
        {
            // 1. Try Realm Search First
            var result = await SearchReplayInRealmAsync(playTime, searchWindow, mapHash);
            if (!string.IsNullOrEmpty(result)) return result;

            // 2. Fallback: Scan 'files' directory directly for recent .osr files
            return await ScanFilesDirectoryAsync(playTime, searchWindow);
        }

        private async Task<string?> SearchReplayInRealmAsync(DateTime playTime, TimeSpan searchWindow, string? mapHash = null)
        {
            return await Task.Run(() =>
            {
                string realmPath = Path.Combine(_lazerAppDataPath, "client.realm");
                if (!File.Exists(realmPath)) return null;

                string tempRealm = Path.Combine(Path.GetTempPath(), $"client_search_temp_{Guid.NewGuid()}.realm");
                string replaysDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Replays");
                Directory.CreateDirectory(replaysDir);

                try
                {
                    Log($"Copying Realm database...");
                    using (var sourceStream = new FileStream(realmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var destinationStream = new FileStream(tempRealm, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        sourceStream.CopyTo(destinationStream);
                    }

                    try
                    {
                        // !! CRITICAL: IsDynamic=true is REQUIRED - see class documentation !!
                        // DO NOT add SchemaVersion or change IsReadOnly to false - it will break!
                        var config = new RealmConfiguration(tempRealm) { IsDynamic = true, IsReadOnly = true };
                        using var realm = Realm.GetInstance(config);
                        
                        var tables = realm.Schema.Select(s => s.Name).ToList();
                        string scoreTable = tables.FirstOrDefault(t => t.Equals("ScoreInfo", StringComparison.OrdinalIgnoreCase)) 
                                         ?? tables.FirstOrDefault(t => t.Contains("Score", StringComparison.OrdinalIgnoreCase)) 
                                         ?? "ScoreInfo";

                        Log($"Realm opened in dynamic mode. Querying '{scoreTable}' (tables: {string.Join(", ", tables.Take(5))}...)");

                        var playTimeUtc = playTime.Kind == DateTimeKind.Unspecified
                            ? DateTime.SpecifyKind(playTime, DateTimeKind.Utc)
                            : playTime.ToUniversalTime();

                        var lowerBound = new DateTimeOffset(playTimeUtc.Subtract(searchWindow), TimeSpan.Zero);
                        var upperBound = new DateTimeOffset(playTimeUtc.Add(searchWindow), TimeSpan.Zero);

                        // Use dynamic query to avoid type mismatch on Date field
                        var allScores = realm.DynamicApi.All(scoreTable);
                        dynamic? bestMatch = null;
                        double minDiff = double.MaxValue;

                        // Manual iteration to avoid LINQ/Type errors
                        int count = 0;
                        foreach (var scoreObj in allScores)
                        {
                            count++;
                            try
                            {
                                dynamic s = scoreObj;
                                
                                // 1. Check map hash if provided (fastest filter)
                                if (!string.IsNullOrEmpty(mapHash))
                                {
                                    string? sMapHash = null;
                                    try { sMapHash = s.BeatmapInfo?.MD5Hash; } catch {}
                                    if (sMapHash == null) try { sMapHash = s.BeatmapInfo?.Hash; } catch {}
                                    if (sMapHash == null) try { sMapHash = s.BeatmapHash; } catch {}
                                    
                                    if (sMapHash == null || !sMapHash.Equals(mapHash, StringComparison.OrdinalIgnoreCase))
                                        continue;
                                }

                                // 2. Check Date (safely handle DateTime vs DateTimeOffset)
                                object? dateObj = null;
                                try { dateObj = s.Date; } catch { }
                                if (dateObj == null) continue;
                                
                                DateTimeOffset scoreDate;
                                if (dateObj is DateTimeOffset dto) scoreDate = dto;
                                else if (dateObj is DateTime dt) scoreDate = new DateTimeOffset(dt, TimeSpan.Zero);
                                else continue;

                                if (scoreDate >= lowerBound && scoreDate <= upperBound)
                                {
                                    double diff = Math.Abs((scoreDate - playTimeUtc).TotalSeconds);
                                    if (diff < minDiff)
                                    {
                                        minDiff = diff;
                                        bestMatch = s;
                                    }
                                }
                            }
                            catch { continue; }
                        }

                        Log($"Scanned {count} scores");

                        if (bestMatch != null)
                        {
                            dynamic s = bestMatch;
                            // Handle Date safely for logging
                            DateTimeOffset finalDate;
                            try { finalDate = s.Date is DateTimeOffset d ? d : new DateTimeOffset((DateTime)s.Date, TimeSpan.Zero); }
                            catch { finalDate = DateTimeOffset.UtcNow; }
                            
                            Log($"Match found! Score Date: {finalDate}, Diff: {minDiff:F1}s");
                            
                            string? replayHash = null;
                            try
                            {
                                var files = s.Files;
                                if (files != null)
                                {
                                    foreach (dynamic fileUsage in files)
                                    {
                                        string? fname = null;
                                        try { fname = fileUsage.Filename; } catch { }
                                        if (fname != null && fname.EndsWith(".osr", StringComparison.OrdinalIgnoreCase))
                                        {
                                            try { replayHash = fileUsage.File?.Hash; } catch { }
                                            if (!string.IsNullOrEmpty(replayHash)) break;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex) { Log($"Error reading files: {ex.Message}"); }

                            // Fallback: try Hash property directly (some schema versions)
                            if (string.IsNullOrEmpty(replayHash))
                            {
                                try { replayHash = s.Hash; } catch { }
                            }

                            if (!string.IsNullOrEmpty(replayHash))
                            {
                                string sourceFile = GetLazerFilePath(replayHash);
                                if (File.Exists(sourceFile))
                                {
                                    string scoreHash = "";
                                    try { scoreHash = s.Hash?.Substring(0, 8) ?? "unknown"; } catch { scoreHash = "unknown"; }
                                    
                                    var unixTime = finalDate.ToUnixTimeSeconds();
                                    
                                    string destFile = Path.Combine(replaysDir, $"OnDemand_{unixTime}_{scoreHash}.osr");
                                    File.Copy(sourceFile, destFile, true);
                                    Log($"Exported replay to {destFile}");
                                    return destFile;
                                }
                                else
                                {
                                    Log($"Replay file exists in DB but missing on disk: {sourceFile}");
                                }
                            }
                            else
                            {
                                Log("Score found but has no .osr file attached.");
                            }
                            // If we found the score but couldn't get the file, return null so fallback can run
                            return null; 
                        }
                        else
                        {
                            Log("No matching score found in time window");
                        }
                    }
                    catch (Exception ex) 
                    { 
                        Log($"Realm error: {ex.Message}"); 
                    }
                }
                finally
                {
                    if (File.Exists(tempRealm)) try { File.Delete(tempRealm); } catch { }
                }

                return null;
            });
        }

        private async Task<string?> ScanFilesDirectoryAsync(DateTime playTime, TimeSpan searchWindow)
        {
            string filesRoot = Path.Combine(_lazerAppDataPath, "files");
            
            // Also check for 'exports' folder
            string exportsRoot = Path.Combine(_lazerAppDataPath, "exports");
            string gExports = @"G:\osu-lazer-data\exports";
            string cExports = @"C:\Users\3clex\AppData\Roaming\osu\exports";

            Log("Scanning files directory for recent replays (Fallback)...");
            return await Task.Run(() =>
            {
                try
                {
                    var playTimeUtc = playTime.Kind == DateTimeKind.Unspecified
                                ? DateTime.SpecifyKind(playTime, DateTimeKind.Utc)
                                : playTime.ToUniversalTime();

                    var candidatePaths = new List<string>();
                    
                    if (Directory.Exists(filesRoot)) candidatePaths.Add(filesRoot);
                    if (Directory.Exists(exportsRoot)) candidatePaths.Add(exportsRoot);
                    if (Directory.Exists(gExports)) candidatePaths.Add(gExports);
                    if (Directory.Exists(cExports)) candidatePaths.Add(cExports);

                    foreach (var root in candidatePaths)
                    {
                        if (!Directory.Exists(root)) continue;
                        
                        Log($"Scanning {root}...");
                        var directory = new DirectoryInfo(root);
                        // Find recently modified files
                        // Search for * because hashed files have no extension, but exports have .osr
                        var recentFiles = directory.EnumerateFiles("*", SearchOption.AllDirectories)
                            .Where(f => f.Length > 100 && Math.Abs((f.LastWriteTimeUtc - playTimeUtc).TotalHours) < 1) 
                            .OrderByDescending(f => f.LastWriteTimeUtc)
                            .Take(100) 
                            .ToList();

                        foreach (var file in recentFiles)
                        {
                            double diff = Math.Abs((file.LastWriteTimeUtc - playTimeUtc).TotalSeconds);
                            // Also check creation time
                            double diffCreate = Math.Abs((file.CreationTimeUtc - playTimeUtc).TotalSeconds);
                            
                            double bestDiff = Math.Min(diff, diffCreate);

                            // Log candidate
                            // Log($"Candidate: {file.Name} (Diff: {bestDiff:F1}s)");

                            if (bestDiff <= searchWindow.TotalSeconds)
                            {
                                // Verify if it's an .osr file (header check or extension)
                                if (file.Extension.Equals(".osr", StringComparison.OrdinalIgnoreCase) || file.Name.Length > 30) // weak heuristic
                                {
                                     string replaysDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Replays");
                                     Directory.CreateDirectory(replaysDir);
                                     string destFile = Path.Combine(replaysDir, $"OnDemand_Loose_{new DateTimeOffset(file.LastWriteTime).ToUnixTimeSeconds()}.osr");
                                     File.Copy(file.FullName, destFile, true);
                                     Log($"Found loose replay match: {file.FullName}");
                                     return destFile;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { Log($"Scan error: {ex.Message}"); }
                return null;
            });
        }

        private string GetLazerFilePath(string hash)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length < 2) return "";
            return Path.Combine(_lazerAppDataPath, "files", hash.Substring(0, 1), hash.Substring(0, 2), hash);
        }
    }
}
