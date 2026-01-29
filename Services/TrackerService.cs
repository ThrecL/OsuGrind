using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using System.Linq;
using System.Collections.Generic;

namespace OsuGrind.Services;

public class TrackerService
{
    // Change this to your own Cloudflare Worker URL if self-hosting the tracker
    private const string TrackerUrl = "https://osugrind-tracker.3cl-exe.workers.dev/ping";
    
    private static readonly HttpClient _client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private static System.Timers.Timer? _timer;
    private static TrackerDb? _db;
    private static DateTime _lastPing = DateTime.MinValue;
    private static readonly object _pingLock = new object();
    private static bool _hasPendingPlay = false;
    private static bool _isStopping = false;

    public static void Start(TrackerDb? db = null)
    {
        _db = db;
        
        // Use a System.Timers.Timer for debouncing
        _timer = new System.Timers.Timer(5 * 60 * 1000); 
        _timer.Elapsed += async (s, e) => {
            if (_hasPendingPlay) await SendPing();
        };
        _timer.AutoReset = true;
        _timer.Start();
        
        // Initial ping on launch
        Task.Run(SendPing);
    }

    public static void OnPlayFinished()
    {
        _hasPendingPlay = true;
        
        // Reset the 5-minute timer
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Start();
        }
        
        DebugService.Log("Play finished, sync scheduled in 5 minutes.", "Tracker");
    }

    public static void TriggerSync()
    {
        // Throttled manual sync (max once every 30 seconds)
        if ((DateTime.UtcNow - _lastPing).TotalSeconds < 30) return;
        Task.Run(SendPing);
    }

    private static async Task SendPing()
    {
        if (_isStopping && !_hasPendingPlay) return;

        lock (_pingLock)
        {
            // Double check throttle inside lock
            if ((DateTime.UtcNow - _lastPing).TotalSeconds < 5) return;
            _lastPing = DateTime.UtcNow;
        }

        try
        {
            _hasPendingPlay = false; // Reset flag before sending
            object? stats = null;
            object? graphs = null;

            if (_db != null && SettingsManager.Current.AccessToken != null)
            {
                // Fetch local analytics (Full timeline)
                var summary = await _db.GetAnalyticsSummaryAsync(0);
                var daily = await _db.GetDailyStatsAsync(0);
                var streak = await _db.GetPlayStreakAsync();
                
                // Baselines for composite match
                double referencePP = 1;
                double referenceAcc = 0.95;
                double targetUR = 80.0;

                if (daily.Count > 0)
                {
                    var topDays = daily.OrderByDescending(d => d.AvgPP).Take(5).ToList();
                    referencePP = topDays.Average(d => d.AvgPP);
                    referenceAcc = topDays.Average(d => d.AvgAcc);
                    targetUR = daily.Where(d => d.AvgUR > 40).OrderBy(d => d.AvgUR).Take(5).DefaultIfEmpty(new DailyStats { AvgUR = 100 }).Average(d => d.AvgUR);
                }
                if (referencePP <= 0) referencePP = 1;

                var recentSummary = await _db.GetAnalyticsSummaryAsync(14); 
                double currentRatio = referencePP > 0 ? recentSummary.AvgPP / referencePP : 0;
                
                // Form detection
                string form = "Stable";
                if (recentSummary.TotalPlays > 0) 
                { 
                    if (currentRatio > 1.05) form = "Peak"; 
                    else if (currentRatio > 0.96) form = "Great"; 
                    else if (currentRatio > 0.88) form = "Stable"; 
                    else if (currentRatio > 0.75) form = "Slumping"; 
                    else form = "Burnout"; 
                }

                // Mentality Calculation - Hard Mode
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

                    double baseScore = (resilienceScore * 0.20) + (focusScore * 0.40) + (consistencyScore * 0.40);
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
                    mentality = Math.Clamp(baseScore * multiplier * fatiguePenalty * goalMod * urPenalty, 0, 100);
                }

                // Calculate All-Time Composite Performance Match for the main card
                double allTimePPFactor = referencePP > 0 ? (summary.AvgPP / referencePP) : 0;
                double allTimeAccFactor = referenceAcc > 0 ? (summary.AvgAccuracy / referenceAcc) : 0;
                double allTimeURFactor = summary.AvgUR > 0 ? (targetUR / summary.AvgUR) : 0;
                double allTimePerfMatch = (allTimePPFactor * 0.6) + (allTimeAccFactor * 0.3) + (Math.Min(1.2, allTimeURFactor) * 0.1);

                stats = new 
                {
                    totalPlays = summary.TotalPlays,
                    totalTime = (int)(summary.TotalDurationMs / 60000.0),
                    avgAcc = summary.AvgAccuracy,
                    avgPP = summary.AvgPP,
                    avgUR = summary.AvgUR,
                    form,
                    mentality,
                    perfMatch = Math.Round(allTimePerfMatch * 100.0, 1)
                };

                // Graph Data Compression with Composite Performance Rating
                var timeline = daily.Select(d => {
                    double ppFactor = referencePP > 0 ? (d.AvgPP / referencePP) : 0;
                    double accFactor = referenceAcc > 0 ? (d.AvgAcc / referenceAcc) : 0;
                    double urFactor = d.AvgUR > 0 ? (targetUR / d.AvgUR) : 0;
                    double compositeMatch = (ppFactor * 0.6) + (accFactor * 0.3) + (Math.Min(1.2, urFactor) * 0.1);
                    
                    return new { 
                        d = d.Date, 
                        p = d.PlayCount, 
                        t = (int)(d.TotalDurationMs / 60000),
                        acc = Math.Round(d.AvgAcc * 100.0, 2), 
                        pp = Math.Round(d.AvgPP, 1), 
                        ur = Math.Round(d.AvgUR, 2), 
                        kr = Math.Round(d.AvgKeyRatio, 3),
                        m = Math.Round(compositeMatch * 100.0, 1)
                    };
                }).ToList();

                // Today's Hourly Data
                var hourly = await _db.GetHourlyStatsTodayAsync();
                var today = hourly.Select(h => new {
                    h = h.Date,
                    p = h.PlayCount,
                    t = (int)(h.TotalDurationMs / 60000),
                    acc = Math.Round(h.AvgAcc * 100.0, 2),
                    pp = Math.Round(h.AvgPP, 1),
                    ur = Math.Round(h.AvgUR, 2),
                    kr = Math.Round(h.AvgKeyRatio, 3)
                }).ToList();

                var hitErrors = await GetRecentHitErrorsAsync(0);
                var histogram = new Dictionary<string, int>();
                for (int i = -100; i <= 100; i += 2) histogram[i.ToString()] = 0;

                foreach (var err in hitErrors)
                {
                    int bin = (int)Math.Floor(err / 2.0 + 0.5) * 2;
                    if (bin >= -100 && bin <= 100) histogram[bin.ToString()]++;
                }

                graphs = new { timeline, today, histogram, streak };
            }

            var payload = new
            {
                userId = SettingsManager.Current.UniqueId,
                token = SettingsManager.Current.AccessToken,
                version = "1.0.1",
                stats,
                graphs
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _client.PostAsync(TrackerUrl, content);
        }
        catch (Exception ex)
        {
            DebugService.Log($"Tracker Ping Failed: {ex.Message}", "Tracker");
        }
    }

    private static async Task<List<double>> GetRecentHitErrorsAsync(int days)
    {
        if (_db == null) return new List<double>();
        var errors = new List<double>();
        try {
            string dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsuGrind", "osugrind.sqlite");
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            string filter = days > 0 ? $"WHERE created_at_utc >= datetime('now', '-{days} days') AND hit_errors IS NOT NULL" : "WHERE hit_errors IS NOT NULL";
            cmd.CommandText = $"SELECT hit_errors FROM plays {filter} ORDER BY created_at_utc DESC LIMIT 10000";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                try {
                    var list = JsonSerializer.Deserialize<List<double>>(reader.GetString(0));
                    if (list != null) errors.AddRange(list);
                } catch { }
            }
        } catch { }
        return errors;
    }

    public static async Task StopAsync()
    {
        if (_isStopping) return;
        _isStopping = true;
        _timer?.Stop();
        _timer?.Dispose();
        if (_hasPendingPlay)
        {
            DebugService.Log("Final sync on exit...", "Tracker");
            await SendPing();
        }
    }

    public static void Stop()
    {
        var stopTask = StopAsync();
        if (!stopTask.Wait(TimeSpan.FromSeconds(7)))
        {
            DebugService.Log("Tracker stop timed out.", "Tracker");
        }
    }
}
