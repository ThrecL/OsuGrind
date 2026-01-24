using System;
using Microsoft.Win32;
using System.IO;
using System.Diagnostics;

namespace OsuGrind.Services;

public static class ProtocolService
{
    private const string ProtocolName = "osugrind";

    public static void Register()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            // Register in HKCU to avoid requiring admin privileges for registration
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProtocolName}");
            key.SetValue("", $"URL:{ProtocolName} Protocol");
            key.SetValue("URL Protocol", "");

            using var iconKey = key.CreateSubKey("DefaultIcon");
            iconKey.SetValue("", $"{exePath},0");

            using var commandKey = key.CreateSubKey(@"shell\open\command");
            commandKey.SetValue("", $"\"{exePath}\" \"%1\"");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to register protocol: {ex.Message}");
        }
    }
}
