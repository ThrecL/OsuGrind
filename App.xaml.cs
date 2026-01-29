using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

using OsuGrind.Services; // Add this

namespace OsuGrind;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private const string MutexName = "OsuGrind_SingleInstance_Mutex_Redux"; // Updated mutex name
    private const string AppId = "OsuGrind.Analytics.v1";

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Set AppUserModelID to unify Taskbar and Volume Mixer entries
        try { SetCurrentProcessExplicitAppUserModelID(AppId); } catch { }

        // Force kill any lingering instances of OsuGrind and its sub-processes
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            // Kill by name to be thorough, including WebView2 children
            var existingOsuGrind = Process.GetProcessesByName(currentProcess.ProcessName)
                .Where(p => p.Id != currentProcess.Id);
            
            foreach (var p in existingOsuGrind)
            {
                try { p.Kill(true); p.WaitForExit(2000); } catch { }
            }
        }
        catch { }

        // Check for existing instance (Mutex fallback)
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Current.Shutdown();
            return;
        }

        base.OnStartup(e);

        // Start Tracking (will wait for DB to be set in MainWindow or handle nulls gracefully)
        // We defer Start() to WebViewWindow_Loaded where we have the DB instance
        
        // Launch the WebView2-based window
        var mainWindow = new WebViewWindow();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { TrackerService.Stop(); } catch { }
        
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        
        base.OnExit(e);
        
        // Force the process to terminate, killing all remaining threads
        Environment.Exit(0);
    }
}

