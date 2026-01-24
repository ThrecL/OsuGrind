using System;
using System.Collections.Generic;
using System.IO;

namespace OsuGrind.Services;

/// <summary>
/// Centralized debug logging service that respects the EnableDebugLogging setting.
/// Logs are written to the temp folder for easy access.
/// </summary>
public static class DebugService
{
    private static readonly object _logLock = new();
    private static bool _isEnabled = true;
    private static string? _logPath;
    
    // Throttling for high-frequency logs
    private static readonly Dictionary<string, DateTime> _lastLogTime = new();
    private static readonly object _throttleLock = new();
    private static TimeSpan _throttleInterval = TimeSpan.FromSeconds(1); // Default: 1 log per second per message key
    
    /// <summary>
    /// Sets the throttle interval for repeated messages.
    /// </summary>
    public static void SetThrottleInterval(TimeSpan interval)
    {
        _throttleInterval = interval;
    }
    
    /// <summary>
    /// Gets or sets whether debug logging is enabled.
    /// This should be bound to the EnableDebugLogging setting.
    /// </summary>
    public static bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            if (value)
            {
                Log("Debug logging ENABLED");
            }
        }
    }
    
    /// <summary>
    /// Gets the path to the current log file.
    /// </summary>
    public static string LogPath
    {
        get
        {
            if (_logPath == null)
            {
                var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
                Directory.CreateDirectory(tempDir);
                _logPath = Path.Combine(tempDir, "osugrind_debug.log");
            }
            return _logPath;
        }
    }
    
    /// <summary>
    /// Logs a debug message if debug logging is enabled.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="source">Optional source identifier (e.g., class name).</param>
    public static event Action<string, string, string>? OnMessageLogged;

    public static void Log(string message, string? source = null)
    {
        if (!_isEnabled) return;
        
        WriteToFile(message, source, "DEBUG");
    }
    
    /// <summary>
    /// Logs an error message. Always logs regardless of debug setting.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="source">Optional source identifier.</param>
    public static void Error(string message, string? source = null)
    {
        WriteToFile(message, source, "ERROR");
    }
    
    /// <summary>
    /// Logs an exception. Always logs regardless of debug setting.
    /// </summary>
    /// <param name="ex">The exception to log.</param>
    /// <param name="context">Optional context about what was happening.</param>
    /// <param name="source">Optional source identifier.</param>
    public static void Exception(Exception ex, string? context = null, string? source = null)
    {
        var message = context != null 
            ? $"{context}: {ex.Message}" 
            : ex.Message;
        WriteToFile(message, source, "EXCEPTION");
        
        // Also log stack trace in debug mode
        if (_isEnabled && ex.StackTrace != null)
        {
            WriteToFile(ex.StackTrace, source, "TRACE");
        }
    }
    
    /// <summary>
    /// Logs a verbose message. Only logs if debug is enabled.
    /// Use for high-frequency logging that would be too noisy normally.
    /// </summary>
    /// <param name="message">The verbose message.</param>
    /// <param name="source">Optional source identifier.</param>
    public static void Verbose(string message, string? source = null)
    {
        if (!_isEnabled) return;
        
        WriteToFile(message, source, "VERBOSE");
    }
    
    /// <summary>
    /// Logs a message with throttling - only logs once per throttle interval for the same key.
    /// Use for high-frequency state logging that floods the log file.
    /// </summary>
    /// <param name="key">Unique key to identify this log type (e.g., "GetStats-State5")</param>
    /// <param name="message">The message to log.</param>
    /// <param name="source">Optional source identifier.</param>
    public static void Throttled(string key, string message, string? source = null)
    {
        if (!_isEnabled) return;
        
        lock (_throttleLock)
        {
            var now = DateTime.Now;
            if (_lastLogTime.TryGetValue(key, out var lastTime))
            {
                if (now - lastTime < _throttleInterval)
                    return; // Skip this log
            }
            _lastLogTime[key] = now;
        }
        
        WriteToFile(message, source, "DEBUG");
    }
    
    /// <summary>
    /// Clears the log file.
    /// </summary>
    public static void ClearLog()
    {
        lock (_logLock)
        {
            try
            {
                if (File.Exists(LogPath))
                {
                    File.WriteAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Log cleared\n");
                }
            }
            catch { /* Ignore errors during clear */ }
        }
    }
    
    private static void WriteToFile(string message, string? source, string level)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var sourceTag = source != null ? $"[{source}] " : "";
        var line = $"[{timestamp}] [{level}] {sourceTag}{message}";

        // Invoke event for UI listeners (WebView console)
        OnMessageLogged?.Invoke(message, source ?? "", level);

        lock (_logLock)
        {
            try
            {
                var logPath = LogPath;
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch { /* Ignore errors during write */ }
        }
    }
}
