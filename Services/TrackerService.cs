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
    private static readonly HttpClient _client = new HttpClient();
    private static System.Timers.Timer? _timer;
    private const string TrackerUrl = "https://osugrind-tracker.3cl-exe.workers.dev/ping";
    private static TrackerDb? _db;
    private static DateTime _lastPing = DateTime.MinValue;
    private static readonly object _pingLock = new object();

    public static void Start(TrackerDb? db = null)
    {
        _db = db;
        
        // Timer for regular 5-minute pings
        _timer = new System.Timers.Timer(5 * 60 * 1000); 
        _timer.Elapsed += async (s, e) => await SendPing();
        _timer.AutoReset = true;
        _timer.Start();
        
        // Initial ping after 5 seconds to allow everything to settle
        Task.Delay(5000).ContinueWith(_ => SendPing());
    }

    public static void TriggerSync()
    {
        // Throttled manual sync (max once every 30 seconds)
        if ((DateTime.UtcNow - _lastPing).TotalSeconds < 30) return;
        Task.Run(SendPing);
    }

    private static async Task SendPing()
    {
        lock (_pingLock)
        {
            // Double check throttle inside lock
            if ((DateTime.UtcNow - _lastPing).TotalSeconds < 10) return;
            _lastPing = DateTime.UtcNow;
        }

        try
        {
            object? stats = null;
            object? graphs = null;

            if (_db != null && SettingsManager.Current.AccessToken != null)
            {
                // Fetch local analytics (Full timeline)
                var summary = await _db.GetAnalyticsSummaryAsync(0);
                var daily = await _db.GetDailyStatsAsync(0);
                var streak = await _db.GetPlayStreakAsync();
                
                // Form & Reference Calculation
                var allDaily = await _db.GetDailyStatsAsync(0);
                double referencePP = allDaily.Any() ? allDaily.Max(d => d.AvgPP) : 1;
                if (referencePP <= 0) referencePP = 1;

                var recentSummary = await _db.GetAnalyticsSummaryAsync(14); 
                double currentRatio = referencePP > 0 ? recentSummary.AvgPP / referencePP : 0;
                string form = "Stable";
                if (recentSummary.TotalPlays > 0) { if (currentRatio > 1.05) form = "Peak"; else if (currentRatio > 0.95) form = "Great"; else if (currentRatio > 0.85) form = "Stable"; else if (currentRatio > 0.70) form = "Slumping"; else form = "Burnout"; }

                // Mentality Calculation
                var mentalitySummary = await _db.GetAnalyticsSummaryAsync(3);
                double mentality = 50; 
                if (mentalitySummary.TotalPlays > 0) {
                    double passRate = (double)mentalitySummary.PassCount / mentalitySummary.TotalPlays;
                    double accFactor = mentalitySummary.AvgAccuracy;
                    double hoursPlayed = mentalitySummary.TotalDurationMs / 3600000.0;
                    double density = Math.Min(1.0, mentalitySummary.TotalPlays / (hoursPlayed * 10 + 1));
                    double baseScore = (passRate * 50.0) + (accFactor * 40.0) + (density * 10.0);
                    double multiplier = 1.0;
                    if (mentalitySummary.LastPlayedUtc.HasValue) { var lastPlayed = mentalitySummary.LastPlayedUtc.Value.ToLocalTime(); var inactivityDays = (DateTime.Now - lastPlayed).TotalDays; multiplier = Math.Pow(0.9, Math.Max(0, inactivityDays - 0.5)); }
                    double currentPerfMatch = mentalitySummary.AvgPP / referencePP;
                    double perfPenalty = 1.0; if (currentPerfMatch < 0.85) perfPenalty = 0.8; if (currentPerfMatch < 0.70) perfPenalty = 0.5; if (currentPerfMatch < 0.50) perfPenalty = 0.2;
                    
                    var goals = await _db.GetTodayGoalProgressAsync(SettingsManager.Current.GoalStars);
                    double goalPenalty = 1.0;
                    bool hasAnyGoals = SettingsManager.Current.GoalPlays > 0 || SettingsManager.Current.GoalHits > 0 || SettingsManager.Current.GoalPP > 0;
                    if (hasAnyGoals) { double hour = DateTime.Now.Hour; if (hour > 12) { double targetProgress = Math.Clamp((hour - 12) / 10.0, 0, 1.0); double actualProgress = 0; int goalCount = 0; if (SettingsManager.Current.GoalPlays > 0) { actualProgress += Math.Min(1.0, (double)goals.Plays / SettingsManager.Current.GoalPlays); goalCount++; } if (SettingsManager.Current.GoalHits > 0) { actualProgress += Math.Min(1.0, (double)goals.Hits / SettingsManager.Current.GoalHits); goalCount++; } if (SettingsManager.Current.GoalPP > 0) { actualProgress += Math.Min(1.0, goals.TotalPP / SettingsManager.Current.GoalPP); goalCount++; } double avgProgress = actualProgress / goalCount; if (avgProgress < targetProgress * 0.7) goalPenalty = 0.6; } }
                    mentality = Math.Clamp(baseScore * multiplier * perfPenalty * goalPenalty, 0, 100);
                }

                stats = new 
                {
                    totalPlays = summary.TotalPlays,
                    totalTime = (int)(summary.TotalDurationMs / 60000.0),
                    avgAcc = summary.AvgAccuracy,
                    avgPP = summary.AvgPP,
                    avgUR = summary.AvgUR,
                    form,
                    mentality
                };

                // Graph Data Compression
                var timeline = daily.Select(d => new { 
                    d = d.Date, 
                    p = d.PlayCount, 
                    acc = Math.Round(d.AvgAcc * 100.0, 2), 
                    pp = Math.Round(d.AvgPP, 1), 
                    ur = Math.Round(d.AvgUR, 2), 
                    kr = Math.Round(d.AvgKeyRatio, 3),
                    m = referencePP > 0 ? Math.Round((d.AvgPP / referencePP) * 100.0, 1) : 0
                }).ToList();

                // Calendar: Compressed dictionary of play counts (only non-zero days)
                var calendar = daily.Where(d => d.PlayCount > 0)
                                    .ToDictionary(d => d.Date, d => d.PlayCount);

                var hitErrors = await GetRecentHitErrorsAsync(0);
                var histogram = new Dictionary<string, int>();
                
                // Initialize all bins from -100 to 100 to ensure consistent chart alignment
                for (int i = -100; i <= 100; i += 2) histogram[i.ToString()] = 0;

                foreach (var err in hitErrors)
                {
                    // Match JS Math.round exactly: Math.floor(x + 0.5)
                    int bin = (int)Math.Floor(err / 2.0 + 0.5) * 2;
                    if (bin >= -100 && bin <= 100)
                    {
                        var key = bin.ToString();
                        histogram[key]++;
                    }
                }

                graphs = new { timeline, histogram, calendar, streak };
            }

            var payload = new
            {
                userId = SettingsManager.Current.UniqueId,
                token = SettingsManager.Current.AccessToken,
                version = "1.0.4",
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

    public static void Stop()
    {
        _timer?.Stop();
        _timer?.Dispose();
        // Final sync on exit
        try { SendPing().GetAwaiter().GetResult(); } catch { }
    }
}
