using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;
using System.IO;

namespace OsuGrind.Services;

public class UpdateService
{
    private const string RepoOwner = "ThrecL";
    private const string RepoName = "OsuGrind";
    private const string CurrentVersion = "1.0.2"; // MUST MATCH app.js/Settings

    private static readonly HttpClient _client = new HttpClient();

    public class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";
        
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = new();
    }

    public class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        try
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("OsuGrind-Updater");
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            
            var response = await _client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new UpdateCheckResult { Available = false };

            var json = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);

            if (release == null) return new UpdateCheckResult { Available = false };

            string latestTag = release.TagName.TrimStart('v');
            if (IsNewer(latestTag, CurrentVersion))
            {
                // Find the first .zip asset
                var zipAsset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                
                return new UpdateCheckResult
                {
                    Available = true,
                    LatestVersion = release.TagName,
                    DownloadUrl = release.HtmlUrl,
                    ZipUrl = zipAsset?.BrowserDownloadUrl ?? ""
                };
            }
        }
        catch (Exception ex)
        {
            DebugService.Error($"Update check failed: {ex.Message}", "UpdateService");
        }

        return new UpdateCheckResult { Available = false };
    }

    public static async Task<bool> InstallUpdateAsync(string zipUrl)
    {
        if (string.IsNullOrEmpty(zipUrl)) return false;

        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "OsuGrindUpdate");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            string zipPath = Path.Combine(tempDir, "update.zip");
            
            DebugService.Log($"Downloading update from {zipUrl}...", "Updater");
            var bytes = await _client.GetByteArrayAsync(zipUrl);
            await File.WriteAllBytesAsync(zipPath, bytes);

            string installDir = AppDomain.CurrentDomain.BaseDirectory;
            string psScriptPath = Path.Combine(tempDir, "install.ps1");

            // Create PowerShell script to handle file replacement after exit
            string psContent = $"""
                Start-Sleep -Seconds 2
                Expand-Archive -Path "{zipPath}" -DestinationPath "{tempDir}\extracted" -Force
                Copy-Item -Path "{tempDir}\extracted\*" -Destination "{installDir}" -Recurse -Force
                Start-Process "{Path.Combine(installDir, "OsuGrind.exe")}"
                Remove-Item -Path "{tempDir}" -Recurse -Force
                """;

            await File.WriteAllTextAsync(psScriptPath, psContent);

            DebugService.Log("Launching updater script...", "Updater");
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{psScriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            // Signal app to close
            System.Windows.Application.Current.Dispatcher.Invoke(() => {
                System.Windows.Application.Current.Shutdown();
            });

            return true;
        }
        catch (Exception ex)
        {
            DebugService.Error($"Update installation failed: {ex.Message}", "Updater");
            return false;
        }
    }

    private static bool IsNewer(string latest, string current)
    {
        try {
            var v1 = new Version(latest);
            var v2 = new Version(current);
            return v1 > v2;
        } catch { return false; }
    }
}

public class UpdateCheckResult
{
    public bool Available { get; set; }
    public string LatestVersion { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string ZipUrl { get; set; } = "";
}
