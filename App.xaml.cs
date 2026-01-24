using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;

namespace OsuGrind;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private const string MutexName = "OsuGrind_SingleInstance_Mutex_Redux"; // Updated mutex name

    protected override void OnStartup(StartupEventArgs e)
    {
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
