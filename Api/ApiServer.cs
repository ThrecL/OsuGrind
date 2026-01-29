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
using OsuGrind.Import;
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
        _db = db; _authService = new AuthService(); Port = port;
        _webRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "WebUI");
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        if (port != 7777) {
            _listener.Prefixes.Add("http://127.0.0.1:7777/");
            _listener.Prefixes.Add("http://localhost:7777/");
        }
        RealmExportService.OnLog += async (msg) => await BroadcastLog(msg);
    }

    public void Start() 
    { 
        int retries = 5;
        while (retries > 0)
        {
            try 
            { 
                _listener.Start(); 
                _listenTask = Task.Run(ListenLoop); 
                Console.WriteLine($"[ApiServer] Listening on http://127.0.0.1:{Port}/");
                return;
            } 
            catch (HttpListenerException) 
            { 
                retries--;
                if (retries == 0) throw;
                Thread.Sleep(500); // Wait for port to release
            }
            catch (Exception ex) 
            { 
                File.WriteAllText("api_server_error.txt", ex.ToString()); 
                throw; 
            } 
        }
    }
    public void Stop() { 
        _cts.Cancel(); 
        try { _listener.Stop(); } catch { } 
        try { _listener.Abort(); } catch { } 
        try { _listener.Close(); } catch { } 
        if (_listenTask != null) {
            _listenTask.Wait(TimeSpan.FromMilliseconds(200));
        }
    }

    private async Task ListenLoop() { 
        while (!_cts.IsCancellationRequested) { 
            try { 
                var context = await _listener.GetContextAsync(); 
                _ = Task.Run(() => HandleRequest(context)); 
            } 
            catch (HttpListenerException) when (_cts.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; } 
            catch (Exception ex) { Console.WriteLine($"[ApiServer] Listen error: {ex.Message}"); } 
        } 
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;
        context.Response.AddHeader("Access-Control-Allow-Origin", "*");
        context.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
        context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
        if (method == "OPTIONS") { context.Response.StatusCode = 200; context.Response.Close(); return; }
        try {
            if (path == "/ws/live" && context.Request.IsWebSocketRequest) { await HandleWebSocket(context); return; }
            if (path.StartsWith("/api/") || path == "/callback" || path.StartsWith("/rewind/file")) { await HandleApiRequest(context, path, method); return; }
            await ServeStaticFile(context, path);
        } catch (Exception ex) { await SendJson(context.Response, new { error = ex.Message }, 500); }
    }

    private async Task HandleApiRequest(HttpListenerContext context, string path, string method)
    {
        var response = context.Response;
        switch (path)
        {
            case "/api/rewind/pp": if (method == "POST") await HandleRewindPp(context); break;
            case "/api/rewind/osr": await HandleRewindOsr(context); break;
            case "/api/rewind/skins": await HandleRewindSkinsList(context); break;
            case "/api/rewind/cursor-offsets": if (method == "POST") await HandleCursorOffsets(context); break;
            case "/api/history/recent": await SendJson(response, await _db.FetchRecentAsync(GetQueryInt(context, "limit", 50))); break;
            case "/api/history":
            {
                var dateStr = context.Request.QueryString["date"];
                if (!string.IsNullOrEmpty(dateStr)) {
                    var plays = await _db.FetchPlaysByLocalDayAsync(dateStr);
                    var totalMs = plays.Sum(p => p.DurationMs);
                    await SendJson(response, new { plays, stats = new { plays = plays.Count, avgAccuracy = plays.Count > 0 ? plays.Average(p => p.Accuracy * 100) : 0, avgPP = plays.Count > 0 ? plays.Average(p => p.PP) : 0, duration = totalMs >= 3600000 ? $"{totalMs/3600000}h {(totalMs%3600000)/60000}m" : $"{totalMs/60000}m" } });
                } else await SendJson(response, new { error = "Date missing" }, 400);
                break;
            }
            case "/api/history/month": 
            {
                var counts = await _db.GetMonthPlayCountsAsync(GetQueryInt(context, "year", DateTime.Now.Year), GetQueryInt(context, "month", DateTime.Now.Month));
                await SendJson(response, new { playCounts = counts }); 
                break;
            }
            case "/api/debug/dump": await SendJson(response, await _db.DumpPlaysAsync()); break;
            case "/api/analytics": 
            {
                var daysStr = context.Request.QueryString["days"];
                int days = daysStr == "today" ? -1 : GetQueryInt(context, "days", 30);
                await SendJson(response, await GetAnalyticsDataAsync(days)); 
                break;
            }
            case "/api/goals":
            {
                var progress = await _db.GetTodayGoalProgressAsync(SettingsManager.Current.GoalStars);
                await SendJson(response, new { settings = new { plays = SettingsManager.Current.GoalPlays, hits = SettingsManager.Current.GoalHits, stars = SettingsManager.Current.GoalStars, pp = SettingsManager.Current.GoalPP }, progress = new { plays = progress.Plays, hits = progress.Hits, stars = progress.StarPlays, pp = progress.TotalPP } });
                break;
            }
            case "/api/goals/save":
            {
                if (method == "POST") {
                    var payload = await ReadJsonBodyAsync<Dictionary<string, object>>(context);
                    if (payload != null) {
                        if (payload.TryGetValue("plays", out var gp)) SettingsManager.Current.GoalPlays = TryGetInt(gp);
                        if (payload.TryGetValue("hits", out var gh)) SettingsManager.Current.GoalHits = TryGetInt(gh);
                        if (payload.TryGetValue("stars", out var gs)) SettingsManager.Current.GoalStars = TryGetDouble(gs);
                        if (payload.TryGetValue("pp", out var gpp)) SettingsManager.Current.GoalPP = TryGetInt(gpp);
                        SettingsManager.Save(); await BroadcastRefresh(); await SendJson(response, new { success = true });
                    } else await SendJson(response, new { error = "Invalid payload" }, 400);
                }
                break;
            }
            case "/api/import/lazer": 
            {
                if (method == "POST") { 
                    var (added, skipped, error) = await new LazerImportService(_db).ImportScoresAsync(SettingsManager.Current.LazerPath, SettingsManager.Current.Username); 
                    if (error != null) await SendJson(response, new { success = false, message = error }, 400); 
                    else { 
                        try { await _db.MigrateAsync(); } catch { }
                        TrackerService.TriggerSync();
                        await BroadcastRefresh(); 
                        await SendJson(response, new { success = true, count = added, skipped }); 
                    } 
                } 
                break;
            }
            case "/api/import/stable": 
            {
                if (method == "POST") { 
                    var (added, skipped, error) = await new OsuStableImportService(_db).ImportScoresAsync(SettingsManager.Current.StablePath, SettingsManager.Current.Username, context.Request.QueryString["aliases"]); 
                    if (!string.IsNullOrEmpty(error)) await SendJson(response, new { success = false, message = error }, 400); 
                    else { 
                        try { await _db.MigrateAsync(); } catch { }
                        TrackerService.TriggerSync();
                        await BroadcastRefresh(); 
                        await SendJson(response, new { success = true, count = added, skipped }); 
                    } 
                } 
                break;
            }
            case "/api/settings": if (method == "GET") await SendJson(response, SettingsManager.Current); else if (method == "POST") { var payload = await ReadJsonBodyAsync<Dictionary<string, object>>(context); if (payload != null) { SettingsManager.UpdateFromDictionary(payload); await SendJson(response, new { success = true }); } else await SendJson(response, new { error = "Invalid settings" }, 400); } break;
            case "/api/settings/delete-scores": if (method == "POST") { await _db.DeleteAllScoresAsync(); await BroadcastRefresh(); await SendJson(response, new { success = true }); } break;
            case "/api/settings/delete-beatmaps": if (method == "POST") { await _db.DeleteAllBeatmapsAsync(); await SendJson(response, new { success = true }); } break;
            case "/api/auth/login": await SendJson(response, new { authUrl = _authService.GetAuthUrl() }); break;
            case "/callback":
            {
                var code = context.Request.QueryString["code"];
                if (!string.IsNullOrEmpty(code)) {
                    var exchangedToken = await _authService.ExchangeCodeForTokenAsync(code);
                    if (!string.IsNullOrEmpty(exchangedToken)) {
                        SettingsManager.Current.AccessToken = exchangedToken;
                        SettingsManager.Save();
                        await BroadcastRefresh();
                        response.StatusCode = 200;
                        var successHtml = "<html><body style='background:#111;color:#fff;font-family:sans-serif;display:flex;flex-direction:column;align-items:center;justify-content:center;height:100vh;'><h1>Login Successful!</h1><p>You can now close this window and return to OsuGrind.</p></body></html>";
                        var bytes = Encoding.UTF8.GetBytes(successHtml);
                        response.ContentType = "text/html";
                        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        response.Close();
                    } else await SendJson(response, new { error = "Failed to exchange code" }, 400);
                } else await SendJson(response, new { error = "Code missing" }, 400);
                break;
            }
            case "/api/profile":
            {
                string? profileToken = SettingsManager.Current.AccessToken;
                if (!string.IsNullOrEmpty(profileToken)) {
                    var profile = await _authService.GetUserProfileAsync(profileToken);
                    if (profile != null) { 
                        var p = profile.Value; 
                        double pp = 0; double acc = 0; int gr = 0; int cr = 0; int playCount = 0; double maxCombo = 0; string coverUrl = "";
                        
                        if (p.TryGetProperty("statistics", out var s)) { 
                            if (s.TryGetProperty("pp", out var ppProp)) pp = ppProp.GetDouble(); 
                            if (s.TryGetProperty("hit_accuracy", out var accProp)) acc = accProp.GetDouble(); 
                            if (s.TryGetProperty("global_rank", out var grProp) && grProp.ValueKind != JsonValueKind.Null) gr = grProp.GetInt32(); 
                            if (s.TryGetProperty("country_rank", out var crProp) && crProp.ValueKind != JsonValueKind.Null) cr = crProp.GetInt32();
                            if (s.TryGetProperty("play_count", out var pcProp)) playCount = pcProp.GetInt32();
                            if (s.TryGetProperty("maximum_combo", out var mcProp)) maxCombo = mcProp.GetInt32();
                        }
                        if (p.TryGetProperty("cover_url", out var coverProp)) coverUrl = coverProp.GetString() ?? "";
                        else if (p.TryGetProperty("cover", out var coverObj) && coverObj.TryGetProperty("url", out var urlProp)) coverUrl = urlProp.GetString() ?? "";
                        
                        // Persist username to settings for tracking
                        var username = p.GetProperty("username").GetString();
                        if (SettingsManager.Current.Username != username) {
                            SettingsManager.Current.Username = username;
                            SettingsManager.Save();
                        }

                        await SendJson(response, new { 
                            isLoggedIn = true, 
                            username = p.GetProperty("username").GetString(), 
                            avatarUrl = p.GetProperty("avatar_url").GetString(), 
                            globalRank = gr, 
                            countryRank = cr, 
                            pp, 
                            accuracy = acc,
                            playCount,
                            maxCombo,
                            coverUrl
                        }); 
                        return;
                    }
                }
                await SendJson(response, new { isLoggedIn = false });
                break;
            }
            case "/api/profile/top":
            {
                string? topToken = SettingsManager.Current.AccessToken;
                if (!string.IsNullOrEmpty(topToken)) {
                    var profile = await _authService.GetUserProfileAsync(topToken);
                    if (profile != null && profile.Value.TryGetProperty("id", out var idP)) {
                        var topScores = await _authService.GetUserTopScoresAsync(topToken, idP.GetInt32());
                        if (topScores != null) { await SendJson(response, topScores); return; }
                    }
                }
                await SendJson(response, await _db.GetTopPlaysAsync(100));
                break;
            }
            case "/api/auth/logout":
                if (method == "POST") {
                    SettingsManager.Current.AccessToken = null;
                    SettingsManager.Save();
                    await BroadcastRefresh();
                    await SendJson(response, new { success = true });
                }
                break;
            case "/api/update": await SendJson(response, await UpdateService.CheckForUpdatesAsync()); break;
            case "/api/update/install": if (method == "POST") { var payload = await ReadJsonBodyAsync<JsonElement>(context); if (payload.TryGetProperty("zipUrl", out var zipProp)) { string? url = zipProp.GetString(); if (!string.IsNullOrEmpty(url)) await SendJson(response, new { success = await UpdateService.InstallUpdateAsync(url) }); } } break;
            case "/rewind/file": await HandleRewindFile(context); break;
            default:
                if (path.StartsWith("/api/play/")) {
                    var id = ExtractIdFromPath(path, "/api/play/");
                    if (method == "DELETE") { await _db.DeletePlayAsync(id); await BroadcastRefresh(); await SendJson(response, new { success = true }); }
                    else if (path.EndsWith("/rewind")) await HandleRewindRequest(context, id);
                } else if (path.StartsWith("/api/rewind/skin-manifest")) await HandleRewindSkinManifest(context);
                else if (path.StartsWith("/api/rewind/skin-files/")) await HandleRewindSkinFileClean(context);
                else if (path.StartsWith("/api/background/")) {
                    var identifier = path.Replace("/api/background/", "");
                    if (identifier == "stable") {
                        var b64 = context.Request.QueryString["path"];
                        if (!string.IsNullOrEmpty(b64)) await ServeStableBackground(context, Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
                        else await SendJson(response, new { error = "Path missing" }, 400);
                    } else await ServeBackground(context, identifier);
                }
                else await SendJson(response, new { error = "Not found" }, 404);
                break;
        }
    }

    private async Task HandleRewindPp(HttpListenerContext context)
    {
        var payload = await ReadJsonBodyAsync<Models.RewindPpRequest>(context);
        if (payload == null) { await SendJson(context.Response, new { error = "Invalid payload" }, 400); return; }
        try {
            using var rosu = new RosuService(); rosu.UpdateContext(payload.BeatmapPath ?? "");
            uint mods = RosuService.ModsToRosuStats(payload.Mods);
            int passed = payload.PassedObjects > 0 ? payload.PassedObjects : (payload.Count300 + payload.Count100 + payload.Count50 + payload.Misses);
            double pp = rosu.CalculatePp(mods, payload.Combo, payload.Count300, payload.Count100, payload.Count50, payload.Misses, passed, clockRate: RosuService.GetClockRateFromMods(mods));
            await SendJson(context.Response, new { pp });
        } catch { await SendJson(context.Response, new { pp = 0 }); }
    }

    private async Task HandleRewindRequest(HttpListenerContext context, int id)
    {
        try {
            var play = await _db.GetPlayAsync(id); if (play == null) { await SendJson(context.Response, new { error = "Play not found" }, 404); return; }
            string? replayPath = play.ReplayFile; 
            if (string.IsNullOrEmpty(replayPath) || !File.Exists(replayPath)) if (!string.IsNullOrEmpty(play.ReplayHash)) replayPath = ExtractReplayFromLazer(play.ReplayHash, play.CreatedAtUtc.Ticks);
            if (string.IsNullOrEmpty(replayPath)) replayPath = await new RealmExportService().SearchReplayByTimeAsync(play.CreatedAtUtc, TimeSpan.FromMinutes(5), play.BeatmapHash);
            if (string.IsNullOrEmpty(replayPath) || !File.Exists(replayPath)) {
                string sPath = SettingsManager.Current.StablePath ?? OsuStableImportService.AutoDetectStablePath() ?? "";
                if (Directory.Exists(sPath)) {
                    var rDir = Path.Combine(sPath, "Data", "r");
                    if (Directory.Exists(rDir)) {
                        var f = new DirectoryInfo(rDir).GetFiles("*.osr").Where(x => x.Name.StartsWith(play.BeatmapHash ?? "None", StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.LastWriteTime).FirstOrDefault();
                        if (f != null && (DateTime.UtcNow - f.LastWriteTimeUtc).TotalHours < 24) replayPath = f.FullName;
                    }
                }
            }
            if (string.IsNullOrEmpty(replayPath) || !File.Exists(replayPath)) { await SendJson(context.Response, new { error = "Replay not found" }, 404); return; }
            var localReplaysDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Replays");
            Directory.CreateDirectory(localReplaysDir);
            var safeName = $"Replay_{play.Id}_{play.CreatedAtUtc.Ticks}.osr";
            var finalPath = Path.Combine(localReplaysDir, safeName);
            try { if (!File.Exists(finalPath)) File.Copy(replayPath, finalPath, true); replayPath = finalPath; if (play.ReplayFile != finalPath) await _db.UpdatePlayReplayFileAsync(id, finalPath); } catch {}

            string? beatmapFolder = null; string? osuFileName = null;
            if (!string.IsNullOrEmpty(play.BeatmapHash)) {
                var exportService = new RealmExportService(); var osuPath = await exportService.ExportBeatmapAsync(play.BeatmapHash);
                if (!string.IsNullOrEmpty(osuPath)) { if (Directory.Exists(osuPath)) { beatmapFolder = osuPath; var osuFiles = Directory.GetFiles(osuPath, "*.osu"); if (osuFiles.Length > 0) osuFileName = Path.GetFileName(osuFiles[0]); } else { beatmapFolder = Path.GetDirectoryName(osuPath); osuFileName = Path.GetFileName(osuPath); } }
                else if (!string.IsNullOrEmpty(play.MapPath) && File.Exists(play.MapPath)) {
                    try {
                        var sFolder = Path.GetDirectoryName(play.MapPath);
                        if (!string.IsNullOrEmpty(sFolder) && Directory.Exists(sFolder)) {
                            var artist = !string.IsNullOrEmpty(play.Artist) ? play.Artist : play.BeatmapArtist; var title = !string.IsNullOrEmpty(play.Title) ? play.Title : play.BeatmapTitle;
                            var fName = string.Join("_", $"{artist} - {title} ({play.BeatmapHash.Substring(0, 8)})".Split(Path.GetInvalidFileNameChars()));
                            var dFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Songs", fName);
                            if (!Directory.Exists(dFolder)) { Directory.CreateDirectory(dFolder); foreach (var file in Directory.GetFiles(sFolder)) File.Copy(file, Path.Combine(dFolder, Path.GetFileName(file)), true); }
                            beatmapFolder = dFolder; osuFileName = Path.GetFileName(play.MapPath);
                        }
                    } catch { }
                }
            }
            if (!string.IsNullOrEmpty(beatmapFolder) && Directory.Exists(beatmapFolder)) {
                var osuFiles = Directory.GetFiles(beatmapFolder, "*.osu").Select(Path.GetFileName).ToList();
                if (osuFiles.Count > 0) osuFileName = FindOsuFileForDifficulty(osuFiles, play.Difficulty) ?? osuFiles.FirstOrDefault();
            }
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            await SendJson(context.Response, new { replayPath = NormalizePath(finalPath), songsRoot = NormalizePath(Path.Combine(baseDir, "Resources", "rewind", "Songs")), skinsRoot = NormalizePath(Path.Combine(baseDir, "Resources", "rewind", "Skins")), beatmapFolder = NormalizePath(beatmapFolder), osuFileName, mods = play.Mods ?? "", beatmapHash = play.BeatmapHash ?? "", replaysRoot = NormalizePath(localReplaysDir), statsTimeline = await _db.GetPpTimelineAsync(play.Id), isImported = (play.Notes != null && play.Notes.Contains("Imported")) || !string.IsNullOrEmpty(play.ReplayHash) });
        } catch (Exception ex) { await SendJson(context.Response, new { error = ex.Message }, 500); }
    }

    private async Task HandleRewindFile(HttpListenerContext context) {
        var fP = Uri.UnescapeDataString(context.Request.QueryString["path"] ?? "").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (fP.EndsWith(Path.DirectorySeparatorChar)) fP = fP.TrimEnd(Path.DirectorySeparatorChar);
        var dL = System.Text.RegularExpressions.Regex.Matches(fP, @"[A-Za-z]:[\\\/]");
        if (dL.Count > 1) fP = fP.Substring(dL[dL.Count - 1].Index);
        if (File.Exists(fP)) await ServeLocalFile(context, fP);
        else if (Directory.Exists(fP)) { context.Response.StatusCode = 200; context.Response.Close(); }
        else await SendJson(context.Response, new { error = "Not found", path = fP }, 404);
    }

    private async Task HandleRewindSkinsList(HttpListenerContext context) {
        var skinsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Skins");
        if (Directory.Exists(skinsRoot)) {
            var dirs = Directory.GetDirectories(skinsRoot).Select(d => Path.GetFileName(d)).ToArray();
            await SendJson(context.Response, dirs);
        } else await SendJson(context.Response, Array.Empty<string>());
    }

    private async Task HandleRewindSkinManifest(HttpListenerContext context) {
        var skinName = context.Request.QueryString["skin"] ?? "-Fun3cL";
        var roots = new[] {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Skins"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "WebUI", "rewind", "Skins")
        };

        foreach (var root in roots) {
            var sFull = Path.Combine(root, skinName);
            if (!Directory.Exists(sFull) && skinName != "-Fun3cL") sFull = Path.Combine(root, "-Fun3cL");
            
            if (Directory.Exists(sFull)) {
                var files = Directory.GetFiles(sFull, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(sFull, f).Replace('\\', '/').ToLowerInvariant())
                    .ToArray();
                await SendJson(context.Response, new { skin = Path.GetFileName(sFull), files });
                return;
            }
        }
        await SendJson(context.Response, new { error = "Skin not found" }, 404);
    }

    private async Task HandleRewindSkinFileClean(HttpListenerContext context) {
        var rel = WebUtility.UrlDecode(context.Request.Url?.AbsolutePath.Replace("/api/rewind/skin-files/", "") ?? "").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        
        var skinsRoots = new[] {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Skins"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "WebUI", "rewind", "Skins")
        };

        foreach (var root in skinsRoots) {
            if (!Directory.Exists(root)) continue;
            
            // Try Case-Insensitive Search
            string? foundPath = FindFileCaseInsensitive(root, rel);
            if (foundPath != null) { await ServeLocalFile(context, foundPath); return; }

            // @2x Fallback
            if (rel.Contains("@2x")) {
                var sdRel = rel.Replace("@2x", "");
                foundPath = FindFileCaseInsensitive(root, sdRel);
                if (foundPath != null) { await ServeLocalFile(context, foundPath); return; }
            }

            // -Fun3cL Fallback
            var parts = rel.Split(Path.DirectorySeparatorChar, 2);
            if (parts.Length > 1 && !parts[0].Equals("-Fun3cL", StringComparison.OrdinalIgnoreCase)) {
                var fallbackRel = Path.Combine("-Fun3cL", parts[1]);
                foundPath = FindFileCaseInsensitive(root, fallbackRel);
                if (foundPath != null) { await ServeLocalFile(context, foundPath); return; }
                
                if (parts[1].Contains("@2x")) {
                    var sdFallbackRel = Path.Combine("-Fun3cL", parts[1].Replace("@2x", ""));
                    foundPath = FindFileCaseInsensitive(root, sdFallbackRel);
                    if (foundPath != null) { await ServeLocalFile(context, foundPath); return; }
                }
            }
        }

        await SendJson(context.Response, new { error = "Not found", path = rel }, 404);
    }

    private string? FindFileCaseInsensitive(string root, string relPath)
    {
        var fullPath = Path.Combine(root, relPath);
        if (File.Exists(fullPath)) return fullPath;

        // Manual case-insensitive walk
        var current = root;
        var parts = relPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var part in parts)
        {
            if (!Directory.Exists(current)) return null;
            var match = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(e => Path.GetFileName(e).Equals(part, StringComparison.OrdinalIgnoreCase));
            
            if (match == null) return null;
            current = match;
        }
        
        return File.Exists(current) ? current : null;
    }

    private async Task<object> GetAnalyticsDataAsync(int days) {
        var summary = await _db.GetAnalyticsSummaryAsync(days); // Stats for the selected period
        var allTimeSummary = await _db.GetAnalyticsSummaryAsync(0); // For all-time Peak Perf
        var daily = (days == -1) ? await _db.GetHourlyStatsTodayAsync() : await _db.GetDailyStatsAsync(days);
        var playsToday = await _db.GetPlaysTodayCountAsync(); 
        var streak = await _db.GetPlayStreakAsync();
        
        // Improved Peak Performance Reference Baselines
        var allDaily = await _db.GetDailyStatsAsync(0); 
        double referencePP = 1;
        double referenceAcc = 0.95;
        double targetUR = 80.0;

        if (allDaily.Count > 0)
        {
            var topDays = allDaily.OrderByDescending(d => d.AvgPP).Take(5).ToList();
            referencePP = topDays.Average(d => d.AvgPP);
            referenceAcc = topDays.Average(d => d.AvgAcc);
            targetUR = allDaily.Where(d => d.AvgUR > 40).OrderBy(d => d.AvgUR).Take(5).DefaultIfEmpty(new DailyStats { AvgUR = 100 }).Average(d => d.AvgUR);
        }
        if (referencePP <= 0) referencePP = 1;
        
        var recentSummary = await _db.GetAnalyticsSummaryAsync(14); 
        double currentRatio = referencePP > 0 ? recentSummary.AvgPP / referencePP : 0;
        string form = "Stable";
        if (recentSummary.TotalPlays > 0) 
        { 
            if (currentRatio > 1.05) form = "Peak"; 
            else if (currentRatio > 0.96) form = "Great"; 
            else if (currentRatio > 0.88) form = "Stable"; 
            else if (currentRatio > 0.75) form = "Slumping"; 
            else form = "Burnout"; 
        }

        var mentalitySummary = await _db.GetAnalyticsSummaryAsync(3);
        double mentality = 75; 
        if (mentalitySummary.TotalPlays > 0) 
        {
            double passRate = (double)mentalitySummary.PassCount / mentalitySummary.TotalPlays;
            double resilienceScore = passRate * 100;
            double avgDuration = (double)mentalitySummary.TotalDurationMs / mentalitySummary.TotalPlays;
            double focusScore = Math.Clamp((avgDuration / 180000.0) * 100, 0, 100);
            double currentPerfMatch = mentalitySummary.AvgPP / referencePP;
            double consistencyScore = Math.Clamp(currentPerfMatch * 100, 0, 100);
            double baseScore = (resilienceScore * 0.2) + (focusScore * 0.4) + (consistencyScore * 0.4);
            double urPenalty = 1.0;
            if (mentalitySummary.AvgUR > 120) urPenalty = 0.8;
            else if (mentalitySummary.AvgUR > 90) urPenalty = 0.9;
            double multiplier = 1.0;
            if (mentalitySummary.LastPlayedUtc.HasValue) 
            { 
                var lastPlayed = mentalitySummary.LastPlayedUtc.Value.ToLocalTime(); 
                var inactivityDays = (DateTime.Now - lastPlayed).TotalDays; 
                multiplier = Math.Pow(0.92, Math.Max(0, inactivityDays - 0.5)); 
            }
            double hoursPlayedRecent = mentalitySummary.TotalDurationMs / 3600000.0;
            double fatiguePenalty = 1.0;
            if (hoursPlayedRecent > 3.0) fatiguePenalty = 0.85;
            if (hoursPlayedRecent > 6.0) fatiguePenalty = 0.60;
            var goals = await _db.GetTodayGoalProgressAsync(SettingsManager.Current.GoalStars);
            double goalMod = 1.0;
            bool hasAnyGoals = SettingsManager.Current.GoalPlays > 0 || SettingsManager.Current.GoalHits > 0 || SettingsManager.Current.GoalPP > 0;
            if (hasAnyGoals) 
            { 
                double hour = DateTime.Now.Hour; 
                if (hour > 12) 
                { 
                    double targetProgress = Math.Clamp((hour - 12) / 10.0, 0, 1.0); 
                    double actualProgress = 0; 
                    int goalCount = 0; 
                    if (SettingsManager.Current.GoalPlays > 0) { actualProgress += Math.Min(1.0, (double)goals.Plays / SettingsManager.Current.GoalPlays); goalCount++; } 
                    if (SettingsManager.Current.GoalHits > 0) { actualProgress += Math.Min(1.0, (double)goals.Hits / SettingsManager.Current.GoalHits); goalCount++; } 
                    if (SettingsManager.Current.GoalPP > 0) { actualProgress += Math.Min(1.0, goals.TotalPP / SettingsManager.Current.GoalPP); goalCount++; } 
                    double avgProgress = actualProgress / goalCount; 
                    if (avgProgress > targetProgress) goalMod = 1.05;
                    else if (avgProgress < targetProgress * 0.4) goalMod = 0.7; 
                } 
            }
            mentality = baseScore * multiplier * fatiguePenalty * goalMod * urPenalty;
        }

        // Calculate All-Time Composite Performance Match for the main card
        double allTimePPFactor = referencePP > 0 ? (allTimeSummary.AvgPP / referencePP) : 0;
        double allTimeAccFactor = referenceAcc > 0 ? (allTimeSummary.AvgAccuracy / referenceAcc) : 0;
        double allTimeURFactor = allTimeSummary.AvgUR > 0 ? (targetUR / allTimeSummary.AvgUR) : 0;
        double allTimePerfMatch = (allTimePPFactor * 0.6) + (allTimeAccFactor * 0.3) + (Math.Min(1.2, allTimeURFactor) * 0.1);

        return new { 
            totalPlays = summary.TotalPlays, 
            totalMinutes = summary.TotalDurationMs / 60000.0, 
            avgAccuracy = summary.AvgAccuracy, 
            avgPP = summary.AvgPP, 
            avgUR = summary.AvgUR, 
            avgKeyRatio = summary.AvgKeyRatio, 
            playsToday, 
            streak, 
            perfMatch = Math.Round(allTimePerfMatch * 100.0, 1), 
            currentForm = form, 
            mentality = Math.Clamp(mentality, 0, 100), 
            dailyActivity = daily.Select(d => new { date = d.Date, plays = d.PlayCount, minutes = d.TotalDurationMs / 60000.0, avgPP = d.AvgPP, avgAcc = d.AvgAcc * 100.0, avgUR = d.AvgUR, avgKeyRatio = d.AvgKeyRatio }), 
            hitErrors = await GetRecentHitErrorsAsync(days) 
        };
    }

    private async Task<List<double>> GetRecentHitErrorsAsync(int days)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsuGrind", "osugrind.sqlite")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        string filter = "";
        if (days == -1) filter = "WHERE date(created_at_utc, 'localtime') = date('now', 'localtime') AND hit_errors IS NOT NULL";
        else if (days > 0) filter = "WHERE created_at_utc >= datetime('now', $offset) AND hit_errors IS NOT NULL";
        else filter = "WHERE hit_errors IS NOT NULL";

        cmd.CommandText = $"SELECT hit_errors FROM plays {filter} ORDER BY created_at_utc DESC LIMIT 10000";
        if (days > 0) cmd.Parameters.AddWithValue("$offset", $"-{days} days");
        
        var errors = new List<double>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) try { var list = JsonSerializer.Deserialize<List<double>>(reader.GetString(0)); if (list != null) errors.AddRange(list); } catch {}
        return errors;
    }

    private async Task<Dictionary<string, int>> GetMonthPlayCountsAsync(int y, int m) 
    { 
        return await _db.GetMonthPlayCountsAsync(y, m); 
    }

    private async Task ServeStaticFile(HttpListenerContext context, string path) 
    { 
        if (path == "/") path = "/index.html"; 
        var rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var local = Path.Combine(_webRoot, rel);
        if (rel.StartsWith("Resources" + Path.DirectorySeparatorChar + "rewind")) local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rel);
        if (File.Exists(local)) { var buf = await File.ReadAllBytesAsync(local); context.Response.ContentType = GetContentType(Path.GetExtension(local)); await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length); context.Response.Close(); }
        else await SendJson(context.Response, new { error = "Not found" }, 404);
    }

    private async Task ServeLocalFile(HttpListenerContext context, string path) {
        if (!File.Exists(path)) { await SendJson(context.Response, new { error = "Not found" }, 404); return; }
        
        var fileInfo = new FileInfo(path);
        long length = fileInfo.Length;
        var response = context.Response;
        response.ContentType = GetContentType(Path.GetExtension(path).ToLowerInvariant());
        response.AddHeader("Accept-Ranges", "bytes");

        var rangeHeader = context.Request.Headers["Range"];
        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=")) {
            try {
                var range = rangeHeader.Substring(6).Split('-');
                long start = long.Parse(range[0]);
                long end = range.Length > 1 && !string.IsNullOrEmpty(range[1]) ? long.Parse(range[1]) : length - 1;
                
                if (start >= length || end >= length || start > end) {
                    response.StatusCode = 416; // Range Not Satisfiable
                    response.Close();
                    return;
                }

                long chunkLength = end - start + 1;
                response.StatusCode = 206; // Partial Content
                response.AddHeader("Content-Range", $"bytes {start}-{end}/{length}");
                response.ContentLength64 = chunkLength;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(start, SeekOrigin.Begin);
                
                byte[] buffer = new byte[8192];
                long remaining = chunkLength;
                while (remaining > 0) {
                    int read = await fs.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) break;
                    await response.OutputStream.WriteAsync(buffer, 0, read);
                    remaining -= read;
                }
                response.Close();
                return;
            } catch {
                response.StatusCode = 400;
                response.Close();
                return;
            }
        }

        response.ContentLength64 = length;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
            await fs.CopyToAsync(response.OutputStream);
        }
        response.Close();
    }
    private async Task ServeStableBackground(HttpListenerContext context, string p) { if (File.Exists(p)) { context.Response.ContentType = GetContentType(Path.GetExtension(p).ToLowerInvariant()); var buf = await File.ReadAllBytesAsync(p); await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length); context.Response.Close(); } else await SendJson(context.Response, new { error = "File not found" }, 404); }
    private async Task ServeBackground(HttpListenerContext context, string id) { var lP = SettingsManager.Current.LazerPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu"); var p = Path.Combine(lP, "files", id.Substring(0, 1), id.Substring(0, 2), id); if (File.Exists(p)) { context.Response.ContentType = GetContentType(Path.GetExtension(p).ToLowerInvariant()); var buf = await File.ReadAllBytesAsync(p); await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length); context.Response.Close(); } else await SendJson(context.Response, new { error = "Not found" }, 404); }

    private string? ExtractReplayFromLazer(string h, long t) { try { var lP = SettingsManager.Current.LazerPath ?? LazerImportService.AutoDetectLazerPath(); var sP = Path.Combine(lP ?? "", "files", h.Substring(0, 1), h.Substring(0, 2), h); if (File.Exists(sP)) { var rD = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Replays"); Directory.CreateDirectory(rD); string dP = Path.Combine(rD, $"{t}_{h}.osr"); if (!File.Exists(dP)) File.Copy(sP, dP, true); return dP; } } catch { } return null; }
    private async Task HandleWebSocket(HttpListenerContext context) { var ws = (await context.AcceptWebSocketAsync(null)).WebSocket; lock (_clientsLock) _liveClients.Add(ws); try { while (ws.State == WebSocketState.Open) await ws.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), _cts.Token); } finally { lock (_clientsLock) _liveClients.Remove(ws); } }
    public async Task BroadcastRefresh() => await Broadcast(JsonSerializer.Serialize(new { type = "refresh" }));
    public async Task BroadcastLiveData(object data) => await Broadcast(JsonSerializer.Serialize(new { type = "live", data }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    public async Task BroadcastLog(string message, string level = "info") => await Broadcast(JsonSerializer.Serialize(new { type = "log", message, level }));
    private async Task Broadcast(string payload) { var bytes = Encoding.UTF8.GetBytes(payload); WebSocket[] clients; lock (_clientsLock) clients = _liveClients.Where(c => c.State == WebSocketState.Open).ToArray(); foreach (var c in clients) try { await c.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); } catch { } }
    private int GetQueryInt(HttpListenerContext context, string key, int def) => int.TryParse(context.Request.QueryString[key], out var r) ? r : def;
    private int ExtractIdFromPath(string path, string prefix) { var rest = path.Substring(prefix.Length); var slash = rest.IndexOf('/'); return int.TryParse(slash >= 0 ? rest.Substring(0, slash) : rest, out var id) ? id : 0; }
    private string GetContentType(string ext) => ext switch { ".html" => "text/html", ".css" => "text/css", ".js" => "application/javascript", ".json" => "application/json", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".ogg" => "audio/ogg", ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".osu" => "text/plain", ".osr" => "application/octet-stream", _ => "application/octet-stream" };
    private async Task SendJson(HttpListenerResponse resp, object data, int code = 200) { var buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })); resp.StatusCode = code; resp.ContentType = "application/json"; await resp.OutputStream.WriteAsync(buf, 0, buf.Length); resp.Close(); }
    private async Task<T?> ReadJsonBodyAsync<T>(HttpListenerContext context) { using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8); var body = await reader.ReadToEndAsync(); return string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
    private async Task<Dictionary<string, object>?> ReadJsonBodyAsync(HttpListenerContext context) { using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8); var body = await reader.ReadToEndAsync(); return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(body); }
    private static int TryGetInt(object? val) { if (val == null) return 0; if (val is int i) return i; if (val is long l) return (int)l; if (val is JsonElement je) return je.TryGetInt32(out int result) ? result : 0; return int.TryParse(val?.ToString() ?? "0", out int r) ? r : 0; }
    private static double TryGetDouble(object? val) { if (val == null) return 0; if (val is double d) return d; if (val is float f) return f; if (val is long l) return (double)l; if (val is int i) return (double)i; if (val is JsonElement je) return je.TryGetDouble(out double result) ? result : 0; return double.TryParse(val?.ToString() ?? "0", NumberStyles.Any, CultureInfo.InvariantCulture, out double r) ? r : 0; }
    private static string? FindOsuFileForDifficulty(IEnumerable<string?> files, string? difficulty) { if (string.IsNullOrEmpty(difficulty)) return files.FirstOrDefault(); var target = $"[{difficulty.Trim()}]"; var match = files.FirstOrDefault(n => n?.Contains(target, StringComparison.OrdinalIgnoreCase) == true); if (match != null) return match; var norm = difficulty.Replace(" ", "").Trim(); return files.FirstOrDefault(n => n?.Replace(" ", "").Contains(norm, StringComparison.OrdinalIgnoreCase) == true); }
    private async Task HandleCursorOffsets(HttpListenerContext context) { var payload = await ReadJsonBodyAsync<Dictionary<string, object>>(context); if (payload != null && payload.TryGetValue("scoreId", out var sId) && payload.TryGetValue("offsets", out var off)) { await _db.UpdateCursorOffsetsAsync(long.Parse(sId!.ToString()!), JsonSerializer.Serialize(off)); await SendJson(context.Response, new { success = true }); } else await SendJson(context.Response, new { error = "Invalid payload" }, 400); }
    private async Task HandleRewindOsr(HttpListenerContext context) { var rP = Uri.UnescapeDataString(context.Request.QueryString["path"] ?? "").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar); if (File.Exists(rP)) { try { var replay = ReplayDecoder.Decode(rP); await SendJson(context.Response, new { gameVersion = replay.OsuVersion, replayData = BuildReplayData(replay), mods = (int)replay.Mods, replayMD5 = replay.ReplayMD5Hash, beatmapMD5 = replay.BeatmapMD5Hash, playerName = replay.PlayerName }); } catch (Exception ex) { await SendJson(context.Response, new { error = ex.Message }, 500); } } else await SendJson(context.Response, new { error = "Replay not found" }, 404); }
    private static string BuildReplayData(Replay replay) { var sb = new StringBuilder(); var cur = 0; foreach (var f in replay.ReplayFrames ?? new List<OsuParsers.Replays.Objects.ReplayFrame>()) { var diff = f.TimeDiff == 0 && f.Time > 0 ? f.Time - cur : f.TimeDiff; cur += diff; sb.Append($"{diff}|{f.X.ToString(CultureInfo.InvariantCulture)}|{f.Y.ToString(CultureInfo.InvariantCulture)}|{GetActionMask(f, replay.Ruleset)},"); } return sb.ToString(); }
    private static int GetActionMask(OsuParsers.Replays.Objects.ReplayFrame f, Ruleset r) => r switch { Ruleset.Taiko => (int)f.TaikoKeys, Ruleset.Fruits => (int)f.CatchKeys, Ruleset.Mania => (int)f.ManiaKeys, _ => (int)f.StandardKeys };
    private string NormalizePath(string? path) { if (string.IsNullOrEmpty(path)) return ""; return path.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/'); }
    public void Dispose() { Stop(); _cts.Dispose(); }
}
