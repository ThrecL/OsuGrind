using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using OsuGrind.Models;
using OsuGrind.Services;

namespace OsuGrind.LiveReading
{
    public class StableMemoryReader : IOsuMemoryReader
    {
        private Process? _process;
        private MemoryScanner? _scanner;
        private IntPtr _baseAddress = IntPtr.Zero;
        private IntPtr _statusPtr = IntPtr.Zero;
        private IntPtr _rulesetsAddr = IntPtr.Zero;
        private IntPtr _playTimeAddr = IntPtr.Zero;
        private IntPtr _menuModsPtr = IntPtr.Zero;
        private IntPtr _replayFlagAddrPtr = IntPtr.Zero;

        private string? _currentOsuFilePath;
        private RosuService _rosuService = new();

        private readonly TrackerDb _db;
        private readonly SoundPlayer _soundPlayer;
        private readonly StableScoreDetector _detector;
        private DateTime _lastConnectionAttempt = DateTime.MinValue;

        private int _liveTrackedMaxCombo = 0;
        private bool _hasSeenHealth = false;
        private int _lastState = -1;

        public bool IsConnected => _process != null && !_process.HasExited && _baseAddress != IntPtr.Zero;
        public bool IsScanning { get; private set; }
        public string ProcessName => "Stable";

        public event Action<bool>? OnPlayRecorded;
        public LiveSnapshot? LastRecordedSnapshot => _detector.LastSnapshot;

        public StableMemoryReader(TrackerDb db, SoundPlayer soundPlayer)
        {
            _db = db;
            _soundPlayer = soundPlayer;
            _detector = new StableScoreDetector(db, soundPlayer);
            _detector.OnPlayRecorded += (success) => OnPlayRecorded?.Invoke(success);
        }

        public void Initialize()
        {
            if (!Offsets.IsLoaded) Offsets.Load();

            if (IsConnected) return;
            if ((DateTime.Now - _lastConnectionAttempt).TotalSeconds < 5) return;
            _lastConnectionAttempt = DateTime.Now;

            var allOsuProcesses = Process.GetProcessesByName("osu!").Concat(Process.GetProcessesByName("osu")).ToList();
            
            foreach (var p in allOsuProcesses)
            {
                bool is32Bit = false;
                try { if (Win32.IsWow64Process(p.Handle, out bool isWow64)) is32Bit = isWow64; } catch { continue; }

                if (is32Bit)
                {
                    _process = p;
                    _scanner = new MemoryScanner(_process);
                    UpdateAddresses();
                    if (_baseAddress != IntPtr.Zero) 
                    {
                        DebugService.Log($"[Stable] Connected. Base: {_baseAddress:X}, Status: {_statusPtr:X}", "StableReader");
                        break;
                    }
                }
            }
        }

        private void UpdateAddresses()
        {
            if (_scanner == null) return;
            IsScanning = true;
            try
            {
                // Signatures from OsuMemoryDataProvider
                _baseAddress = _scanner.Scan("F8 01 74 04 83 65");
                
                if (_baseAddress != IntPtr.Zero)
                {
                    _statusPtr = IntPtr.Add(_baseAddress, -60);
                    _playTimeAddr = IntPtr.Add(_baseAddress, 100);
                }

                _menuModsPtr = _scanner.Scan("C8 FF ?? ?? ?? ?? ?? 81 0D ?? ?? ?? ?? ?? 08 00 00");
                if (_menuModsPtr != IntPtr.Zero) _menuModsPtr = IntPtr.Add(_menuModsPtr, 9);

                IntPtr replaySigAddr = _scanner.Scan("8B FA B8 01 00 00 00");
                if (replaySigAddr != IntPtr.Zero)
                    _replayFlagAddrPtr = IntPtr.Add(replaySigAddr, 0x2A);

                _rulesetsAddr = _scanner.Scan("C7 86 48 01 00 00 01 00 00 00 A1");
                if (_rulesetsAddr != IntPtr.Zero) _rulesetsAddr = IntPtr.Add(_rulesetsAddr, 11);
            }
            catch (Exception ex) { DebugService.Log($"UpdateAddresses Error: {ex.Message}", "Error"); }
            finally { IsScanning = false; }
        }

        public LiveSnapshot GetStats()
        {
            if (!IsConnected) return new LiveSnapshot { StateNumber = -1 };
            var snapshot = new LiveSnapshot { StateNumber = 0 };
            try
            {
                // Read State
                if (_statusPtr != IntPtr.Zero)
                {
                    IntPtr statePtr = _scanner!.ReadIntPtr(_statusPtr);
                    if (statePtr != IntPtr.Zero) snapshot.StateNumber = _scanner.ReadInt32(statePtr);
                }

                if (snapshot.StateNumber == 2 && _lastState != 2)
                {
                    _liveTrackedMaxCombo = 0;
                    _hasSeenHealth = false;
                }
                _lastState = snapshot.StateNumber;

                // Replay detection
                if (_replayFlagAddrPtr != IntPtr.Zero)
                {
                    IntPtr flagPtr = _scanner!.ReadIntPtr(_replayFlagAddrPtr);
                    if (flagPtr != IntPtr.Zero) snapshot.IsReplay = _scanner.ReadByte(flagPtr) == 1;
                }

                // Beatmap
                IntPtr beatmapPtrAddr = _scanner!.ReadIntPtr(IntPtr.Add(_baseAddress, -12));
                IntPtr beatmapPtr = _scanner!.ReadIntPtr(beatmapPtrAddr);

                if (beatmapPtr != IntPtr.Zero)
                {
                    snapshot.Artist = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(beatmapPtr, 0x18)));
                    snapshot.Title = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(beatmapPtr, 0x24)));
                    snapshot.Version = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(beatmapPtr, 0xAC)));
                    snapshot.Beatmap = $"{snapshot.Artist} - {snapshot.Title} [{snapshot.Version}]";
                    snapshot.MD5Hash = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(beatmapPtr, 0x6C)));

                    string stablePath = SettingsManager.Current.StablePath ?? "";
                    if (string.IsNullOrEmpty(stablePath))
                    {
                        try { 
                            if (_process != null && _process.MainModule != null)
                                stablePath = Path.GetDirectoryName(_process.MainModule.FileName) ?? ""; 
                        } catch { }
                    }
                    if (!string.IsNullOrEmpty(stablePath)) snapshot.OsuFolder = stablePath;

                    string folder = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(beatmapPtr, 0x78)));
                    string filename = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(beatmapPtr, 0x90)));
                    string background = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(beatmapPtr, 0x68)));

                    if (!string.IsNullOrEmpty(stablePath) && !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(filename))
                    {
                        string fullPath = Path.Combine(stablePath, "Songs", folder, filename);
                        if (File.Exists(fullPath))
                        {
                            if (fullPath != _currentOsuFilePath) { 
                                _currentOsuFilePath = fullPath; 
                                _rosuService.UpdateContext(fullPath); 
                            }
                            snapshot.MapPath = fullPath;
                            string bgPath = Path.Combine(stablePath, "Songs", folder, background);
                            if (File.Exists(bgPath)) 
                            {
                                snapshot.BackgroundPath = bgPath;
                                snapshot.BackgroundHash = "STABLE:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(bgPath));
                            }
                        }
                    }
                    else snapshot.MapPath = _currentOsuFilePath;

                    // Mods
                    uint modsBits = 0;
                    bool playingModsFound = false;

                    if (snapshot.StateNumber == 2 || snapshot.StateNumber == 7)
                    {
                        try
                        {
                            IntPtr rulesetPtr = _scanner!.ReadIntPtr(_rulesetsAddr);
                            IntPtr rulesetAddr = _scanner!.ReadIntPtr(IntPtr.Add(rulesetPtr, 4));
                            if (rulesetAddr != IntPtr.Zero)
                            {
                                IntPtr playDataBase = IntPtr.Zero;
                                if (snapshot.StateNumber == 7) playDataBase = _scanner.ReadIntPtr(IntPtr.Add(rulesetAddr, 0x38));
                                else {
                                    IntPtr gameplayBase = _scanner.ReadIntPtr(IntPtr.Add(rulesetAddr, 104));
                                    if (gameplayBase != IntPtr.Zero) playDataBase = _scanner.ReadIntPtr(IntPtr.Add(gameplayBase, 56));
                                }

                                if (playDataBase != IntPtr.Zero)
                                {
                                    IntPtr modsClassPtr = _scanner.ReadIntPtr(IntPtr.Add(playDataBase, 0x1C));
                                    if (modsClassPtr != IntPtr.Zero)
                                    {
                                        int val1 = _scanner.ReadInt32(IntPtr.Add(modsClassPtr, 0x08));
                                        int val2 = _scanner.ReadInt32(IntPtr.Add(modsClassPtr, 0x0C));
                                        modsBits = (uint)(val1 ^ val2);
                                        playingModsFound = true;
                                    }
                                }
                            }
                        } catch { }
                    }

                    if (!playingModsFound && _menuModsPtr != IntPtr.Zero)
                    {
                        IntPtr modsAddr = _scanner.ReadIntPtr(_menuModsPtr);
                        if (modsAddr != IntPtr.Zero) modsBits = (uint)_scanner.ReadInt32(modsAddr);
                    }

                    snapshot.ModsList = ParseMods(modsBits, true);
                    snapshot.Mods = string.Join("", snapshot.ModsList);

                    if (_rosuService != null && _rosuService.IsLoaded)
                    {
                        uint rosuMods = RosuService.ModsToRosuStats(snapshot.ModsList);
                        double clockRate = RosuService.GetClockRateFromMods(rosuMods);
                        var attrs = _rosuService.GetDifficultyAttributes(rosuMods, clockRate);
                        snapshot.AR = attrs.AR; snapshot.CS = attrs.CS; snapshot.OD = attrs.OD; snapshot.HP = attrs.HP;
                        snapshot.Stars = _rosuService.GetStars(rosuMods, 0, clockRate);
                        var ppFcRes = _rosuService.CalculatePpIfFc(_currentOsuFilePath ?? "", snapshot.ModsList, 100.0, -1, -1, -1, -1, clockRate);
                        snapshot.PPIfFC = ppFcRes.PP;
                        snapshot.MostlyBPM = (int)Math.Round(_rosuService.BaseBpm * clockRate);
                        snapshot.BPM = snapshot.MostlyBPM;
                        snapshot.MapMaxCombo = ppFcRes.MaxCombo;
                        snapshot.MaxScore = _rosuService.CalculateStableMaxScore(_currentOsuFilePath ?? "", snapshot.ModsList, snapshot.MapMaxCombo, clockRate);
                        snapshot.TotalObjects = _rosuService.TotalObjects; 
                        if (ppFcRes.MapLength > 0) snapshot.TotalTimeMs = (int)(ppFcRes.MapLength / clockRate);

                        if (snapshot.StateNumber == 5 || snapshot.StateNumber == 0)
                        {
                            snapshot.IsPreview = true;
                            snapshot.PP = ppFcRes.PP; snapshot.Combo = ppFcRes.MaxCombo; snapshot.MaxCombo = ppFcRes.MaxCombo;
                            snapshot.Score = snapshot.MaxScore; snapshot.Accuracy = 1.0;
                            snapshot.HitCounts = new HitCounts(_rosuService.TotalObjects, 0, 0, 0);
                            snapshot.Grade = snapshot.ModsList.Contains("HD") || snapshot.ModsList.Contains("FL") ? "SSH" : "SS";
                            snapshot.TimeMs = snapshot.TotalTimeMs;
                        }
                    }
                }

                if (snapshot.StateNumber == 2 || snapshot.StateNumber == 7) // Playing or Results
                {
                    snapshot.IsPlaying = snapshot.StateNumber == 2;
                    snapshot.Passed = snapshot.StateNumber == 7;

                    if (snapshot.IsPlaying && _playTimeAddr != IntPtr.Zero)
                    {
                        IntPtr timePtr = _scanner!.ReadIntPtr(_playTimeAddr);
                        if (timePtr != IntPtr.Zero) snapshot.TimeMs = _scanner.ReadInt32(IntPtr.Subtract(timePtr, 16));
                    }

                    if (_rulesetsAddr != IntPtr.Zero)
                    {
                        IntPtr rulesetPtr = _scanner!.ReadIntPtr(_rulesetsAddr);
                        IntPtr rulesetAddr = _scanner!.ReadIntPtr(IntPtr.Add(rulesetPtr, 4));
                        if (rulesetAddr != IntPtr.Zero)
                        {
                            if (snapshot.StateNumber == 7) // Results
                            {
                                IntPtr resBase = _scanner.ReadIntPtr(IntPtr.Add(rulesetAddr, 0x38));
                                if (resBase != IntPtr.Zero)
                                {
                                    int r300 = _scanner.ReadUInt16(IntPtr.Add(resBase, 0x8a));
                                    int r100 = _scanner.ReadUInt16(IntPtr.Add(resBase, 0x88));
                                    int r50 = _scanner.ReadUInt16(IntPtr.Add(resBase, 0x8c));
                                    int rMiss = _scanner.ReadUInt16(IntPtr.Add(resBase, 0x92));
                                    snapshot.HitCounts = new HitCounts(r300, r100, r50, rMiss);
                                    snapshot.MaxCombo = _scanner.ReadUInt16(IntPtr.Add(resBase, 0x68));
                                    
                                    uint score1 = _scanner.ReadXorInt32(IntPtr.Add(resBase, 0x78));
                                    uint score2 = _scanner.ReadXorInt32(IntPtr.Add(resBase, 0x48));
                                    snapshot.Score = (score1 > 0 && score1 < 2147483647) ? (long)score1 : (long)score2;


                                    snapshot.Combo = snapshot.MaxCombo;
                                    int totalHits = r300 + r100 + r50 + rMiss;
                                    if (totalHits > 0) snapshot.Accuracy = (double)(r300 * 300 + r100 * 100 + r50 * 50) / (totalHits * 300);
                                    snapshot.Grade = GetGrade(snapshot.HitCounts, snapshot.ModsList);
                                    if (totalHits > 0 && (snapshot.Score ?? 0) > 0) snapshot.IsResultsReady = true;
                                }
                            }
                            else // Playing
                            {
                                IntPtr gameplayBase = _scanner.ReadIntPtr(IntPtr.Add(rulesetAddr, 104));
                                if (gameplayBase != IntPtr.Zero)
                                {
                                    IntPtr scoreBase = _scanner.ReadIntPtr(IntPtr.Add(gameplayBase, 56));
                                    if (scoreBase != IntPtr.Zero)
                                    {
                                        int h300 = _scanner.ReadUInt16(IntPtr.Add(scoreBase, 138));
                                        int h100 = _scanner.ReadUInt16(IntPtr.Add(scoreBase, 136));
                                        int h50 = _scanner.ReadUInt16(IntPtr.Add(scoreBase, 140));
                                        int miss = _scanner.ReadUInt16(IntPtr.Add(scoreBase, 146));
                                        snapshot.HitCounts = new HitCounts(h300, h100, h50, miss);
                                        
                                        int combo = _scanner.ReadUInt16(IntPtr.Add(scoreBase, 148));
                                        int passedObjects = h300 + h100 + h50 + miss;
                                        snapshot.Combo = passedObjects > 0 ? combo : 0;
                                        
                                        uint gScore1 = _scanner.ReadXorInt32(IntPtr.Add(scoreBase, 120));
                                        uint gScore2 = _scanner.ReadXorInt32(IntPtr.Add(scoreBase, 72)); // 0x48
                                        snapshot.Score = (gScore1 > 0 && gScore1 < 2147483647) ? (long)gScore1 : (long)gScore2;
                                        

                                        int currentCombo = snapshot.Combo ?? 0;
                                        if (currentCombo > _liveTrackedMaxCombo) _liveTrackedMaxCombo = currentCombo;
                                        snapshot.MaxCombo = _liveTrackedMaxCombo;
                                        if (passedObjects > 0) snapshot.Accuracy = (double)(h300 * 300 + h100 * 100 + h50 * 50) / (passedObjects * 300);
                                        else snapshot.Accuracy = 1.0;
                                        snapshot.Grade = GetGrade(snapshot.HitCounts, snapshot.ModsList);
                                        IntPtr hpBase = _scanner.ReadIntPtr(IntPtr.Add(gameplayBase, 64));
                                        if (hpBase != IntPtr.Zero)
                                        {
                                            snapshot.LiveHP = _scanner.ReadDouble(IntPtr.Add(hpBase, 0x1C));
                                            if (snapshot.LiveHP > 0.01) _hasSeenHealth = true;
                                            snapshot.Failed = _hasSeenHealth && snapshot.LiveHP <= 0.0001;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (snapshot.HitCounts != null && _rosuService != null && _rosuService.IsLoaded)
                    {
                        uint rosuMods = RosuService.ModsToRosuStats(snapshot.ModsList);
                        double clockRate = RosuService.GetClockRateFromMods(rosuMods);
                        int passed = snapshot.HitCounts.Count300 + snapshot.HitCounts.Count100 + snapshot.HitCounts.Count50 + snapshot.HitCounts.Misses;
                        if (passed == 0) snapshot.PP = 0;
                        else
                        {
                            double ratio = _rosuService.TotalObjects > 0 ? (double)passed / _rosuService.TotalObjects : 0;
                            int sliderEnds = (int)Math.Round(ratio * _rosuService.TotalSliders);
                            snapshot.PP = _rosuService.CalculatePp(rosuMods, snapshot.MaxCombo ?? 0, snapshot.HitCounts.Count300, snapshot.HitCounts.Count100, snapshot.HitCounts.Count50, snapshot.HitCounts.Misses, passed, sliderEndHits: sliderEnds, clockRate: clockRate);
                        }
                    }
                }
                _detector.Process(snapshot);
            }
            catch (Exception ex) { DebugService.Log($"StableReader Error: {ex.Message}", "Error"); }
            return snapshot;
        }

        private string GetGrade(HitCounts hits, List<string>? mods)
        {
            if (hits == null) return "SS";
            int total = hits.Count300 + hits.Count100 + hits.Count50 + hits.Misses;
            if (total == 0) return "SS";
            double r300 = (double)hits.Count300 / total;
            if (hits.Misses > 0) return r300 > 0.9 ? "A" : r300 > 0.8 ? "B" : r300 > 0.7 ? "C" : "D";
            if (hits.Count300 == total) return mods != null && (mods.Contains("HD") || mods.Contains("FL")) ? "SSH" : "SS";
            if (r300 > 0.9 && (double)hits.Count50 / total < 0.01) return mods != null && (mods.Contains("HD") || mods.Contains("FL")) ? "SH" : "S";
            return r300 > 0.8 ? "A" : r300 > 0.7 ? "B" : r300 > 0.6 ? "C" : "D";
        }

        public static List<string> ParseMods(uint bits, bool stable = true)
        {
            var mods = new List<string>();
            if ((bits & (1 << 0)) != 0) mods.Add("NF"); if ((bits & (1 << 1)) != 0) mods.Add("EZ");
            if ((bits & (1 << 2)) != 0) mods.Add("TD"); if ((bits & (1 << 3)) != 0) mods.Add("HD");
            if ((bits & (1 << 4)) != 0) mods.Add("HR"); if ((bits & (1 << 5)) != 0) mods.Add("SD");
            if ((bits & (1 << 9)) != 0) mods.Add("NC"); else if ((bits & (1 << 6)) != 0) mods.Add("DT");
            if ((bits & (1 << 7)) != 0) mods.Add("RX"); if ((bits & (1 << 8)) != 0) mods.Add("HT");
            if ((bits & (1 << 10)) != 0) mods.Add("FL"); if ((bits & (1 << 11)) != 0) mods.Add("AT");
            if ((bits & (1 << 12)) != 0) mods.Add("SO"); if ((bits & (1 << 13)) != 0) mods.Add("AP");
            if ((bits & (1 << 14)) != 0) mods.Add("PF");
            if (stable && !mods.Contains("CL")) mods.Add("CL");
            else if (mods.Count == 0) mods.Add("NM");
            return mods;
        }
        public void SetDebugLogging(bool enabled) { }
        public void Dispose() { _scanner?.Dispose(); _rosuService?.Dispose(); }
        public string? TryGetBeatmapPath(string md5) => null;
    }
}
