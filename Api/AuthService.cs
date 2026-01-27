using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using OsuGrind.Services;

namespace OsuGrind.Api;

public class AuthService
{
    private static int ClientId;
    private static string ClientSecret = "";
    private const string RedirectUri = "http://localhost:7777/callback"; 

    static AuthService()
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "secrets.json");
            if (!File.Exists(path)) path = Path.Combine(Directory.GetCurrentDirectory(), "secrets.json");
            
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement.GetProperty("OsuOAuth");
                ClientId = root.GetProperty("ClientId").GetInt32();
                ClientSecret = root.GetProperty("ClientSecret").GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            DebugService.Error($"Failed to load secrets: {ex.Message}");
        }
    }
    
    private readonly HttpClient _http = new();

    public string GetAuthUrl()
    {
        return $"https://osu.ppy.sh/oauth/authorize?client_id={ClientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&response_type=code&scope=public%20identify";
    }

    public async Task<string?> ExchangeCodeForTokenAsync(string code)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", ClientId.ToString()),
            new KeyValuePair<string, string>("client_secret", ClientSecret),
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri)
        });

        var response = await _http.PostAsync("https://osu.ppy.sh/oauth/token", content);
        if (!response.IsSuccessStatusCode)
        {
            DebugService.Error($"Token exchange failed: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("access_token", out var token))
        {
            return token.GetString();
        }
        return null;
    }

    public async Task<JsonElement?> GetUserProfileAsync(string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "https://osu.ppy.sh/api/v2/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        var response = await _http.SendAsync(req);
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        return null;
    }

    public async Task<JsonElement?> GetUserTopScoresAsync(string token, int userId, int limit = 100)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"https://osu.ppy.sh/api/v2/users/{userId}/scores/best?limit={limit}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        var response = await _http.SendAsync(req);
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        return null;
    }
}
