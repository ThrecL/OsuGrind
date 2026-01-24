using System;
using System.Windows.Threading;

namespace OsuGrind.Services;

internal sealed class UiTimer : IDisposable
{
    private readonly DispatcherTimer timer;

    public UiTimer(TimeSpan interval, Action tick)
    {
        timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = interval,
        };
        timer.Tick += (_, __) => tick();
    }

    public void Start() => timer.Start();
    public void Stop() => timer.Stop();

    public void Dispose()
    {
        try { timer.Stop(); } catch { }
    }
}





