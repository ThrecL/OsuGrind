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
            if (path.StartsWith("/api/") || path == "/callback" || path.StartsWith("/rewind/file")) { await HandleApiRequest(context, path, method); return; }
            await ServeStaticFile(context, path);
        }
        catch (Exception ex) { await SendJson(context.Response, new { error = ex.Message }, 500); }
    }

    private async Task HandleRewindPp(HttpListenerContext context)
    {
        var payload = await ReadJsonBodyAsync<Models.RewindPpRequest>(context.Request);
        if (payload == null) { await SendJson(context.Response, new { error = "Invalid payload" }, 400); return; }

        if (payload.ScoreId > 0)
        {
            var timeline = await _db.GetPpTimelineAsync(payload.ScoreId);
            if (timeline != null && timeline.Count > 0)
            {
                int passed = payload.PassedObjects > 0 ? payload.PassedObjects : (payload.Count300 + payload.Count100 + payload.Count50 + payload.Misses);
                int index = Math.Clamp(passed, 0, timeline.Count - 1);
                var stats = timeline[index];

                if (stats.Length >= 7)
                {
                    await SendJson(context.Response, new { pp = stats[0], combo = stats[1], acc = stats[2], h300 = stats[3], h100 = stats[4], h50 = stats[5], miss = stats[6], source = "live_record" });
                }
                else await SendJson(context.Response, new { pp = stats[0], source = "live_record" });
                return;
            }
        }

        string? mapPath = payload.BeatmapPath;
        if (string.IsNullOrEmpty(mapPath) && !string.IsNullOrEmpty(payload.BeatmapHash))
        {
            mapPath = RosuService.GetBeatmapPath(payload.BeatmapHash, SettingsManager.Current.LazerPath);
            if (string.IsNullOrEmpty(mapPath) || !File.Exists(mapPath))
            {
                var songsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Songs");
                if (Directory.Exists(songsRoot))
                {
                    var shortHash = payload.BeatmapHash.Substring(0, 8);
                    var dir = Directory.GetDirectories(songsRoot, $"*({shortHash})*").FirstOrDefault();
                    if (dir != null) mapPath = Directory.GetFiles(dir, "*.osu").FirstOrDefault();
                }
            }
            if (string.IsNullOrEmpty(mapPath) || !File.Exists(mapPath))
            {
                var exportedDir = await new RealmExportService().ExportBeatmapAsync(payload.BeatmapHash);
                if (!string.IsNullOrEmpty(exportedDir) && Directory.Exists(exportedDir)) mapPath = Directory.GetFiles(exportedDir, "*.osu").FirstOrDefault();
            }
        }

        if (string.IsNullOrEmpty(mapPath) || !File.Exists(mapPath)) { await SendJson(context.Response, new { pp = 0, error = "Map not found" }); return; }

        try {
            using var rosu = new RosuService();
            rosu.UpdateContext(mapPath);
            uint modsBits = RosuService.ModsToRosuStats(payload.Mods);
            int passed = payload.PassedObjects > 0 ? payload.PassedObjects : (payload.Count300 + payload.Count100 + payload.Count50 + payload.Misses);
            double clockRate = RosuService.GetClockRateFromMods(modsBits);
            double stars = rosu.GetStars(modsBits, passed, clockRate);
            double pp = rosu.CalculatePp(modsBits, payload.Combo, payload.Count300, payload.Count100, payload.Count50, payload.Misses, passed, sliderEndHits: payload.SliderEndHits, smallTickHits: payload.SmallTickHits, largeTickHits: payload.LargeTickHits, clockRate: clockRate);
            await SendJson(context.Response, new { pp, stars });
        } catch (Exception ex) { await SendJson(context.Response, new { pp = 0, error = ex.Message }); }
    }

    private async Task HandleApiRequest(HttpListenerContext context, string path, string method)
    {
        var response = context.Response;
        switch (path)
        {
            case "/api/rewind/pp": if (method == "POST") await HandleRewindPp(context); break;
            case "/api/rewind/cursor-offsets": if (method == "POST") await HandleCursorOffsets(context); break;
            case "/api/history/recent": await SendJson(response, await _db.FetchRecentAsync(GetQueryInt(context, "limit", 50))); break;
            case "/api/history":
                var dateStr = context.Request.QueryString["date"];
                if (DateTime.TryParse(dateStr, out var date))
                {
                    var plays = await _db.FetchPlaysRangeAsync(date.Date.ToUniversalTime(), date.Date.AddDays(1).ToUniversalTime());
                    var totalMs = plays.Sum(p => p.DurationMs);
                    await SendJson(response, new { plays, stats = new { plays = plays.Count, avgAccuracy = plays.Count > 0 ? plays.Average(p => p.Accuracy * 100) : 0, avgPP = plays.Count > 0 ? plays.Average(p => p.PP) : 0, duration = totalMs >= 3600000 ? $"{totalMs/3600000}h {(totalMs%3600000)/60000}m" : $"{totalMs/60000}m" } });
                } else await SendJson(response, new { error = "Invalid date" }, 400);
                break;
            case "/api/history/month": await SendJson(response, new { playCounts = await GetMonthPlayCountsAsync(GetQueryInt(context, "year", DateTime.Now.Year), GetQueryInt(context, "month", DateTime.Now.Month)) }); break;
            case "/api/analytics": await SendJson(response, await GetAnalyticsDataAsync()); break;
            case "/api/import/lazer": if (method == "POST") { var (added, skipped, error) = await new LazerImportService(_db).ImportScoresAsync(SettingsManager.Current.LazerPath); if (error != null) await SendJson(response, new { success = false, message = error }, 400); else await SendJson(response, new { success = true, count = added, skipped }); } break;
            case "/api/settings":
                if (method == "GET") await SendJson(response, SettingsManager.Current);
                else if (method == "POST") { var payload = await ReadJsonBodyAsync<Dictionary<string, object>>(context.Request); if (payload != null) { SettingsManager.UpdateFromDictionary(payload); await SendJson(response, new { success = true }); } else await SendJson(response, new { error = "Invalid settings" }, 400); }
                break;
            case "/api/profile":
                string? token = SettingsManager.Current.AccessToken;
                if (!string.IsNullOrEmpty(token)) {
                    var profile = await _authService.GetUserProfileAsync(token);
                    if (profile != null) { await SendJson(response, new { isLoggedIn = true, username = profile.Value.TryGetProperty("username", out var un) ? un.GetString() : null, avatarUrl = profile.Value.TryGetProperty("avatar_url", out var av) ? av.GetString() : null }); return; }
                }
                await SendJson(response, new { isLoggedIn = false });
                break;
            default:
                if (path.StartsWith("/api/play/") && path.EndsWith("/rewind")) await HandleRewindRequest(context, ExtractIdFromPath(path, "/api/play/"));
                else if (path.StartsWith("/api/rewind/osr")) await HandleRewindOsr(context);
                else if (path == "/rewind/file") await HandleRewindFile(context);
                else if (path.StartsWith("/api/rewind/skins")) await HandleRewindSkins(context);
                else if (path.StartsWith("/api/rewind/skin-manifest")) await HandleRewindSkinManifest(context);
                else if (path.StartsWith("/api/rewind/skin-files/")) await HandleRewindSkinFileClean(context);
                else if (path.StartsWith("/api/background/")) {
                    var identifier = path.Replace("/api/background/", "");
                    if (identifier == "stable") {
                        var base64 = context.Request.QueryString["path"];
                        if (!string.IsNullOrEmpty(base64)) { try { await ServeStableBackground(context, Encoding.UTF8.GetString(Convert.FromBase64String(base64))); } catch { await SendJson(response, new { error = "Invalid path" }, 400); } }
                        else await SendJson(response, new { error = "Path missing" }, 400);
                    } else await ServeBackground(context, identifier);
                } else await SendJson(response, new { error = "Not found" }, 404);
                break;
        }
    }

    private async Task<object> GetAnalyticsDataAsync() { var t = await _db.GetTotalStatsAsync(); var d = await _db.GetDailyAverageStatsAsync(); var s = await _db.GetDailyStatsAsync(30); double m = s.Any() ? s.Max(x => x.AvgPP) : 1; return new { totalPlays = t.totalPlays, totalMinutes = t.totalTimeMs / 60000.0, avgAccuracy = d.avgDailyAcc, avgPP = d.avgDailyPP, performanceMatch = m > 0 ? (d.avgDailyPP / m) * 100.0 : 0 }; }
    private async Task<Dictionary<string, int>> GetMonthPlayCountsAsync(int y, int m) { var p = await _db.FetchPlaysRangeAsync(new DateTime(y, m, 1).ToUniversalTime(), new DateTime(y, m, 1).AddMonths(1).ToUniversalTime()); return p.GroupBy(x => x.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd")).ToDictionary(g => g.Key, g => g.Count()); }
    private async Task ServeStaticFile(HttpListenerContext context, string path) { if (path == "/") path = "/index.html"; var local = ResolveStaticPath(path); if (File.Exists(local)) { context.Response.Headers["Cache-Control"] = "no-store"; var buf = await File.ReadAllBytesAsync(local); context.Response.ContentType = GetContentType(Path.GetExtension(local)); await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length); context.Response.Close(); } else await SendJson(context.Response, new { error = "Not found" }, 404); }
    private string ResolveStaticPath(string path) { var rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar); var p = Path.Combine(_webRoot, rel); if (File.Exists(p)) return p; if (rel.Equals("favicon.ico", StringComparison.OrdinalIgnoreCase)) return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "favicon.ico"); if (rel.StartsWith("assets" + Path.DirectorySeparatorChar + "mods")) return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Assets", "Mods", rel.Substring(12)); return p; }
    private async Task HandleWebSocket(HttpListenerContext context) { var ws = (await context.AcceptWebSocketAsync(null)).WebSocket; lock (_clientsLock) _liveClients.Add(ws); try { while (ws.State == WebSocketState.Open) await ws.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), _cts.Token); } finally { lock (_clientsLock) _liveClients.Remove(ws); } }
    public async Task BroadcastLog(string message, string level = "info") => await Broadcast(JsonSerializer.Serialize(new { type = "log", message, level }));
    public async Task BroadcastLiveData(object data) => await Broadcast(JsonSerializer.Serialize(new { type = "live", data }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    private async Task Broadcast(string payload) { var bytes = Encoding.UTF8.GetBytes(payload); WebSocket[] clients; lock (_clientsLock) clients = _liveClients.Where(c => c.State == WebSocketState.Open).ToArray(); foreach (var c in clients) try { await c.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); } catch { } }
    private int GetQueryInt(HttpListenerContext context, string key, int def) => int.TryParse(context.Request.QueryString[key], out var r) ? r : def;
    private int ExtractIdFromPath(string path, string prefix) { var rest = path.Substring(prefix.Length); var slash = rest.IndexOf('/'); return int.TryParse(slash >= 0 ? rest.Substring(0, slash) : rest, out var id) ? id : 0; }
    private string GetContentType(string ext) => ext switch { ".html" => "text/html", ".css" => "text/css", ".js" => "application/javascript", ".json" => "application/json", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".ogg" => "audio/ogg", ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".osu" => "text/plain", ".ini" => "text/plain", ".osr" => "application/octet-stream", _ => "application/octet-stream" };
    private async Task SendJson(HttpListenerResponse resp, object data, int code = 200) { var buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })); resp.StatusCode = code; resp.ContentType = "application/json"; await resp.OutputStream.WriteAsync(buf, 0, buf.Length); resp.Close(); }
    private static async Task<T?> ReadJsonBodyAsync<T>(HttpListenerRequest request) { using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8); var body = await reader.ReadToEndAsync(); return string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
    private static string BuildReplayData(Replay replay) { var sb = new StringBuilder(); var cur = 0; foreach (var f in replay.ReplayFrames ?? new List<OsuParsers.Replays.Objects.ReplayFrame>()) { var diff = f.TimeDiff == 0 && f.Time > 0 ? f.Time - cur : f.TimeDiff; cur += diff; sb.Append($"{diff}|{f.X.ToString(CultureInfo.InvariantCulture)}|{f.Y.ToString(CultureInfo.InvariantCulture)}|{GetActionMask(f, replay.Ruleset)},"); } return sb.ToString(); }
    private static int GetActionMask(OsuParsers.Replays.Objects.ReplayFrame f, Ruleset r) => r switch { Ruleset.Taiko => (int)f.TaikoKeys, Ruleset.Fruits => (int)f.CatchKeys, Ruleset.Mania => (int)f.ManiaKeys, _ => (int)f.StandardKeys };

    private async Task HandleRewindRequest(HttpListenerContext context, int id)
    {
        var play = await _db.GetPlayAsync(id);
        if (play == null) { await SendJson(context.Response, new { error = "Play not found" }, 404); return; }

        string? replayPath = play.ReplayFile;
        if (string.IsNullOrEmpty(replayPath) || !File.Exists(replayPath))
        {
            if (!string.IsNullOrEmpty(play.ReplayHash)) replayPath = ExtractReplayFromLazer(play.ReplayHash, play.CreatedAtUtc.Ticks);
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
            string stablePath = SettingsManager.Current.StablePath ?? @"C:\project\osu!";
            if (!string.IsNullOrEmpty(play.BeatmapHash) && Directory.Exists(stablePath))
            {
                var replayDir = Path.Combine(stablePath, "Data", "r");
                if (Directory.Exists(replayDir))
                {
                    var file = new DirectoryInfo(replayDir).GetFiles("*.osr").Where(f => f.Name.StartsWith(play.BeatmapHash, StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                    if (file != null && (DateTime.UtcNow - file.LastWriteTimeUtc).TotalHours < 24)
                    {
                        replayPath = file.FullName;
                        await _db.UpdatePlayReplayFileAsync(id, replayPath);
                        play.ReplayFile = replayPath;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(replayPath) || !File.Exists(replayPath)) { await SendJson(context.Response, new { error = "Replay not found" }, 404); return; }

        var localReplaysDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Replays");
        if (!replayPath.StartsWith(localReplaysDir, StringComparison.OrdinalIgnoreCase))
        {
            try {
                Directory.CreateDirectory(localReplaysDir);
                var fileName = Path.GetFileName(replayPath);
                if (fileName.EndsWith(".osr") && (replayPath.Contains("Data" + Path.DirectorySeparatorChar + "r") || replayPath.Contains("Data/r"))) fileName = $"Stable_{play.BeatmapHash.Substring(0, 8)}_{play.CreatedAtUtc.Ticks}.osr";
                var destPath = Path.Combine(localReplaysDir, fileName);
                if (!File.Exists(destPath)) File.Copy(replayPath, destPath, true);
                replayPath = destPath;
                await _db.UpdatePlayReplayFileAsync(id, replayPath);
            } catch { }
        }

        string? beatmapFolder = null;
        string? osuFileName = null;
        if (!string.IsNullOrEmpty(play.BeatmapHash))
        {
            var exportService = new RealmExportService();
            var osuPath = await exportService.ExportBeatmapAsync(play.BeatmapHash);
            if (!string.IsNullOrEmpty(osuPath))
            {
                if (Directory.Exists(osuPath)) {
                    beatmapFolder = osuPath; 
                    var osuFiles = Directory.GetFiles(osuPath, "*.osu");
                    if (osuFiles.Length > 0) osuFileName = Path.GetFileName(osuFiles[0]);
                }
                else { beatmapFolder = Path.GetDirectoryName(osuPath); osuFileName = Path.GetFileName(osuPath); }
            }
            else if (!string.IsNullOrEmpty(play.MapPath) && File.Exists(play.MapPath))
            {
                try {
                    var sourceFolder = Path.GetDirectoryName(play.MapPath);
                    if (!string.IsNullOrEmpty(sourceFolder) && Directory.Exists(sourceFolder))
                    {
                        var artist = !string.IsNullOrEmpty(play.Artist) ? play.Artist : play.BeatmapArtist;
                        var title = !string.IsNullOrEmpty(play.Title) ? play.Title : play.BeatmapTitle;
                        var folderName = string.Join("_", $"{artist} - {title} ({play.BeatmapHash.Substring(0, 8)})".Split(Path.GetInvalidFileNameChars()));
                        var destFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Songs", folderName);
                        if (!Directory.Exists(destFolder)) {
                            Directory.CreateDirectory(destFolder);
                            foreach (var file in Directory.GetFiles(sourceFolder)) File.Copy(file, Path.Combine(destFolder, Path.GetFileName(file)), true);
                        }
                        beatmapFolder = destFolder; osuFileName = Path.GetFileName(play.MapPath);
                    }
                } catch { }
            }
        }

        if (!string.IsNullOrEmpty(beatmapFolder) && Directory.Exists(beatmapFolder))
        {
            var osuFiles = Directory.GetFiles(beatmapFolder, "*.osu").Select(Path.GetFileName).ToList();
            if (osuFiles.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(play.Difficulty)) osuFileName = FindOsuFileForDifficulty(osuFiles, play.Difficulty) ?? osuFiles.FirstOrDefault();
                else osuFileName ??= osuFiles.FirstOrDefault();
            }
        }

        var timeline = await _db.GetPpTimelineAsync(play.Id);
        await SendJson(context.Response, new { replayPath, songsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Songs"), skinsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Skins"), beatmapFolder, osuFileName, mods = play.Mods ?? "", beatmapHash = play.BeatmapHash ?? "", replaysRoot = Path.GetDirectoryName(replayPath) ?? "", statsTimeline = timeline });
    }

    private static string? FindOsuFileForDifficulty(IEnumerable<string?> files, string difficulty)
    {
        var target = $"[{difficulty.Trim()}]";
        var match = files.FirstOrDefault(n => n?.Contains(target, StringComparison.OrdinalIgnoreCase) == true);
        if (match != null) return match;
        var norm = difficulty.Replace(" ", "").Trim();
        return files.FirstOrDefault(n => n?.Replace(" ", "").Contains(norm, StringComparison.OrdinalIgnoreCase) == true);
    }

    private async Task HandleCursorOffsets(HttpListenerContext context) { var payload = await ReadJsonBodyAsync<Dictionary<string, object>>(context.Request); if (payload != null && payload.TryGetValue("scoreId", out var sId) && payload.TryGetValue("offsets", out var off)) { await _db.UpdateCursorOffsetsAsync(long.Parse(sId!.ToString()!), JsonSerializer.Serialize(off)); await SendJson(context.Response, new { success = true }); } else await SendJson(context.Response, new { error = "Invalid payload" }, 400); }
    private async Task HandleRewindOsr(HttpListenerContext context) { var rP = WebUtility.UrlDecode(context.Request.QueryString["path"] ?? ""); if (File.Exists(rP)) { try { var replay = ReplayDecoder.Decode(rP); await SendJson(context.Response, new { gameVersion = replay.OsuVersion, replayData = BuildReplayData(replay), mods = (int)replay.Mods, replayMD5 = replay.ReplayMD5Hash, beatmapMD5 = replay.BeatmapMD5Hash, playerName = replay.PlayerName }); } catch (Exception ex) { await SendJson(context.Response, new { error = ex.Message }, 500); } } else await SendJson(context.Response, new { error = "Replay not found" }, 404); }
    private async Task HandleRewindFile(HttpListenerContext context) { 
        var fP = WebUtility.UrlDecode(context.Request.QueryString["path"] ?? "").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar); 
        var dL = System.Text.RegularExpressions.Regex.Matches(fP, @"[A-Za-z]:[\\\/]"); 
        if (dL.Count > 1) fP = fP.Substring(dL[dL.Count - 1].Index); 
        if (File.Exists(fP)) try { await ServeLocalFile(context, fP); } catch (Exception ex) { await SendJson(context.Response, new { error = ex.Message }, 500); }
        else if (Directory.Exists(fP)) { context.Response.StatusCode = 200; context.Response.Close(); }
        else await SendJson(context.Response, new { error = "Not found", path = fP }, 404); 
    }
    private async Task ServeStableBackground(HttpListenerContext context, string p)
    {
        if (File.Exists(p))
        {
            context.Response.ContentType = GetContentType(Path.GetExtension(p).ToLowerInvariant());
            var buf = await File.ReadAllBytesAsync(p);
            await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length);
            context.Response.Close();
        }
        else
        {
            // Try to find the file if the drive letter is wrong (e.g. moved installation)
            // This is common if the DB has old paths "E:\osu!..." but now it's "C:\osu!..."
            // We can try to re-resolve relative to the current StablePath setting
            var fileName = Path.GetFileName(p);
            var folderName = Path.GetFileName(Path.GetDirectoryName(p));
            
            var stablePath = SettingsManager.Current.StablePath;
            if (!string.IsNullOrEmpty(stablePath))
            {
                // Try: Stable/Songs/FolderName/FileName
                var candidate = Path.Combine(stablePath, "Songs", folderName ?? "", fileName);
                if (File.Exists(candidate))
                {
                    context.Response.ContentType = GetContentType(Path.GetExtension(candidate).ToLowerInvariant());
                    var buf = await File.ReadAllBytesAsync(candidate);
                    await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length);
                    context.Response.Close();
                    return;
                }
            }
            
            await SendJson(context.Response, new { error = "File not found" }, 404);
        }
    }
    private async Task ServeBackground(HttpListenerContext context, string id) { var lP = SettingsManager.Current.LazerPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu"); var p = Path.Combine(lP, "files", id.Substring(0, 1), id.Substring(0, 2), id); if (File.Exists(p)) { context.Response.ContentType = GetContentType(Path.GetExtension(p).ToLowerInvariant()); var buf = await File.ReadAllBytesAsync(p); await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length); context.Response.Close(); } else await SendJson(context.Response, new { error = "Not found" }, 404); }
    private async Task HandleRewindSkins(HttpListenerContext context) { await SendJson(context.Response, new { skins = new[] { "-Fun3cL" } }); }
    
    private async Task HandleRewindSkinManifest(HttpListenerContext context)
    {
        var requestedSkin = context.Request.QueryString["skin"] ?? "";
        var skinName = (string.IsNullOrEmpty(requestedSkin) || requestedSkin.Contains("Default")) ? "-Fun3cL" : requestedSkin;
        var skinsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Skins");
        string sPath = SettingsManager.Current.StablePath ?? "";
        if (string.IsNullOrEmpty(sPath)) {
            var bD = AppDomain.CurrentDomain.BaseDirectory;
            var cands = new[] { Path.Combine(bD, "..", "..", "..", "..", "osu!"), @"C:\project\osu!" };
            foreach (var c in cands) if (Directory.Exists(c)) { sPath = c; break; }
        }
        var sFull = !string.IsNullOrEmpty(sPath) ? Path.Combine(sPath, "Skins", skinName) : Path.Combine(skinsRoot, skinName);
        if (!Directory.Exists(sFull)) sFull = Path.Combine(skinsRoot, "-Fun3cL");

        if (Directory.Exists(sFull)) {
            var files = Directory.GetFiles(sFull, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(sFull, f)).Select(f => f.Replace('\\', '/').ToLowerInvariant()).ToList();
            var expanded = new HashSet<string>(files);
            foreach (var f in files) {
                var fN = Path.GetFileName(f); var dN = Path.GetDirectoryName(f)?.Replace('\\', '/') ?? ""; if (dN.Length > 0) dN += "/";
                var m = System.Text.RegularExpressions.Regex.Match(fN, @"^([a-z]+)-([0-9]+.*)");
                if (m.Success) expanded.Add(dN + m.Groups[1].Value + m.Groups[2].Value);
                else { var mN = System.Text.RegularExpressions.Regex.Match(fN, @"^([a-z]+)([0-9]+.*)"); if (mN.Success) expanded.Add(dN + mN.Groups[1].Value + "-" + mN.Groups[2].Value); }
                if (f.Contains("/")) expanded.Add(f.Replace('/', '\\'));
            }
            await SendJson(context.Response, new { skin = requestedSkin, files = expanded.OrderBy(f => f).ToArray() });
        } else await SendJson(context.Response, new { error = "Skin not found" }, 404);
    }

    private async Task HandleRewindSkinFileClean(HttpListenerContext context)
    {
        var prefix = "/api/rewind/skin-files/";
        var rP = context.Request.Url?.AbsolutePath ?? "";
        var rel = rP.StartsWith(prefix) ? WebUtility.UrlDecode(rP.Substring(prefix.Length)).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar) : "";
        if (string.IsNullOrWhiteSpace(rel)) { await SendJson(context.Response, new { error = "Path missing" }, 400); return; }
        var pts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (pts.Length == 0) { await SendJson(context.Response, new { error = "Not found" }, 404); return; }
        var requested = pts[0]; var sub = Path.Combine(pts.Skip(1).ToArray());
        var sName = (requested.Contains("Default")) ? "-Fun3cL" : requested;

        if (sub.Equals("skin.ini", StringComparison.OrdinalIgnoreCase)) {
            var iPath = ""; string sP = SettingsManager.Current.StablePath ?? "";
            var cands = new[] { Path.Combine(sP, "Skins", sName, "skin.ini"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Skins", sName, "skin.ini") };
            foreach (var c in cands) if (File.Exists(c)) { iPath = c; break; }
            if (File.Exists(iPath)) {
                var content = (await File.ReadAllTextAsync(iPath)).Replace("fonts/hitcircle\\", "fonts/hitcircle/").Replace("fonts/score\\", "fonts/score/").Replace("fonts/combo\\", "fonts/combo/").Replace("fonts/dots\\", "fonts/dots/");
                var buf = Encoding.UTF8.GetBytes(content); context.Response.ContentType = "text/plain"; context.Response.AddHeader("Access-Control-Allow-Origin", "*"); await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length); context.Response.Close(); return;
            }
        }

        var sRoots = new List<string>();
        string stP = SettingsManager.Current.StablePath ?? "";
        if (string.IsNullOrEmpty(stP)) { var bD = AppDomain.CurrentDomain.BaseDirectory; var cands = new[] { Path.Combine(bD, "..", "..", "..", "..", "osu!"), @"C:\project\osu!" }; foreach (var c in cands) if (Directory.Exists(c)) { stP = c; break; } }
        if (!string.IsNullOrEmpty(stP)) sRoots.Add(Path.Combine(stP, "Skins", sName));
        sRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Skins", sName));
        sRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "WebUI", "rewind", "Skins", sName));

        string? fP = null; var fCands = new List<string> { sub }; if (!sub.Contains('.')) fCands.AddRange(new[] { sub + ".png", sub + ".jpg", sub + ".wav", sub + ".mp3" });
        foreach (var pts_c in fCands) {
            var dCands = new List<string> { pts_c }; 
            var nC = pts_c.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar); 
            if (nC != pts_c) dCands.Add(nC);
            var fN = Path.GetFileName(pts_c); var dN = Path.GetDirectoryName(pts_c) ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(fN, @"^([a-zA-Z]+)(-?)([0-9]+.*)");
            if (m.Success) dCands.Add(Path.Combine(dN, (m.Groups[2].Value == "-") ? m.Groups[1].Value + m.Groups[3].Value : m.Groups[1].Value + "-" + m.Groups[3].Value));

            foreach (var fC in dCands) {
                foreach (var r in sRoots) {
                    if (!Directory.Exists(r)) continue;
                    var p = Path.Combine(r, fC); if (File.Exists(p)) { fP = p; break; }
                    if (fC.Contains("@2x")) { var n2 = Path.Combine(r, fC.Replace("@2x", "")); if (File.Exists(n2)) { fP = n2; break; } }
                    var fF = new[] { "Fonts/score", "Fonts/hitcircle", "Fonts/combo", "Fonts/dots", "score", "hitcircle", "combo", "dots" };
                    foreach (var fld in fF) {
                        var pF = Path.Combine(r, fld, Path.GetFileName(fC)); if (File.Exists(pF)) { fP = pF; break; }
                        if (fC.Contains("@2x")) { var nF = Path.Combine(r, fld, Path.GetFileName(fC).Replace("@2x", "")); if (File.Exists(nF)) { fP = nF; break; } }
                    }
                    if (fP != null) break;
                }
                if (fP != null) break;
            }
            if (fP != null) break;
        }
        if (fP != null) await ServeLocalFile(context, fP);
        else await SendJson(context.Response, new { error = "Not found" }, 404);
    }

    private async Task ServeLocalFile(HttpListenerContext context, string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        context.Response.ContentType = GetContentType(Path.GetExtension(path).ToLowerInvariant());
        context.Response.AddHeader("Access-Control-Allow-Origin", "*");
        context.Response.Headers["Cache-Control"] = "public, max-age=3600";
        var rH = context.Request.Headers["Range"];
        if (!string.IsNullOrEmpty(rH) && rH.StartsWith("bytes=")) {
            var sp = rH.Substring(6).Split('-');
            long s = long.Parse(sp[0]), e = sp.Length > 1 && !string.IsNullOrEmpty(sp[1]) ? long.Parse(sp[1]) : bytes.Length - 1;
            context.Response.StatusCode = 206; context.Response.Headers["Content-Range"] = $"bytes {s}-{e}/{bytes.Length}"; context.Response.Headers["Accept-Ranges"] = "bytes"; context.Response.ContentLength64 = e - s + 1;
            await context.Response.OutputStream.WriteAsync(bytes, (int)s, (int)(e - s + 1));
        } else { context.Response.Headers["Accept-Ranges"] = "bytes"; context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length); }
        context.Response.Close();
    }

    private string? ExtractReplayFromLazer(string h, long t) { try { var lP = SettingsManager.Current.LazerPath; var sP = Path.Combine(lP ?? "", "files", h.Substring(0, 1), h.Substring(0, 2), h); if (File.Exists(sP)) { var rD = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Replays"); Directory.CreateDirectory(rD); string dP = Path.Combine(rD, $"{t}_{h}.osr"); if (!File.Exists(dP)) File.Copy(sP, dP, true); return dP; } } catch { } return null; }
    public void Dispose() { Stop(); _cts.Dispose(); }
}
