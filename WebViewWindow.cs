using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using OsuGrind.Api;
using OsuGrind.Services;
using OsuGrind.LiveReading;
using System.Text.Json;
using System.Threading;

namespace OsuGrind;

/// <summary>
/// WebView2-based main window that hosts the HTML UI.
/// </summary>
public partial class WebViewWindow : Window
{
    private readonly WebView2 _webView;
    private readonly ApiServer _apiServer;
    private readonly TrackerDb _db;
    private readonly SoundPlayer _soundPlayer;
    private readonly IOsuMemoryReader _osuReader;
    private readonly CancellationTokenSource _cts;

    private Task? _gameLoopTask;

    public WebViewWindow()
    {
        // Core setup
        Title = "Osu!Grind";
        Width = 1150;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ResizeMode = ResizeMode.CanResize;
        Background = System.Windows.Media.Brushes.Black;

        // Initialize services
        _db = new TrackerDb();
        _soundPlayer = new SoundPlayer();
        _osuReader = new UnifiedOsuReader(_db, _soundPlayer);
        _osuReader.OnPlayRecorded += (success) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (_webView?.CoreWebView2 != null)
                {
                    var json = JsonSerializer.Serialize(new { type = "playRecorded", success = success });
                    _webView.CoreWebView2.PostWebMessageAsJson(json);
                }
            });
        };
        _apiServer = new ApiServer(_db);

        _cts = new CancellationTokenSource();

        // Forward global debug logs to WebView console AND WebSocket clients
        DebugService.OnMessageLogged += (msg, source, level) =>
        {
            try
            {
                // Broadcast to remote clients (browser subagent)
                _ = _apiServer.BroadcastLog(msg, level);

                // Send to local WebView
                Dispatcher.Invoke(() =>
                {
                    if (_webView?.CoreWebView2 != null)
                    {
                        var json = JsonSerializer.Serialize(new { type = "log", message = msg, level = level, source = source });
                        _webView.CoreWebView2.PostWebMessageAsJson(json);
                    }
                });
            }
            catch { /* Ignore errors */ }
        };


        // Create WebView2
        _webView = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(10, 10, 10),
            Margin = new Thickness(6) // Leave space for resize borders
        };
        Content = _webView;

        // Custom Window Chrome (removes white bar, keeps resize)
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false
        });

        // Event handlers
        Loaded += WebViewWindow_Loaded;
        Closing += WebViewWindow_Closing;
        SourceInitialized += (s, e) => 
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        };
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0084) // WM_NCHITTEST
        {
            var mouseX = (int)(short)(lParam.ToInt64() & 0xFFFF);
            var mouseY = (int)(short)((lParam.ToInt64() >> 16) & 0xFFFF);

            var border = 8; // Resize border thickness
            var result = IntPtr.Zero;
            var windowRect = new Rect(Left, Top, Width, Height);
            
            // Adjust for DPI if needed, but for now simple logic:
            // Since this is a borderless window, we need to map screen coordinates to window relative
            // But we can just use the window position.
            
            // Actually, let's use a simpler approach: 
            // If the mouse is close to the edge, return resize handles.
            
            // Get window rectangle
            GetWindowRect(hwnd, out var rect);
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            var x = mouseX - rect.Left;
            var y = mouseY - rect.Top;

            if (x < border && y < border) result = (IntPtr)13; // HTTOPLEFT
            else if (x > width - border && y < border) result = (IntPtr)14; // HTTOPRIGHT
            else if (x < border && y > height - border) result = (IntPtr)16; // HTBOTTOMLEFT
            else if (x > width - border && y > height - border) result = (IntPtr)17; // HTBOTTOMRIGHT
            else if (x < border) result = (IntPtr)10; // HTLEFT
            else if (x > width - border) result = (IntPtr)11; // HTRIGHT
            else if (y < border) result = (IntPtr)12; // HTTOP
            else if (y > height - border) result = (IntPtr)15; // HTBOTTOM
            
            if (result != IntPtr.Zero)
            {
                handled = true;
                return result;
            }
        }
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private async void WebViewWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Initialize database
            await _db.MigrateAsync();
            
            // Start API server
            _apiServer.Start();

            // Initialize WebView2
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OsuGrind", "WebView2"
            );
            Directory.CreateDirectory(userDataFolder);

            var options = new CoreWebView2EnvironmentOptions("--remote-debugging-port=9222");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
            await _webView.EnsureCoreWebView2Async(env);

            var skinsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rewind", "Skins");
            Directory.CreateDirectory(skinsRoot);
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "osugrind-skins.local",
                skinsRoot,
                CoreWebView2HostResourceAccessKind.Allow);
 
             // Configure WebView2

            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

            // Handle messages from JavaScript
            _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            // Navigate to the local server
            _webView.CoreWebView2.Navigate($"http://localhost:{_apiServer.Port}/");

            // Start game loop
            _gameLoopTask = Task.Run(GameLoop);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to initialize: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            DebugService.Log($"[WebViewWindow] WebMessageReceived - Raw: {e.WebMessageAsJson}");
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var message = JsonSerializer.Deserialize<WebMessage>(e.WebMessageAsJson, options);
            DebugService.Log($"[WebViewWindow] Deserialized - Action: {message?.Action}, Url: {message?.Url}");
            if (message == null) return;

            Dispatcher.Invoke(() =>
            {
                switch (message.Action)
                {
                    case "startDrag":
                        try 
                        {
                            ReleaseCapture();
                            SendMessage(new System.Windows.Interop.WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
                        }
                        catch { /* Ignore */ }
                        break;
                    case "minimize":
                        WindowState = WindowState.Minimized;
                        break;
                    case "maximize":
                        WindowState = WindowState == WindowState.Maximized 
                            ? WindowState.Normal 
                            : WindowState.Maximized;
                        break;
                    case "close":
                        Close();
                        break;
                    case "openAuth":
                        DebugService.Log($"[WebViewWindow] Received openAuth message, URL: {message.Url}");
                        if (!string.IsNullOrEmpty(message.Url))
                        {
                            try
                            {
                                DebugService.Log($"[WebViewWindow] Opening URL in default browser: {message.Url}");
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = message.Url,
                                    UseShellExecute = true
                                });
                                DebugService.Log("[WebViewWindow] Process.Start completed");
                            }
                            catch (Exception ex)
                            {
                                DebugService.Log($"[WebViewWindow] Error opening URL: {ex.Message}");
                            }
                        }
                        else
                        {
                            DebugService.Log("[WebViewWindow] openAuth received but URL is empty");
                        }
                        break;
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainWindow] Message error: {ex.Message}");
        }
    }

    private async Task GameLoop()
    {
        var lastBroadcast = DateTime.UtcNow;
        var broadcastInterval = TimeSpan.FromMilliseconds(100);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // Tick memory reader
                var snapshot = _osuReader.GetStats();

                // Broadcast live data at interval
                if (DateTime.UtcNow - lastBroadcast >= broadcastInterval)
                {
                    var status = "disconnected";
                    if (_osuReader.IsConnected) status = "connected";
                    else if (_osuReader.IsScanning) status = "connecting";

                    var liveData = new
                    {
                        connectionStatus = status,
                        gameName = _osuReader.ProcessName,
                        mapName = snapshot?.Beatmap ?? "Searching...",
                        artist = snapshot?.Artist ?? "—",
                        title = snapshot?.Title ?? "Searching for game process...",
                        version = snapshot?.Version ?? "",
                        accuracy = snapshot?.Accuracy ?? 1.0,
                        combo = snapshot?.Combo ?? 0,
                        score = snapshot?.Score ?? 0,
                        pp = snapshot?.PP ?? 0,
                        ppIfFc = snapshot?.PPIfFC ?? 0,
                        grade = snapshot?.Grade ?? "SS",
                        mods = snapshot?.ModsList?.ToArray() ?? new string[] { "NM" },
                        hitCounts = new
                        {
                            great = snapshot?.HitCounts?.Count300 ?? 0,
                            ok = snapshot?.HitCounts?.Count100 ?? 0,
                            meh = snapshot?.HitCounts?.Count50 ?? 0,
                            miss = snapshot?.HitCounts?.Misses ?? 0
                        },
                        cs = snapshot?.CS ?? 0,
                        ar = snapshot?.AR ?? 0,
                        od = snapshot?.OD ?? 0,
                        hp = snapshot?.HP ?? 0,
                        liveHP = snapshot?.LiveHP,
                        stars = snapshot?.Stars ?? 0,
                        bpm = snapshot?.BPM ?? 0,
                        playState = snapshot?.PlayState ?? "Idle",
                        currentTime = (snapshot?.TimeMs ?? 0) / 1000.0,
                        totalTime = (snapshot?.TotalTimeMs ?? 0) / 1000.0,
                        maxCombo = snapshot?.MaxCombo ?? 0,
                        mapMaxCombo = snapshot?.MaxCombo ?? 0,
                        totalObjects = snapshot?.TotalObjects ?? 0,

                        backgroundPath = !string.IsNullOrEmpty(snapshot?.BackgroundHash) 
                            ? $"/api/background/{snapshot.BackgroundHash}" 
                            : null,
                        mentality = 50 
                    };


                    await _apiServer.BroadcastLiveData(liveData);
                    lastBroadcast = DateTime.UtcNow;
                }

                // 4. Update dynamic tick rate based on activity
                bool isPlaying = snapshot?.IsPlaying ?? false;
                bool isResults = snapshot?.StateNumber == 7;
                int tickDelay = (isPlaying || isResults) ? 16 : 66; // 60Hz during action, 15Hz idle

                await Task.Delay(tickDelay); // Dynamic frequency
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameLoop] Error: {ex.Message}");
                await Task.Delay(1000);
            }
        }
    }




    private void WebViewWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _cts.Cancel();
        _gameLoopTask?.Wait(TimeSpan.FromSeconds(2));
        _apiServer.Stop();
        _osuReader.Dispose();
        // _db.Dispose(); // TrackerDb handles its own connections

    }

    private class WebMessage
    {
        public string? Action { get; set; }
        public string? Url { get; set; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HT_CAPTION = 0x2;
}
