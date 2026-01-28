using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;

namespace OsuGrind.Services;

public class TrackerService
{
    private static readonly HttpClient _client = new HttpClient();
    private static System.Timers.Timer? _timer;
    private const string TrackerUrl = "https://osugrind-tracker.3cl-exe.workers.dev/ping";

    public static void Start()
    {
        _timer = new System.Timers.Timer(5 * 60 * 1000); // 5 minutes
        _timer.Elapsed += async (s, e) => await SendPing();
        _timer.AutoReset = true;
        _timer.Start();
        
        // Initial ping
        Task.Run(SendPing);
    }

    private static async Task SendPing()
    {
        try
        {
            var payload = new
            {
                userId = SettingsManager.Current.UniqueId,
                version = "1.0.0" 
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

    public static void Stop()
    {
        _timer?.Stop();
        _timer?.Dispose();
    }
}
