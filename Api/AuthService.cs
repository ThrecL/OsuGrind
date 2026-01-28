using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OsuGrind.Services;

namespace OsuGrind.Api;

public class AuthService
{
    private const int ClientId = 47034;
    private const string RedirectUri = "http://localhost:7777/callback"; 
    private const string TokenEndpoint = "https://osugrind.app/auth/token";
    
    private readonly HttpClient _http = new();

    public string GetAuthUrl()
    {
        var url = $"https://osu.ppy.sh/oauth/authorize?client_id={ClientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&response_type=code&scope=public%20identify";
        DebugService.Log($"Auth URL generated: {url}", "AuthService");
        return url;
    }

    public async Task<string?> ExchangeCodeForTokenAsync(string code)
    {
        try 
        {
            var payload = new { code, redirect_uri = RedirectUri };
            var jsonContent = new StringContent(
                JsonSerializer.Serialize(payload), 
                Encoding.UTF8, 
                "application/json");

            var response = await _http.PostAsync(TokenEndpoint, jsonContent);
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
        }
        catch (Exception ex)
        {
            DebugService.Error($"Auth Error: {ex.Message}");
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
