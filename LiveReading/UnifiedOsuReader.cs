using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using OsuGrind.Models;
using OsuGrind.Services;
using OsuGrind.Api;

namespace OsuGrind.LiveReading
{
    public class UnifiedOsuReader : IOsuMemoryReader
    {
        private readonly LazerMemoryReader _lazerReader;
        private readonly StableMemoryReader _stableReader;
        private IOsuMemoryReader? _activeReader;
        private readonly TrackerDb _db;
        private readonly SoundPlayer _soundPlayer;

        public bool IsConnected => _activeReader?.IsConnected ?? false;
        public bool IsScanning => !IsConnected; // Always "connecting" if not connected
        public string ProcessName => IsConnected ? (_activeReader?.ProcessName ?? "Osu") : "Searching";

        public event Action<bool>? OnPlayRecorded;
        public LiveSnapshot? LastRecordedSnapshot => _activeReader?.LastRecordedSnapshot;

        public UnifiedOsuReader(TrackerDb db, SoundPlayer soundPlayer, ApiServer api)
        {
            _db = db;
            _soundPlayer = soundPlayer;
            _lazerReader = new LazerMemoryReader(db, soundPlayer, api);
            _stableReader = new StableMemoryReader(db, soundPlayer, api);
            
            _lazerReader.OnPlayRecorded += (success) => {
                OnPlayRecorded?.Invoke(success);
                if (success) TrackerService.OnPlayFinished();
            };
            _stableReader.OnPlayRecorded += (success) => {
                OnPlayRecorded?.Invoke(success);
                if (success) TrackerService.OnPlayFinished();
            };
        }

        public void Initialize()
        {
            // If we have an active reader and it's still connected, just tick it
            if (_activeReader != null && _activeReader.IsConnected)
            {
                _activeReader.Initialize();
                return;
            }

            DebugService.Throttled("unified-init", "[UnifiedReader] Initialize called - no active reader or disconnected", "UnifiedReader");

            // Detect Lazer
            _lazerReader.Initialize();
            if (_lazerReader.IsConnected)
            {
                _activeReader = _lazerReader;
                DebugService.Log("[UnifiedReader] Connected to Lazer", "UnifiedReader");
                return;
            }

            // Detect Stable
            _stableReader.Initialize();
            if (_stableReader.IsConnected)
            {
                _activeReader = _stableReader;
                DebugService.Log("[UnifiedReader] Connected to Stable", "UnifiedReader");
                return;
            }

            _activeReader = null;
        }

        public LiveSnapshot GetStats()
        {
            if (_activeReader == null || !_activeReader.IsConnected)
            {
                Initialize();
            }

            if (_activeReader != null)
            {
                return _activeReader.GetStats();
            }

            return new LiveSnapshot { StateNumber = -1 };
        }

        public void SetDebugLogging(bool enabled)
        {
            _lazerReader.SetDebugLogging(enabled);
            _stableReader.SetDebugLogging(enabled);
        }

        public string? TryGetBeatmapPath(string md5)
        {
            return _activeReader?.TryGetBeatmapPath(md5);
        }

        public void Dispose()
        {
            _lazerReader.Dispose();
            _stableReader.Dispose();
        }
    }
}
