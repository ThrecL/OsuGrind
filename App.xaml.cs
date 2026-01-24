using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

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

        // Check for existing instance

        _mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            Current.Shutdown();
            return;
        }

        base.OnStartup(e);

        // Launch the WebView2-based window
        var mainWindow = new WebViewWindow();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
