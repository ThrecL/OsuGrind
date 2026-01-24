using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OsuGrind.Services;
using OsuParsers.Decoders;
using OsuParsers.Enums;
using OsuParsers.Replays;

namespace OsuGrind.Api;

public class ApiServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _webRoot;
    private readonly TrackerDb _db;
    private readonly AuthService _authService;
    private readonly CancellationTokenSource _cts;

    private readonly List<WebSocket> _liveClients = new();
    private readonly object _clientsLock = new();
    private Task? _listenTask;

    public int Port { get; }

    public ApiServer(TrackerDb db, int port = 5173)
    {
        _db = db;
        _authService = new AuthService();
        Port = port;
        _webRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "WebUI");
        _cts = new CancellationTokenSource();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");

        RealmExportService.OnLog += async (msg) => await BroadcastLog(msg);
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            _listenTask = Task.Run(ListenLoop);
            Console.WriteLine($"[ApiServer] Listening on http://localhost:{Port}/");
        }
        catch (Exception ex)
        {
            File.WriteAllText("api_server_error.txt", ex.ToString());
            throw;
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _listenTask?.Wait(TimeSpan.FromSeconds(2));
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (HttpListenerException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.WriteLine($"[ApiServer] Listen error: {ex.Message}"); }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;

        // Add CORS headers to every response
        context.Response.AddHeader("Access-Control-Allow-Origin", "*");
        context.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
        context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");

        if (method == "OPTIONS")
        {
            context.Response.StatusCode = 200;
            context.Response.Close();
            return;
        }

        try
        {
            if (path == "/ws/live" && context.Request.IsWebSocketRequest) { await HandleWebSocket(context); return; }
            if (path.StartsWith("/api/") || path == "/callback" || path.StartsWith("/rewind/file/")) { await HandleApiRequest(context, path, method); return; }
            if (path == "/rewind/file") { await HandleApiRequest(context, path, method); return; }
            await ServeStaticFile(context, path);
        }
        catch (Exception ex) { await SendJson(context.Response, new { error = ex.Message }, 500); }
    }

    private async Task HandleRewindPp(HttpListenerContext context)
    {
        var payload = await ReadJsonBodyAsync<Models.RewindPpRequest>(context.Request);
        if (payload == null) { await SendJson(context.Response, new { error = "Invalid payload" }, 400); return; }

        // LIVE RECORDING LOGIC: If ScoreId is provided, try to use recorded timeline
        if (payload.ScoreId > 0)
        {
            Console.WriteLine($"[PP TIMELINE] Request for ScoreId={payload.ScoreId}, Passed={payload.PassedObjects}");
            var timeline = await _db.GetPpTimelineAsync(payload.ScoreId);
            if (timeline != null && timeline.Count > 0)
            {
                Console.WriteLine($"[PP TIMELINE] Found timeline for ScoreId={payload.ScoreId}, Count={timeline.Count}");
                int passed = payload.PassedObjects > 0 ? payload.PassedObjects : (payload.Count300 + payload.Count100 + payload.Count50 + payload.Misses);
                // Return recorded stats at index 'passed'
                int index = Math.Clamp(passed, 0, timeline.Count - 1);
                var stats = timeline[index];

                if (stats.Length >= 7)
                {
                    await SendJson(context.Response, new
                    {
                        pp = stats[0],
                        combo = stats[1],
                        acc = stats[2],
                        h300 = stats[3],
                        h100 = stats[4],
                        h50 = stats[5],
                        miss = stats[6],
                        source = "live_record"
                    });
                }
                else
                {
                    await SendJson(context.Response, new { pp = stats[0], source = "live_record" });
                }
                return;
            }
            else
            {
                Console.WriteLine($"[PP TIMELINE] No timeline found for ScoreId={payload.ScoreId}");
            }
        }

        // IMPORTED PLAYS LOGIC (Fallback)
        // LOG EVERYTHING
        Console.WriteLine($"[PP DEBUG] Received Request: Hash={payload.BeatmapHash}, Combo={payload.Combo}, 300={payload.Count300}, 100={payload.Count100}, 50={payload.Count50}, Miss={payload.Misses}, Passed={payload.PassedObjects}, Ends={payload.SliderEndHits}, Ticks={payload.LargeTickHits}, Mods={string.Join(",", payload.Mods)}");

        string? mapPath = payload.BeatmapPath;
        if (string.IsNullOrEmpty(mapPath) && !string.IsNullOrEmpty(payload.BeatmapHash))
        {
            // 1. Try Lazer storage directly (if hash is the blob hash)
            mapPath = RosuService.GetBeatmapPath(payload.BeatmapHash, SettingsManager.Current.LazerPath);

            // 2. Try searching in exported songs
            if (string.IsNullOrEmpty(mapPath) || !File.Exists(mapPath))
            {
                var songsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Songs");
                if (Directory.Exists(songsRoot))
                {
                    var shortHash = payload.BeatmapHash.Substring(0, 8);
                    var dir = Directory.GetDirectories(songsRoot, $"*({shortHash})*").FirstOrDefault();
                    if (dir != null)
                    {
                        mapPath = Directory.GetFiles(dir, "*.osu").FirstOrDefault();
                    }
                }
            }

            // 3. Last resort: Export it now (might be slow but accurate)
            if (string.IsNullOrEmpty(mapPath) || !File.Exists(mapPath))
            {
                var exportService = new RealmExportService();
                var exportedDir = await exportService.ExportBeatmapAsync(payload.BeatmapHash);
                if (!string.IsNullOrEmpty(exportedDir) && Directory.Exists(exportedDir))
                {
                    mapPath = Directory.GetFiles(exportedDir, "*.osu").FirstOrDefault();
                }
            }
        }

        if (string.IsNullOrEmpty(mapPath) || !File.Exists(mapPath))
        {
            Console.WriteLine($"[PP DEBUG] Error: Map not found for hash '{payload.BeatmapHash}'.");
            await SendJson(context.Response, new { pp = 0, error = "Map not found" });
            return;
        }

        Console.WriteLine($"[PP DEBUG] Using Map Path: {mapPath}");

        try {
            using var rosu = new RosuService();
            rosu.UpdateContext(mapPath);
            
            uint modsBits = RosuService.ModsToRosuStats(payload.Mods);
            int passed = payload.PassedObjects > 0 ? payload.PassedObjects : (payload.Count300 + payload.Count100 + payload.Count50 + payload.Misses);
            double clockRate = RosuService.GetClockRateFromMods(modsBits);
            
            double stars = rosu.GetStars(modsBits, passed, clockRate);
            double pp = rosu.CalculatePp(modsBits, payload.Combo, payload.Count300, payload.Count100, payload.Count50, payload.Misses, passed, 
                sliderEndHits: payload.SliderEndHits, 
                smallTickHits: payload.SmallTickHits, 
                largeTickHits: payload.LargeTickHits, 
                clockRate: clockRate);
            
            Console.WriteLine($"[PP DEBUG] Result: {pp:F2}pp | Stars: {stars:F2}* | Combo: {payload.Combo} | Passed: {passed}/{payload.PassedObjects} | Map: {Path.GetFileName(mapPath)}");
            await SendJson(context.Response, new { pp, stars });
        } catch (Exception ex) {
            Console.WriteLine($"[ApiServer] PP calculation error: {ex.Message}");
            await SendJson(context.Response, new { pp = 0, error = ex.Message });
        }
    }

    private async Task HandleApiRequest(HttpListenerContext context, string path, string method)
    {
        var response = context.Response;
        switch (path)
        {
            case "/api/rewind/pp":
                if (method == "POST") await HandleRewindPp(context);
                break;
            case "/api/rewind/cursor-offsets":
                if (method == "POST") await HandleCursorOffsets(context);
                break;
            case "/api/history/recent":
                var limit = GetQueryInt(context, "limit", 50);
                await SendJson(response, await _db.FetchRecentAsync(limit));
                break;
            case "/api/history":
                var dateStr = context.Request.QueryString["date"];
                if (DateTime.TryParse(dateStr, out var date))
                {
                    var plays = await _db.FetchPlaysRangeAsync(date.Date.ToUniversalTime(), date.Date.AddDays(1).ToUniversalTime());
                    var totalMs = plays.Sum(p => p.DurationMs);
                    await SendJson(response, new { 
                        plays, 
                        stats = new { 
                            plays = plays.Count, 
                            avgAccuracy = plays.Count > 0 ? plays.Average(p => p.Accuracy * 100) : 0, 
                            avgPP = plays.Count > 0 ? plays.Average(p => p.PP) : 0, 
                            duration = totalMs >= 3600000 ? $"{totalMs/3600000}h {(totalMs%3600000)/60000}m" : $"{totalMs/60000}m"
                        }
                    });
                }
                else await SendJson(response, new { error = "Invalid date" }, 400);
                break;
            case "/api/history/month":
                var year = GetQueryInt(context, "year", DateTime.Now.Year);
                var month = GetQueryInt(context, "month", DateTime.Now.Month);
                await SendJson(response, new { playCounts = await GetMonthPlayCountsAsync(year, month) });
                break;
            case "/api/analytics":
                await SendJson(response, await GetAnalyticsDataAsync());
                break;
            case "/api/import/lazer":
                if (method == "POST")
                {
                    var importService = new LazerImportService(_db);
                    var (added, skipped, error) = await importService.ImportScoresAsync(SettingsManager.Current.LazerPath);
                    if (error != null) await SendJson(response, new { success = false, message = error }, 400);
                    else await SendJson(response, new { success = true, count = added, skipped });
                }
                break;
            case "/api/import/stable":
                if (method == "POST")
                {
                    // Placeholder for stable import
                    await SendJson(response, new { success = false, message = "Stable import not implemented yet" }, 501);
                }
                break;

            case "/api/settings":
                if (method == "GET")
                {
                    await SendJson(response, SettingsManager.Current);
                }
                else if (method == "POST")
                {
                    var payload = await ReadJsonBodyAsync<Dictionary<string, object>>(context.Request);
                    if (payload == null)
                    {
                        await SendJson(response, new { error = "Invalid settings payload" }, 400);
                        return;
                    }

                    SettingsManager.UpdateFromDictionary(payload);
                    await SendJson(response, new { success = true });
                }
                else
                {
                    await SendJson(response, new { error = "Method not allowed" }, 405);
                }
                break;

            case "/api/settings/delete-scores":
                if (method == "POST")
                {
                    await _db.DeleteAllScoresAsync();
                    await SendJson(response, new { success = true, count = 0 });
                }
                break;
            case "/api/settings/delete-beatmaps":
                if (method == "POST")
                {
                    await _db.DeleteAllBeatmapsAsync();
                    await SendJson(response, new { success = true, count = 0 });
                }
                break;

            case "/api/profile":
                string? token = SettingsManager.Current.AccessToken;
                if (!string.IsNullOrEmpty(token))
                {
                    var profile = await _authService.GetUserProfileAsync(token);
                    if (profile != null)
                    {
                        var p = profile.Value;
                        await SendJson(response, new { isLoggedIn = true, username = p.TryGetProperty("username", out var un) ? un.GetString() : null, avatarUrl = p.TryGetProperty("avatar_url", out var av) ? av.GetString() : null });
                        return;
                    }
                }
                await SendJson(response, new { isLoggedIn = false });
                break;
            default:
                if (path.StartsWith("/api/play/") && path.EndsWith("/rewind")) await HandleRewindRequest(context, ExtractIdFromPath(path, "/api/play/"));
                else if (path.StartsWith("/api/rewind/osr")) await HandleRewindOsr(context);
                else if (path.StartsWith("/rewind/file")) await HandleRewindFile(context);
                else if (path.StartsWith("/api/rewind/skins")) await HandleRewindSkins(context);
                else if (path.StartsWith("/api/rewind/skin-manifest")) await HandleRewindSkinManifest(context);
                else if (path.StartsWith("/api/rewind/skin-files/")) await HandleRewindSkinFileClean(context);
                else if (path.StartsWith("/api/background/")) 
                {
                    var identifier = path.Replace("/api/background/", "");
                    if (identifier == "stable")
                    {
                        var base64 = context.Request.QueryString["path"];
                        if (!string.IsNullOrEmpty(base64))
                        {
                            try {
                                var bytes = Convert.FromBase64String(base64);
                                var fullPath = Encoding.UTF8.GetString(bytes);
                                await ServeStableBackground(context, fullPath);
                            } catch {
                                await SendJson(response, new { error = "Invalid stable path" }, 400);
                            }
                        }
                        else await SendJson(response, new { error = "Path missing" }, 400);
                    }
                    else await ServeBackground(context, identifier);
                }
                else await SendJson(response, new { error = "Not found" }, 404);
                break;
        }
    }

    private string? ExtractReplayFromLazer(string hash, long ticks)
    {
        try
        {
            var lazerPath = SettingsManager.Current.LazerPath;
            if (string.IsNullOrEmpty(lazerPath) || !Directory.Exists(lazerPath))
            {
                // Auto-detect
                var candidates = new[]
                {
                    @"G:\osu-lazer-data",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu"),
                };
                foreach (var c in candidates)
                {
                    if (Directory.Exists(Path.Combine(c, "files")))
                    {
                        lazerPath = c;
                        break;
                    }
                }
            }
            
            // Check if user pointed to the root or the realm file
            if (File.Exists(lazerPath)) lazerPath = Path.GetDirectoryName(lazerPath);

            var folder1 = hash.Substring(0, 1);
            var folder2 = hash.Substring(0, 2);
            var sourcePath = Path.Combine(lazerPath ?? "", "files", folder1, folder2, hash);

            if (File.Exists(sourcePath))
            {
                var replaysDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Replays");
                Directory.CreateDirectory(replaysDir);
                string destName = $"{ticks}_{hash}.osr"; 
                var destPath = Path.Combine(replaysDir, destName);
                
                if (!File.Exists(destPath)) File.Copy(sourcePath, destPath, true);
                return destPath;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApiServer] Extraction failed: {ex.Message}");
        }
        return null;
    }


    private async Task HandleRewindRequest(HttpListenerContext context, int id)
    {
        var play = await _db.GetPlayAsync(id);
        if (play == null) { await SendJson(context.Response, new { error = "Play not found" }, 404); return; }

        Console.WriteLine($"[Rewind] Handling request for play {id}. MapPath: {play.MapPath}, ReplayFile: {play.ReplayFile}");

        string? replayPath = play.ReplayFile;

        if (string.IsNullOrEmpty(replayPath) || !File.Exists(replayPath))
        {
            if (!string.IsNullOrEmpty(play.ReplayHash))
            {
                replayPath = ExtractReplayFromLazer(play.ReplayHash, play.CreatedAtUtc.Ticks);
                if (!string.IsNullOrEmpty(replayPath))
                {
                    await _db.UpdatePlayReplayFileAsync(id, replayPath);
                    play.ReplayFile = replayPath;
                }
            }
        }

        if (string.IsNullOrEmpty(replayPath))
        {
            replayPath = await new RealmExportService().SearchReplayByTimeAsync(play.CreatedAtUtc, TimeSpan.FromMinutes(5), play.BeatmapHash);
            if (!string.IsNullOrEmpty(replayPath))
            {
                await _db.UpdatePlayReplayFileAsync(id, replayPath);
                play.ReplayFile = replayPath;
            }
        }

        if (string.IsNullOrEmpty(replayPath) || !File.Exists(replayPath))
        {
            // NEW FALLBACK: If replay is missing from DB, try to find it now
            string stablePath = SettingsManager.Current.StablePath ?? @"C:\project\osu!";
            if (!string.IsNullOrEmpty(play.BeatmapHash) && Directory.Exists(stablePath))
            {
                DebugService.Log($"[Rewind] Replay missing from record. Searching in {stablePath} for MD5 {play.BeatmapHash}", "ApiServer");
                var replayDir = Path.Combine(stablePath, "Data", "r");
                if (Directory.Exists(replayDir))
                {
                    var file = new DirectoryInfo(replayDir).GetFiles("*.osr")
                        .Where(f => f.Name.StartsWith(play.BeatmapHash, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();
                    
                    if (file != null && (DateTime.UtcNow - file.LastWriteTimeUtc).TotalHours < 24)
                    {
                        replayPath = file.FullName;
                        DebugService.Log($"[Rewind] Found lost replay: {file.Name}", "ApiServer");
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(replayPath) || !File.Exists(replayPath))
        {
            DebugService.Log($"[Rewind] Replay file not found after fallback: {replayPath ?? "(null)"}", "ApiServer");
            await SendJson(context.Response, new { error = "Replay not found" }, 404);
            return;
        }

        // EXTRACT REPLAY: Copy to local Replays folder if it's not already in the app directory
        var localReplaysDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Replays");
        DebugService.Log($"[Rewind] Source replay: {replayPath}", "ApiServer");
        DebugService.Log($"[Rewind] Target replays dir: {localReplaysDir}", "ApiServer");

        if (!replayPath.StartsWith(localReplaysDir, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(localReplaysDir);
                var fileName = Path.GetFileName(replayPath);
                // If it's a stable temp replay, give it a better name
                if (fileName.EndsWith(".osr") && (replayPath.Contains("Data" + Path.DirectorySeparatorChar + "r") || replayPath.Contains("Data/r")))
                {
                    fileName = $"Stable_{play.BeatmapHash.Substring(0, 8)}_{play.CreatedAtUtc.Ticks}.osr";
                }
                var destPath = Path.Combine(localReplaysDir, fileName);
                if (!File.Exists(destPath))
                {
                    File.Copy(replayPath, destPath, true);
                    DebugService.Log($"[Rewind] Extracted replay to: {destPath}", "ApiServer");
                }
                replayPath = destPath;
                // Update DB so we don't have to copy again
                await _db.UpdatePlayReplayFileAsync(id, replayPath);
            }
            catch (Exception ex)
            {
                DebugService.Log($"[Rewind] Failed to extract replay: {ex.Message}", "ApiServer");
            }
        }

        string? beatmapFolder = null;
        string? osuFileName = null;
        if (!string.IsNullOrEmpty(play.BeatmapHash))
        {
            var exportService = new RealmExportService();
            var osuPath = await exportService.ExportBeatmapAsync(play.BeatmapHash);
            if (!string.IsNullOrEmpty(osuPath))
            {
                if (Directory.Exists(osuPath))
                {
                    beatmapFolder = osuPath;
                }
                else
                {
                    beatmapFolder = Path.GetDirectoryName(osuPath);
                    osuFileName = Path.GetFileName(osuPath);
                }
            }
            // FALLBACK FOR STABLE: If Lazer export failed but we have a MapPath in the DB
            else if (!string.IsNullOrEmpty(play.MapPath) && File.Exists(play.MapPath))
            {
                DebugService.Log($"[Rewind] Lazer export failed, using Stable MapPath: {play.MapPath}", "ApiServer");
                try
                {
                    var sourceOsuFile = play.MapPath;
                    var sourceFolder = Path.GetDirectoryName(sourceOsuFile);
                    if (!string.IsNullOrEmpty(sourceFolder) && Directory.Exists(sourceFolder))
                    {
                        var artist = !string.IsNullOrEmpty(play.Artist) ? play.Artist : play.BeatmapArtist;
                        var title = !string.IsNullOrEmpty(play.Title) ? play.Title : play.BeatmapTitle;
                        
                        var folderName = $"{artist} - {title} ({play.BeatmapHash.Substring(0, 8)})";
                        folderName = string.Join("_", folderName.Split(Path.GetInvalidFileNameChars()));
                        var destFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Songs", folderName);
                        
                        if (!Directory.Exists(destFolder))
                        {
                            Directory.CreateDirectory(destFolder);
                            DebugService.Log($"[Rewind] Extracting stable beatmap to: {destFolder}", "ApiServer");
                            // Copy all files from source folder to dest folder
                            foreach (var file in Directory.GetFiles(sourceFolder))
                            {
                                var destPath = Path.Combine(destFolder, Path.GetFileName(file));
                                File.Copy(file, destPath, true);
                            }
                        }
                        else
                        {
                            DebugService.Log($"[Rewind] Beatmap already extracted to: {destFolder}", "ApiServer");
                        }
                        
                        beatmapFolder = destFolder;
                        osuFileName = Path.GetFileName(sourceOsuFile);
                    }
                }
                catch (Exception ex)
                {
                    DebugService.Log($"[Rewind] Failed to extract stable beatmap: {ex.Message}", "ApiServer");
                }
            }
        }

        if (!string.IsNullOrEmpty(beatmapFolder) && Directory.Exists(beatmapFolder))
        {
            if (beatmapFolder.Contains("Unknown - Unknown", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(play.Artist)
                && !string.IsNullOrWhiteSpace(play.Title))
            {
                var folderName = $"{play.Artist} - {play.Title} ({play.BeatmapHash?.Substring(0, 8)})";
                folderName = string.Join("_", folderName.Split(Path.GetInvalidFileNameChars()));
                var preferredFolder = Path.Combine(Path.GetDirectoryName(beatmapFolder) ?? string.Empty, folderName);
                if (Directory.Exists(preferredFolder))
                {
                    beatmapFolder = preferredFolder;
                }
            }

            var osuFiles = Directory.GetFiles(beatmapFolder, "*.osu", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            if (osuFiles.Count == 0)
            {
                await SendJson(context.Response, new { error = "No beatmap files found" }, 404);
                return;
            }

            if (!string.IsNullOrWhiteSpace(play.Difficulty))
            {
                var matchedFile = FindOsuFileForDifficulty(osuFiles, play.Difficulty);
                if (string.IsNullOrEmpty(matchedFile))
                {
                    await SendJson(context.Response, new { error = $"Beatmap difficulty not found: {play.Difficulty}" }, 404);
                    return;
                }

                osuFileName = matchedFile;
            }
            else
            {
                osuFileName ??= osuFiles.FirstOrDefault();
            }
        }

        if (!string.IsNullOrEmpty(osuFileName) && !osuFileName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
        {
            var candidatePath = Path.Combine(beatmapFolder ?? string.Empty, osuFileName + ".osu");
            if (File.Exists(candidatePath))
            {
                osuFileName = Path.GetFileName(candidatePath);
            }
        }

        Console.WriteLine($"[Rewind] Export info: folder={beatmapFolder ?? "(null)"}, osu={osuFileName ?? "(null)"}");

        var songsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Songs");
        var skinsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Skins");
        // Fetch full timeline if available
        var timeline = await _db.GetPpTimelineAsync(play.Id);

        var payload = new
        {
            replayPath,
            songsRoot,
            skinsRoot,
            beatmapFolder,
            osuFileName,
            mods = play.Mods ?? "",
            beatmapHash = play.BeatmapHash ?? "",
            replaysRoot = Path.GetDirectoryName(replayPath) ?? "",
            statsTimeline = timeline // Include full stats timeline for JS-side lookup
        };

        await SendJson(context.Response, payload);
    }

    private async Task HandleCursorOffsets(HttpListenerContext context)
    {
        if (context.Request.HttpMethod != "POST")
        {
            await SendJson(context.Response, new { error = "Method not allowed" }, 405);
            return;
        }

        var payload = await ReadJsonBodyAsync<Dictionary<string, object>>(context.Request);
        if (payload == null)
        {
            await SendJson(context.Response, new { error = "Invalid payload" }, 400);
            return;
        }

        if (!payload.TryGetValue("scoreId", out var scoreIdObj) || !long.TryParse(scoreIdObj?.ToString(), out var scoreId))
        {
            await SendJson(context.Response, new { error = "ScoreId missing" }, 400);
            return;
        }

        if (!payload.TryGetValue("offsets", out var offsetsObj))
        {
            await SendJson(context.Response, new { error = "Offsets missing" }, 400);
            return;
        }

        var json = JsonSerializer.Serialize(offsetsObj);
        await _db.UpdateCursorOffsetsAsync(scoreId, json);
        await SendJson(context.Response, new { success = true });
    }

    private static string? FindOsuFileForDifficulty(IEnumerable<string?> files, string difficulty)
    {
        var sanitizedDifficulty = difficulty.Trim();
        if (string.IsNullOrEmpty(sanitizedDifficulty))
        {
            return null;
        }

        var target = $"[{sanitizedDifficulty}]";
        var exactBracket = files.FirstOrDefault(name => name?.Contains(target, StringComparison.OrdinalIgnoreCase) == true);
        if (!string.IsNullOrEmpty(exactBracket))
        {
            return exactBracket;
        }

        var normalizedDifficulty = sanitizedDifficulty.Replace(" ", string.Empty);
        return files.FirstOrDefault(name =>
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            var fileNormalized = name.Replace(" ", string.Empty);
            return fileNormalized.Contains(normalizedDifficulty, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string? ExtractDifficultyFromFilename(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var openBracket = fileName.LastIndexOf('[');
        var closeBracket = fileName.LastIndexOf(']');
        if (openBracket < 0 || closeBracket <= openBracket)
        {
            return null;
        }

        return fileName.Substring(openBracket + 1, closeBracket - openBracket - 1).Trim();
    }

    private async Task HandleRewindOsr(HttpListenerContext context)
    {
        var replayPath = context.Request.QueryString["path"];
        if (string.IsNullOrWhiteSpace(replayPath))
        {
            await SendJson(context.Response, new { error = "Replay path missing" }, 400);
            return;
        }

        replayPath = WebUtility.UrlDecode(replayPath);
        if (string.IsNullOrWhiteSpace(replayPath) || !File.Exists(replayPath))
        {
            await SendJson(context.Response, new { error = "Replay not found" }, 404);
            return;
        }

        try
        {
            var replay = ReplayDecoder.Decode(replayPath);
            var payload = new
            {
                gameVersion = replay.OsuVersion,
                replayData = BuildReplayData(replay),
                mods = (int)replay.Mods,
                replayMD5 = replay.ReplayMD5Hash,
                beatmapMD5 = replay.BeatmapMD5Hash,
                playerName = replay.PlayerName
            };
            await SendJson(context.Response, payload);
        }
        catch (Exception ex)
        {
            await SendJson(context.Response, new { error = ex.Message }, 500);
        }
    }

    private async Task HandleRewindFile(HttpListenerContext context)
    {
        var filePath = context.Request.QueryString["path"];
        if (string.IsNullOrWhiteSpace(filePath))
        {
            await SendJson(context.Response, new { error = "File path missing" }, 400);
            return;
        }

        var rawPath = filePath;
        filePath = WebUtility.UrlDecode(filePath);
        var noFallback = string.Equals(context.Request.QueryString["nofallback"], "1", StringComparison.OrdinalIgnoreCase);
        // Normalize slashes
        filePath = filePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        
        Console.WriteLine($"[Rewind] Serving: {filePath}");

        // Fix path duplication: Rewind may prepend an absolute path to another absolute path
        // e.g., "G:\...\rewind\Songs/G:\...\rewind\Songs\..." -> extract the last absolute path
        // Find all drive letter occurrences and use the last one if there are multiple
        var driveLetterMatches = System.Text.RegularExpressions.Regex.Matches(filePath, @"[A-Za-z]:[\\\/]");
        if (driveLetterMatches.Count > 1)
        {
            // Use the last drive letter occurrence (the actual file path)
            var lastMatch = driveLetterMatches[driveLetterMatches.Count - 1];
            filePath = filePath.Substring(lastMatch.Index);
        }

        if (File.Exists(filePath))
        {
            try
            {
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                var contentType = GetContentType(ext);
                context.Response.ContentType = contentType;
                context.Response.AddHeader("Access-Control-Allow-Origin", "*");
                
                var bytes = await File.ReadAllBytesAsync(filePath);
                
                // Support Range requests for audio/video seeking
                var rangeHeader = context.Request.Headers["Range"];
                if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                {
                    var rangeSpec = rangeHeader.Substring(6);
                    var rangeParts = rangeSpec.Split('-');
                    long start = 0, end = bytes.Length - 1;
                    
                    if (!string.IsNullOrEmpty(rangeParts[0]))
                        start = long.Parse(rangeParts[0]);
                    if (rangeParts.Length > 1 && !string.IsNullOrEmpty(rangeParts[1]))
                        end = long.Parse(rangeParts[1]);
                    
                    var length = end - start + 1;
                    context.Response.StatusCode = 206;
                    context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{bytes.Length}";
                    context.Response.Headers["Accept-Ranges"] = "bytes";
                    context.Response.ContentLength64 = length;
                    await context.Response.OutputStream.WriteAsync(bytes, (int)start, (int)length);
                }
                else
                {
                    context.Response.Headers["Accept-Ranges"] = "bytes";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                }
                context.Response.Close();
            }
            catch (Exception ex)
            {
                await SendJson(context.Response, new { error = ex.Message }, 500);
            }
        }
        else await SendJson(context.Response, new { error = "File not found" }, 404);
    }

    private async Task ServeStableBackground(HttpListenerContext context, string path)
    {
        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            context.Response.ContentType = GetContentType(ext);
            var buf = await File.ReadAllBytesAsync(path);
            await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length);
            context.Response.Close();
        }
        else await SendJson(context.Response, new { error = "File not found", searched = path }, 404);
    }

    private async Task ServeBackground(HttpListenerContext context, string identifier)
    {
        string path;
        if (identifier.StartsWith("STABLE:"))
        {
            var base64 = identifier.Substring(7);
            var bytes = Convert.FromBase64String(base64);
            path = Encoding.UTF8.GetString(bytes);
        }
        else
        {
            path = Path.Combine(SettingsManager.Current.LazerPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu"), "files", identifier.Substring(0, 1), identifier.Substring(0, 2), identifier);
        }

        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            context.Response.ContentType = GetContentType(ext);
            var buf = await File.ReadAllBytesAsync(path);
            await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length);
            context.Response.Close();
        }
        else await SendJson(context.Response, new { error = "Not found", searched = path }, 404);
    }

    private async Task HandleRewindSkins(HttpListenerContext context)
    {
        var skinsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Skins");
        if (!Directory.Exists(skinsRoot))
        {
            await SendJson(context.Response, new { skins = Array.Empty<string>() });
            return;
        }

        var skins = Directory.GetDirectories(skinsRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => File.Exists(Path.Combine(skinsRoot, name!, "skin.ini")))
            .OrderBy(name => name)
            .ToArray();

        await SendJson(context.Response, new { skins });
    }

    private async Task HandleRewindSkinManifest(HttpListenerContext context)
    {
        var skinName = context.Request.QueryString["skin"];
        if (string.IsNullOrWhiteSpace(skinName))
        {
            await SendJson(context.Response, new { error = "Skin name missing" }, 400);
            return;
        }

        var skinsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Skins");
        var skinPath = Path.Combine(skinsRoot, skinName);
        if (!Directory.Exists(skinPath))
        {
            await SendJson(context.Response, new { error = "Skin not found" }, 404);
            return;
        }

        var files = Directory.GetFiles(skinPath, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(skinPath, f))
            .Select(f => f.Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await SendJson(context.Response, new { skin = skinName, files });
    }

    private async Task HandleRewindSkinFileClean(HttpListenerContext context)
    {
        var prefix = "/api/rewind/skin-files/";
        var requestPath = context.Request.Url?.AbsolutePath ?? string.Empty;
        var relativePath = requestPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) 
            ? requestPath.Substring(prefix.Length) 
            : string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            await SendJson(context.Response, new { error = "Skin path missing" }, 400);
            return;
        }

        var skinsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Skins");
        var decoded = WebUtility.UrlDecode(relativePath)
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        var parts = decoded.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            await SendJson(context.Response, new { error = "File not found" }, 404);
            return;
        }

        var skinName = parts[0];
        var subPath = Path.Combine(parts.Skip(1).ToArray());

        // Fast Path
        var exactPath = Path.Combine(skinsRoot, skinName, subPath);
        if (File.Exists(exactPath))
        {
            if (new FileInfo(exactPath).Length < 500) { try { if (File.ReadLines(exactPath).First().StartsWith("version https://git-lfs")) exactPath = ""; } catch { } }
            if (!string.IsNullOrEmpty(exactPath)) { await ServeLocalFile(context, exactPath); return; }
        }

        // Font Fallback
        if (skinName.Equals("OsuDefaultSkin", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var f in new[] { "score", "hitcircle", "combo", "dots" })
            {
                var p = Path.Combine(skinsRoot, "3cl", "Fonts", f, subPath);
                if (!File.Exists(p) && subPath.Contains("@2x")) p = Path.Combine(skinsRoot, "3cl", "Fonts", f, subPath.Replace("@2x", ""));
                if (File.Exists(p)) { await ServeLocalFile(context, p); return; }
            }
        }

        await SendJson(context.Response, new { error = "File not found" }, 404);
    }

    private async Task ServeLocalFile(HttpListenerContext context, string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        context.Response.ContentType = GetContentType(Path.GetExtension(path).ToLowerInvariant());
        context.Response.AddHeader("Access-Control-Allow-Origin", "*");
        context.Response.Headers["Cache-Control"] = "public, max-age=3600";
        var rangeHeader = context.Request.Headers["Range"];
        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
        {
            var rangeParts = rangeHeader.Substring(6).Split('-');
            long start = long.Parse(rangeParts[0]), end = rangeParts.Length > 1 && !string.IsNullOrEmpty(rangeParts[1]) ? long.Parse(rangeParts[1]) : bytes.Length - 1;
            context.Response.StatusCode = 206;
            context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{bytes.Length}";
            context.Response.Headers["Accept-Ranges"] = "bytes";
            context.Response.ContentLength64 = end - start + 1;
            await context.Response.OutputStream.WriteAsync(bytes, (int)start, (int)(end - start + 1));
        }
        else { context.Response.Headers["Accept-Ranges"] = "bytes"; context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length); }
        context.Response.Close();
    }

    private async Task<object> GetAnalyticsDataAsync()
    {
        var total = await _db.GetTotalStatsAsync();
        var daily = await _db.GetDailyAverageStatsAsync();
        var stats = await _db.GetDailyStatsAsync(30);
        double maxPP = stats.Any() ? stats.Max(d => d.AvgPP) : 1;
        return new { totalPlays = total.totalPlays, totalMinutes = total.totalTimeMs / 60000.0, avgAccuracy = daily.avgDailyAcc, avgPP = daily.avgDailyPP, performanceMatch = maxPP > 0 ? (daily.avgDailyPP / maxPP) * 100.0 : 0 };
    }

    private async Task<Dictionary<string, int>> GetMonthPlayCountsAsync(int year, int month)
    {
        var plays = await _db.FetchPlaysRangeAsync(new DateTime(year, month, 1).ToUniversalTime(), new DateTime(year, month, 1).AddMonths(1).ToUniversalTime());
        return plays.GroupBy(p => p.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd")).ToDictionary(g => g.Key, g => g.Count());
    }

    private async Task ServeStaticFile(HttpListenerContext context, string path)
    {
        if (path == "/") path = "/index.html";
        var local = ResolveStaticPath(path);
        if (!string.IsNullOrEmpty(local) && File.Exists(local))
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            var buf = await File.ReadAllBytesAsync(local);
            context.Response.ContentType = GetContentType(Path.GetExtension(local));
            await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length);
            context.Response.Close();
        }
        else await SendJson(context.Response, new { error = "Not found" }, 404);
    }

    private string? ResolveStaticPath(string path)
    {
        var rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var primary = Path.Combine(_webRoot, rel);
        if (File.Exists(primary)) return primary;
        if (rel.Equals("favicon.ico", StringComparison.OrdinalIgnoreCase)) return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "favicon.ico");
        if (rel.StartsWith("assets" + Path.DirectorySeparatorChar + "mods")) return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Assets", "Mods", rel.Substring(12));
        return primary;
    }

    private async Task HandleWebSocket(HttpListenerContext context)
    {
        var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
        lock (_clientsLock) _liveClients.Add(ws);
        try { while (ws.State == WebSocketState.Open) await ws.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), _cts.Token); } finally { lock (_clientsLock) _liveClients.Remove(ws); }
    }

    public async Task BroadcastLog(string message, string level = "info") => await Broadcast(JsonSerializer.Serialize(new { type = "log", message, level }));
    public async Task BroadcastLiveData(object data) => await Broadcast(JsonSerializer.Serialize(new { type = "live", data }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    private async Task Broadcast(string payload) {
        var bytes = Encoding.UTF8.GetBytes(payload);
        WebSocket[] clients; lock (_clientsLock) clients = _liveClients.Where(c => c.State == WebSocketState.Open).ToArray();
        foreach (var c in clients) try { await c.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); } catch { }
    }

    private int GetQueryInt(HttpListenerContext context, string key, int def) => int.TryParse(context.Request.QueryString[key], out var r) ? r : def;
    private int ExtractIdFromPath(string path, string prefix) { var rest = path.Substring(prefix.Length); var slash = rest.IndexOf('/'); return int.TryParse(slash >= 0 ? rest.Substring(0, slash) : rest, out var id) ? id : 0; }
    private string GetContentType(string ext) => ext switch { ".html" => "text/html", ".css" => "text/css", ".js" => "application/javascript", ".json" => "application/json", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".ogg" => "audio/ogg", ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".osu" => "text/plain", ".ini" => "text/plain", ".osr" => "application/octet-stream", _ => "application/octet-stream" };
    private async Task SendJson(HttpListenerResponse resp, object data, int code = 200) { var buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })); resp.StatusCode = code; resp.ContentType = "application/json"; await resp.OutputStream.WriteAsync(buf, 0, buf.Length); resp.Close(); }
    private static async Task<T?> ReadJsonBodyAsync<T>(HttpListenerRequest request) { using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8); var body = await reader.ReadToEndAsync(); return string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
    private static string BuildReplayData(Replay replay) { var sb = new StringBuilder(); var cur = 0; foreach (var f in replay.ReplayFrames ?? new List<OsuParsers.Replays.Objects.ReplayFrame>()) { var diff = f.TimeDiff == 0 && f.Time > 0 ? f.Time - cur : f.TimeDiff; cur += diff; sb.Append($"{diff}|{f.X.ToString(CultureInfo.InvariantCulture)}|{f.Y.ToString(CultureInfo.InvariantCulture)}|{GetActionMask(f, replay.Ruleset)},"); } return sb.ToString(); }
    private static int GetActionMask(OsuParsers.Replays.Objects.ReplayFrame f, Ruleset r) => r switch { Ruleset.Taiko => (int)f.TaikoKeys, Ruleset.Fruits => (int)f.CatchKeys, Ruleset.Mania => (int)f.ManiaKeys, _ => (int)f.StandardKeys };
    public void Dispose() { Stop(); _cts.Dispose(); }
}
