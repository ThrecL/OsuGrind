using System;
using OsuGrind.Models;

namespace OsuGrind.LiveReading
{
    public interface IOsuMemoryReader : IDisposable
    {
        bool IsConnected { get; }
        bool IsScanning { get; }
        string ProcessName { get; } // e.g. "Lazer" or "Stable"

        void Initialize();
        LiveSnapshot GetStats();
        void SetDebugLogging(bool enabled);

        // Fired when a play is completed and recorded
        public event Action<bool>? OnPlayRecorded;

        // The snapshot of the last recorded play (for metadata access)
        LiveSnapshot? LastRecordedSnapshot { get; }

        string? TryGetBeatmapPath(string md5);
    }
}
