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
        _db = db;
        _authService = new AuthService();
        Port = port;
        _webRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "WebUI");
        _cts = new CancellationTokenSource();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        
        // Listen on port 7777 for OAuth callback
        if (port != 7777)
        {
            _listener.Prefixes.Add("http://localhost:7777/");
            _listener.Prefixes.Add("http://127.0.0.1:7777/");
        }

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
                if (!string.IsNullOrEmpty(dateStr))
                {
                    var plays = await _db.FetchPlaysByLocalDayAsync(dateStr);
                    var totalMs = plays.Sum(p => p.DurationMs);
                    await SendJson(response, new { plays, stats = new { plays = plays.Count, avgAccuracy = plays.Count > 0 ? plays.Average(p => p.Accuracy * 100) : 0, avgPP = plays.Count > 0 ? plays.Average(p => p.PP) : 0, duration = totalMs >= 3600000 ? $"{totalMs/3600000}h {(totalMs%3600000)/60000}m" : $"{totalMs/60000}m" } });
                } else await SendJson(response, new { error = "Date missing" }, 400);
                break;
            case "/api/history/month": await SendJson(response, new { playCounts = await GetMonthPlayCountsAsync(GetQueryInt(context, "year", DateTime.Now.Year), GetQueryInt(context, "month", DateTime.Now.Month)) }); break;
            case "/api/analytics":
                var periodStr = context.Request.QueryString["days"];
                int days = 30;
                if (periodStr == "today") days = -1;
                else int.TryParse(periodStr, out days);
                await SendJson(response, await GetAnalyticsDataAsync(days));
                break;
            case "/api/goals":
                var progress = await _db.GetTodayGoalProgressAsync(SettingsManager.Current.GoalStars);
                await SendJson(response, new { 
                    settings = new {
                        plays = SettingsManager.Current.GoalPlays,
                        hits = SettingsManager.Current.GoalHits,
                        stars = SettingsManager.Current.GoalStars,
                        pp = SettingsManager.Current.GoalPP
                    },
                    progress = new {
                        plays = progress.Plays,
                        hits = progress.Hits,
                        stars = progress.StarPlays,
                        pp = progress.TotalPP
                    }
                });
                break;
            case "/api/goals/save":
                if (method == "POST") {
                    var payload = await ReadJsonBodyAsync<Dictionary<string, object>>(context.Request);
                    if (payload != null) {
                        if (payload.TryGetValue("plays", out var gp)) SettingsManager.Current.GoalPlays = TryGetInt(gp);
                        if (payload.TryGetValue("hits", out var gh)) SettingsManager.Current.GoalHits = TryGetInt(gh);
                        if (payload.TryGetValue("stars", out var gs)) SettingsManager.Current.GoalStars = TryGetDouble(gs);
                        if (payload.TryGetValue("pp", out var gpp)) SettingsManager.Current.GoalPP = TryGetInt(gpp);
                        SettingsManager.Save();
                        await BroadcastRefresh();
                        await SendJson(response, new { success = true });
                    } else await SendJson(response, new { error = "Invalid payload" }, 400);
                }
                break;
            case "/api/import/lazer": 
                if (method == "POST") { 
                    var importService = new LazerImportService(_db);
                    var (added, skipped, error) = await importService.ImportScoresAsync(SettingsManager.Current.LazerPath, SettingsManager.Current.Username); 
                    if (error != null) { DebugService.Log($"[LazerImport] Failed: {error}", "ApiServer"); await SendJson(response, new { success = false, message = error }, 400); }
                    else { await _db.MigrateAsync(); await BroadcastRefresh(); await SendJson(response, new { success = true, count = added, skipped }); }
                } 
                break;
            case "/api/import/stable":
                if (method == "POST") {
                    var importService = new OsuStableImportService(_db);
                    var aliases = context.Request.QueryString["aliases"];
                    var (added, skipped, error) = await importService.ImportScoresAsync(SettingsManager.Current.StablePath, SettingsManager.Current.Username, aliases);
                    if (!string.IsNullOrEmpty(error)) { DebugService.Log($"[StableImport] Failed: {error}", "ApiServer"); await SendJson(response, new { success = false, message = error }, 400); }
                    else { await _db.MigrateAsync(); await BroadcastRefresh(); await SendJson(response, new { success = true, count = added, skipped }); }
                }
                break;
            case "/api/settings":
                if (method == "GET") await SendJson(response, SettingsManager.Current);
                else if (method == "POST") { var payload = await ReadJsonBodyAsync<Dictionary<string, object>>(context.Request); if (payload != null) { SettingsManager.UpdateFromDictionary(payload); await SendJson(response, new { success = true }); } else await SendJson(response, new { error = "Invalid settings" }, 400); }
                break;
            case "/api/settings/delete-scores":
                if (method == "POST") { await _db.DeleteAllScoresAsync(); await BroadcastRefresh(); await SendJson(response, new { success = true }); }
                break;
            case "/api/settings/delete-beatmaps":
                if (method == "POST") { await _db.DeleteAllBeatmapsAsync(); await SendJson(response, new { success = true }); }
                break;
            case "/api/data/delete-zero":
                if (method == "POST") { 
                    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsuGrind", "osugrind.sqlite")}");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM plays WHERE score = 0";
                    await cmd.ExecuteNonQueryAsync();
                    await BroadcastRefresh();
                    await SendJson(response, new { success = true }); 
                }
                break;
            case "/api/auth/login":
                await SendJson(response, new { authUrl = _authService.GetAuthUrl() });
                break;
            case "/api/auth/logout":
                SettingsManager.Current.AccessToken = null; SettingsManager.Save(); await SendJson(response, new { success = true });
                break;
            case "/callback":
                var code = context.Request.QueryString["code"];
                if (!string.IsNullOrEmpty(code)) {
                    var authToken = await _authService.ExchangeCodeForTokenAsync(code);
                    if (!string.IsNullOrEmpty(authToken)) {
                        SettingsManager.Current.AccessToken = authToken; SettingsManager.Save();
                        var html = "<html><body style='background:#111;color:#fff;font-family:sans-serif;text-align:center;padding-top:50px;'><h1>Login Successful!</h1><p>You can close this window now.</p><script>setTimeout(() => { window.location.href = 'http://localhost:" + Port + "/'; }, 1000);</script></body></html>";
                        var b = Encoding.UTF8.GetBytes(html); response.ContentType = "text/html"; await response.OutputStream.WriteAsync(b, 0, b.Length); response.Close(); return;
                    }
                }
                await SendJson(response, new { error = "Auth failed" }, 400);
                break;
            case "/api/profile":
                string? token = SettingsManager.Current.AccessToken;
                if (!string.IsNullOrEmpty(token)) {
                    var profile = await _authService.GetUserProfileAsync(token);
                    if (profile != null) { 
                        var p = profile.Value;
                        double pp = 0; double acc = 0; int pc = 0; int gr = 0; int cr = 0; int mc = 0; int uid = 0;
                        if (p.TryGetProperty("id", out var idP)) uid = idP.GetInt32();
                        if (p.TryGetProperty("statistics", out var stats)) {
                            if (stats.TryGetProperty("pp", out var ppP)) pp = ppP.GetDouble();
                            if (stats.TryGetProperty("hit_accuracy", out var acP)) acc = acP.GetDouble();
                            if (stats.TryGetProperty("play_count", out var pcP)) pc = pcP.GetInt32();
                            if (stats.TryGetProperty("global_rank", out var grP) && grP.ValueKind != JsonValueKind.Null) gr = grP.GetInt32();
                            if (stats.TryGetProperty("country_rank", out var crP) && crP.ValueKind != JsonValueKind.Null) cr = crP.GetInt32();
                            if (stats.TryGetProperty("maximum_combo", out var mcP)) mc = mcP.GetInt32();
                        }
                        var username = p.TryGetProperty("username", out var un) ? un.GetString() : null;
                        if (!string.IsNullOrEmpty(username)) { SettingsManager.Current.Username = username; SettingsManager.Current.PeakPP = Math.Max(SettingsManager.Current.PeakPP, pp); SettingsManager.Save(); }
                        await SendJson(response, new { isLoggedIn = true, userId = uid, username, avatarUrl = p.TryGetProperty("avatar_url", out var av) ? av.GetString() : null, coverUrl = p.TryGetProperty("cover", out var cov) && cov.TryGetProperty("url", out var curl) ? curl.GetString() : null, globalRank = gr, countryRank = cr, pp, accuracy = acc, playCount = pc, maxCombo = mc }); 
                        return; 
                    }
                }
                await SendJson(response, new { isLoggedIn = false });
                break;
            case "/api/profile/top":
                string? topToken = SettingsManager.Current.AccessToken;
                if (!string.IsNullOrEmpty(topToken)) {
                    var profile = await _authService.GetUserProfileAsync(topToken);
                    if (profile != null && profile.Value.TryGetProperty("id", out var idP)) {
                        var topScores = await _authService.GetUserTopScoresAsync(topToken, idP.GetInt32());
                        if (topScores != null) { await SendJson(response, topScores); return; }
                    }
                }
                await SendJson(response, new { error = "Not logged in or failed to fetch" }, 401);
                break;
            default:
                if (path.StartsWith("/api/play/")) {
                    var id = ExtractIdFromPath(path, "/api/play/");
                    if (method == "DELETE") { await _db.DeletePlayAsync(id); await BroadcastRefresh(); await SendJson(response, new { success = true }); }
                    else if (path.EndsWith("/notes") && method == "POST") {
                        var payload = await ReadJsonBodyAsync<Dictionary<string, string>>(context.Request);
                        if (payload != null && payload.TryGetValue("notes", out var notes)) { await _db.UpdatePlayNotesAsync(id, notes); await SendJson(response, new { success = true }); }
                        else await SendJson(response, new { error = "Invalid notes" }, 400);
                    }
                    else if (path.EndsWith("/rewind")) await HandleRewindRequest(context, id);
                }
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

    private async Task<object> GetAnalyticsDataAsync(int days)
    {
        var summary = await _db.GetAnalyticsSummaryAsync(days);
        var daily = (days == -1) ? await _db.GetHourlyStatsTodayAsync() : await _db.GetDailyStatsAsync(days);
        var playsToday = await _db.GetPlaysTodayCountAsync();
        var streak = await _db.GetPlayStreakAsync();
        
        var recentSummary = await _db.GetAnalyticsSummaryAsync(7);
        var baselineSummary = await _db.GetAnalyticsSummaryAsync(90); // 90 days for better baseline
        
        string form = "No Data";
        if (summary.TotalPlays > 0)
        {
            double rPP = recentSummary.AvgPP;
            double bPP = baselineSummary.AvgPP;
            
            if (rPP > bPP * 1.10) form = "Peak";
            else if (rPP > bPP * 1.03) form = "Improving";
            else if (rPP < bPP * 0.90) form = "Burnout";
            else if (rPP < bPP * 0.96) form = "Slumping";
            else form = "Stable";
        }

        // IMPROVED MENTALITY FORMULA
        // Factors: Pass Rate (Focus), Accuracy (Precision), and Play Density
        double mentality = 0; 
        if (summary.TotalPlays > 0)
        {
            double passRate = (double)summary.TotalPlays > 0 ? (double)daily.Sum(d => d.PassCount) / summary.TotalPlays : 0;
            double accFactor = summary.AvgAccuracy; // Already 0.0 to 1.0
            
            // Density Factor: Reward sessions with consistent play time
            double hoursPlayed = summary.TotalDurationMs / 3600000.0;
            double density = Math.Min(1.0, summary.TotalPlays / (hoursPlayed * 10 + 1)); // Target ~10 plays per hour

            mentality = (passRate * 50.0) + (accFactor * 40.0) + (density * 10.0);
        }

        double peakPP = SettingsManager.Current.PeakPP;
        // Fallback: use the best daily average ever recorded in the DB
        var allDaily = await _db.GetDailyStatsAsync(0);
        double recordDailyAvg = allDaily.Any() ? allDaily.Max(d => d.AvgPP) : 1;
        double referencePP = Math.Max(peakPP, recordDailyAvg);
        if (referencePP <= 0) referencePP = 1;

        return new {
            totalPlays = summary.TotalPlays,
            totalMinutes = summary.TotalDurationMs / 60000.0,
            avgAccuracy = summary.AvgAccuracy,
            avgPP = summary.AvgPP,
            avgUR = summary.AvgUR,
            avgKeyRatio = summary.AvgKeyRatio,
            playsToday = playsToday,
            streak = streak,
            perfMatch = summary.TotalPlays > 0 ? (summary.AvgPP / referencePP) * 100.0 : 0,
            currentForm = form,
            mentality = Math.Clamp(mentality, 0, 100),
            dailyActivity = daily.Select(d => new {
                date = d.Date,
                plays = d.PlayCount,
                minutes = d.TotalDurationMs / 60000.0,
                avgPP = d.AvgPP,
                avgAcc = d.AvgAcc * 100.0,
                avgUR = d.AvgUR,
                avgKeyRatio = d.AvgKeyRatio
            }),
            dailyPerformance = daily.Select(d => new {
                date = d.Date,
                match = (d.AvgPP / peakPP) * 100.0
            }),
            hitErrors = await GetRecentHitErrorsAsync(days)
        };
    }

    private async Task<List<double>> GetRecentHitErrorsAsync(int days)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsuGrind", "osugrind.sqlite")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        string filter = days > 0 ? "WHERE created_at_utc >= datetime('now', $offset) AND hit_errors IS NOT NULL" : "WHERE hit_errors IS NOT NULL";
        int limit = days > 0 ? 100 : 2000; // Sample more data for "ALL"
        cmd.CommandText = $"SELECT hit_errors FROM plays {filter} ORDER BY created_at_utc DESC LIMIT {limit}";
        if (days > 0) cmd.Parameters.AddWithValue("$offset", $"-{days} days");
        var errors = new List<double>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            try {
                var json = reader.GetString(0);
                var list = JsonSerializer.Deserialize<List<double>>(json);
                if (list != null) errors.AddRange(list);
            } catch {}
        }
        return errors;
    }

    private async Task<Dictionary<string, int>> GetMonthPlayCountsAsync(int y, int m) 
    { 
        return await _db.GetMonthPlayCountsAsync(y, m); 
    }

    private async Task ServeStaticFile(HttpListenerContext context, string path) 
    { 
        if (path == "/") path = "/index.html"; 
        var local = ResolveStaticPath(path); 
        if (File.Exists(local)) 
        { 
            context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";
            var buf = await File.ReadAllBytesAsync(local); 
            context.Response.ContentType = GetContentType(Path.GetExtension(local)); 
            await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length); 
            context.Response.Close(); 
        } 
        else await SendJson(context.Response, new { error = "Not found" }, 404); 
    }
    private string ResolveStaticPath(string path) { var rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar); var p = Path.Combine(_webRoot, rel); if (File.Exists(p)) return p; if (rel.Equals("favicon.ico", StringComparison.OrdinalIgnoreCase)) return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "WebUI", "rewind", "favicon.ico"); if (rel.StartsWith("assets" + Path.DirectorySeparatorChar + "mods")) return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Assets", "Mods", rel.Substring(12)); return p; }
    private async Task HandleWebSocket(HttpListenerContext context) { var ws = (await context.AcceptWebSocketAsync(null)).WebSocket; lock (_clientsLock) _liveClients.Add(ws); try { while (ws.State == WebSocketState.Open) await ws.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), _cts.Token); } finally { lock (_clientsLock) _liveClients.Remove(ws); } }
    public async Task BroadcastLog(string message, string level = "info") => await Broadcast(JsonSerializer.Serialize(new { type = "log", message, level }));
    public async Task BroadcastLiveData(object data) => await Broadcast(JsonSerializer.Serialize(new { type = "live", data }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    public async Task BroadcastRefresh() => await Broadcast(JsonSerializer.Serialize(new { type = "refresh" }));
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
        var play = await _db.GetPlayAsync(id); if (play == null) { await SendJson(context.Response, new { error = "Play not found" }, 404); return; }
        string? replayPath = play.ReplayFile; if (string.IsNullOrEmpty(replayPath) || !File.Exists(replayPath)) if (!string.IsNullOrEmpty(play.ReplayHash)) replayPath = ExtractReplayFromLazer(play.ReplayHash, play.CreatedAtUtc.Ticks);
        if (string.IsNullOrEmpty(replayPath)) replayPath = await new RealmExportService().SearchReplayByTimeAsync(play.CreatedAtUtc, TimeSpan.FromMinutes(5), play.BeatmapHash);
        if (string.IsNullOrEmpty(replayPath) || !File.Exists(replayPath)) {
            string sPath = SettingsManager.Current.StablePath ?? @"C:\project\osu!";
            if (Directory.Exists(sPath)) {
                var rDir = Path.Combine(sPath, "Data", "r");
                if (Directory.Exists(rDir)) {
                    var f = new DirectoryInfo(rDir).GetFiles("*.osr").Where(x => x.Name.StartsWith(play.BeatmapHash, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.LastWriteTime).FirstOrDefault();
                    if (f != null && (DateTime.UtcNow - f.LastWriteTimeUtc).TotalHours < 24) replayPath = f.FullName;
                }
            }
        }
        if (string.IsNullOrEmpty(replayPath) || !File.Exists(replayPath)) { await SendJson(context.Response, new { error = "Replay not found" }, 404); return; }

        var localReplaysDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Replays");
        if (!replayPath.StartsWith(localReplaysDir, StringComparison.OrdinalIgnoreCase)) {
            try {
                Directory.CreateDirectory(localReplaysDir); var fName = Path.GetFileName(replayPath);
                if (fName.EndsWith(".osr") && replayPath.Contains("Data" + Path.DirectorySeparatorChar + "r")) fName = $"Stable_{play.BeatmapHash.Substring(0, 8)}_{play.CreatedAtUtc.Ticks}.osr";
                var dPath = Path.Combine(localReplaysDir, fName); if (!File.Exists(dPath)) File.Copy(replayPath, dPath, true); replayPath = dPath; await _db.UpdatePlayReplayFileAsync(id, replayPath);
            } catch { }
        }

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
            if (osuFiles.Count > 0) { if (!string.IsNullOrWhiteSpace(play.Difficulty)) osuFileName = FindOsuFileForDifficulty(osuFiles, play.Difficulty) ?? osuFiles.FirstOrDefault(); else osuFileName ??= osuFiles.FirstOrDefault(); }
        }
        var timeline = await _db.GetPpTimelineAsync(play.Id);
        await SendJson(context.Response, new { replayPath, songsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Songs"), skinsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "rewind", "Skins"), beatmapFolder, osuFileName, mods = play.Mods ?? "", beatmapHash = play.BeatmapHash ?? "", replaysRoot = Path.GetDirectoryName(replayPath) ?? "", statsTimeline = timeline, isImported = (play.Notes != null && play.Notes.Contains("Imported")) || !string.IsNullOrEmpty(play.ReplayHash) });
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
    private async Task ServeStableBackground(HttpListenerContext context, string p) { if (File.Exists(p)) { context.Response.ContentType = GetContentType(Path.GetExtension(p).ToLowerInvariant()); var buf = await File.ReadAllBytesAsync(p); await context.Response.OutputStream.WriteAsync(buf, 0, buf.Length); context.Response.Close(); } else await SendJson(context.Response, new { error = "File not found" }, 404); }
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
    private static string? FindOsuFileForDifficulty(IEnumerable<string?> files, string difficulty)
    {
        var target = $"[{difficulty.Trim()}]";
        var match = files.FirstOrDefault(n => n?.Contains(target, StringComparison.OrdinalIgnoreCase) == true);
        if (match != null) return match;
        var norm = difficulty.Replace(" ", "").Trim();
        return files.FirstOrDefault(n => n?.Replace(" ", "").Contains(norm, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static int TryGetInt(object? val)
    {
        if (val == null) return 0;
        if (val is int i) return i;
        if (val is long l) return (int)l;
        if (val is JsonElement je) return je.TryGetInt32(out int result) ? result : 0;
        int.TryParse(val.ToString(), out int r);
        return r;
    }

    private static double TryGetDouble(object? val)
    {
        if (val == null) return 0;
        if (val is double d) return d;
        if (val is float f) return f;
        if (val is long l) return (double)l;
        if (val is int i) return (double)i;
        if (val is JsonElement je) return je.TryGetDouble(out double result) ? result : 0;
        double.TryParse(val.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r);
        return r;
    }

    public void Dispose() { Stop(); _cts.Dispose(); }
}
