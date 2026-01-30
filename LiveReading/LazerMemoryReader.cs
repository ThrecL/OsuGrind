using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using OsuGrind.Models;
using OsuGrind.Services;
using OsuGrind.Api;

namespace OsuGrind.LiveReading;

public class ModSettings
{
    public double? SpeedChange { get; set; }
    public double? AR { get; set; }
    public double? CS { get; set; }
    public double? OD { get; set; }
    public double? HP { get; set; }
}

public class LazerMemoryReader : IOsuMemoryReader
{
    private ModSettings _currentModSettings = new();
    private List<string> _currentModsList = new();
    private MemoryScanner _scanner = null!;
    private IntPtr _gameBaseAddress = IntPtr.Zero;
    private Process _process = null!;
    private string? _currentBeatmapHash;
    private string? _currentOsuFilePath;
    private List<double> _objectStartTimes = new();
    private int _circles, _sliders, _spinners, _totalObjects, _lastGamemode = -1;
    private double _staticStars, _staticBpm, _minBpm, _maxBpm, _baseMinBpm, _baseMaxBpm, _baseModeBpm, _baseStars;
    private float _baseCS, _baseAR, _baseOD, _baseHP;
    public RosuService _rosuService;
    private Dictionary<IntPtr, string> _modVTableMap = new();
    private DateTime _lastConnectionAttempt = DateTime.MinValue, _lastTimeChange = DateTime.Now, _lastScreenScan = DateTime.MinValue, _lastModScan = DateTime.MinValue, _lastBeatmapInfoScan = DateTime.MinValue;
    private CachedBeatmapStats? _cachedStats;
    private IntPtr _cachedCurrentScreen = IntPtr.Zero, _lastResultScoreInfoPtr = IntPtr.Zero, _lastBeatmapInfoPtr = IntPtr.Zero;
    private RawBeatmapInfo? _cachedRawBeatmapInfo;
    private LazerScoreDetector? _detector;
    private string? _lastResolvedMd5;
    private double _lastTime;
    private bool _isPausedState = false;

    private static readonly Dictionary<int, Dictionary<string, string[]>> ModsCategories = new()
    {
        [0] = new() { ["Reduction"] = ["EZ", "NF", "HT", "DC"], ["Increase"] = ["HR", "SD", "PF", "DT", "NC", "HD", "FL", "BL", "ST", "AC"], ["Automation"] = ["AT", "CN", "RX", "AP", "SO"], ["Conversion"] = ["TP", "DA", "CL", "RD", "MR", "AL", "SG"], ["Fun"] = ["TR", "WG", "SI", "GR", "DF", "WU", "WD", "TC", "BR", "AD", "MU", "NS", "MG", "RP", "AS", "FR", "BU", "SY", "DP", "BM"], ["System"] = ["TD", "SV2"] },
        [1] = new() { ["Reduction"] = ["EZ", "NF", "HT", "DC", "SR"], ["Increase"] = ["HR", "SD", "PF", "DT", "NC", "HD", "FL", "AC"], ["Automation"] = ["AT", "CN", "RX"], ["Conversion"] = ["RD", "DA", "CL", "SW", "SG", "CS"], ["Fun"] = ["WU", "WD", "MU", "AS"], ["System"] = ["TD", "SV2"] },
        [2] = new() { ["Reduction"] = ["EZ", "NF", "HT", "DC"], ["Increase"] = ["HR", "SD", "PF", "DT", "NC", "HD", "FL", "AC"], ["Automation"] = ["AT", "CN", "RX"], ["Conversion"] = ["DA", "CL", "MR"], ["Fun"] = ["WU", "WD", "FF", "MU", "NS", "MF"], ["System"] = ["TD", "SV2"] },
        [3] = new() { ["Reduction"] = ["EZ", "NF", "HT", "DC", "NR"], ["Increase"] = ["HR", "SD", "PF", "DT", "NC", "FI", "HD", "CO", "FL", "AC"], ["Automation"] = ["AT", "CN"], ["Conversion"] = ["RD", "DS", "MR", "DA", "CL", "IN", "CS", "HO", "1K", "2K", "3K", "4K", "5K", "6K", "7K", "8K", "9K", "10K"], ["Fun"] = ["WU", "WD", "MU", "AS"], ["System"] = ["TD", "SV2"] }
    };

    private static readonly HashSet<string> KnownAcr = ["EZ", "NF", "HT", "DC", "HR", "SD", "PF", "DT", "NC", "HD", "FL", "BL", "ST", "AC", "TP", "DA", "CL", "RD", "MR", "AL", "SG", "AT", "CN", "RX", "AP", "SO", "TR", "WG", "SI", "GR", "DF", "WU", "WD", "TC", "BR", "AD", "MU", "NS", "MG", "RP", "AS", "FR", "BU", "SY", "DP", "BM", "TD", "SV2", "1K", "2K", "3K", "4K", "5K", "6K", "7K", "8K", "9K", "10K"];

    private class CachedBeatmapStats
    {
        public string MD5Hash { get; set; } = "";
        public uint RosuMods { get; set; }
        public double ClockRate, PPIfFC, Stars, MapLength;
        public float CS, AR, OD, HP;
        public int MaxCombo;
        public long MaxScore;
    }

    public LazerMemoryReader(TrackerDb db, SoundPlayer sp, ApiServer api)
    {
        _rosuService = new();
        _detector = new(db, sp, api);
    }

    public void Dispose() { _scanner?.Dispose(); _rosuService?.Dispose(); }
    public bool IsConnected => _process != null && !_process.HasExited && _gameBaseAddress != IntPtr.Zero;
    public bool IsScanning { get; private set; }
    public string ProcessName => "Lazer";
    public event Action<bool>? OnPlayRecorded;
    public LiveSnapshot? LastRecordedSnapshot { get; private set; }

    public void Initialize()
    {
        if (!OffsetLoader.IsLoaded) OffsetLoader.Load();
        if (IsConnected || (DateTime.Now - _lastConnectionAttempt).TotalSeconds < 5) return;
        _lastConnectionAttempt = DateTime.Now;
        var procs = Process.GetProcessesByName("osu").Concat(Process.GetProcessesByName("osu!")).ToArray();
        if (procs.Length == 0) { _process = null!; _gameBaseAddress = IntPtr.Zero; return; }
        _process = procs.First();
        _scanner?.Dispose();
        _scanner = new(_process);
        _modVTableMap.Clear();
        UpdateGameBaseAddress();
    }

    public void UpdateGameBaseAddress()
    {
        if (_scanner == null) return;
        IsScanning = true;
        try {
            var pat = "00 00 80 44 00 00 40 44 00 00 00 00 ?? ?? ?? ?? 00 00 00 00";
            var cand = _scanner.ScanAll(pat, false, false, false, true);
            if (cand.Count == 0) cand = _scanner.ScanAll(pat, false, false, false, false);
            foreach (var addr in cand) {
                IntPtr ext = _scanner.ReadIntPtr(IntPtr.Subtract(addr, 0x24));
                if (ext == IntPtr.Zero) continue;
                IntPtr api = _scanner.ReadIntPtr(IntPtr.Add(ext, OffsetLoader.ExternalLinkOpener.api));
                if (api == IntPtr.Zero) continue;
                IntPtr gb = _scanner.ReadIntPtr(IntPtr.Add(api, OffsetLoader.APIAccess.game));
                if (gb != IntPtr.Zero) { _gameBaseAddress = gb; _modVTableMap.Clear(); BuildModVTableMap(); return; }
            }
            _gameBaseAddress = IntPtr.Zero;
        } finally { IsScanning = false; }
    }

    private int ReadGamemode()
    {
        if (_gameBaseAddress == IntPtr.Zero) return 0;
        try {
            IntPtr rb = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameDesktop.Ruleset));
            IntPtr ri = _scanner.ReadIntPtr(IntPtr.Add(rb, 0x20));
            int gm = _scanner.ReadInt32(IntPtr.Add(ri, OffsetLoader.RulesetInfo.OnlineID));
            return (gm >= 0 && gm <= 3) ? gm : 0;
        } catch { return 0; }
    }

    private void BuildModVTableMap() => BuildModVTableMap(ReadGamemode());

    private string? TryReadAcr(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        for (int i = 0; i <= 0x60; i += 8) {
            try {
                IntPtr p = _scanner.ReadIntPtr(IntPtr.Add(ptr, i));
                if (p == IntPtr.Zero) continue;
                string s = _scanner.ReadString(p).ToUpper();
                if (s.Length >= 2 && s.Length <= 4 && KnownAcr.Contains(s)) return s;
            } catch { }
        }
        return null;
    }

    private void BuildModVTableMap(int gm)
    {
        if (_gameBaseAddress == IntPtr.Zero) return;
        _modVTableMap.Clear();
        _lastGamemode = gm;
        try {
            IntPtr mb = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameDesktop.AvailableMods));
            IntPtr dict = IntPtr.Zero;
            foreach (var off in new[] { 0x20, 0x10, 0x18, 0x28, 0x30 }) {
                IntPtr p = _scanner.ReadIntPtr(IntPtr.Add(mb, off));
                if (p != IntPtr.Zero && _scanner.ReadIntPtr(IntPtr.Add(p, 0x10)) != IntPtr.Zero) { dict = p; break; }
            }
            if (dict == IntPtr.Zero) return;
            IntPtr ent = _scanner.ReadIntPtr(IntPtr.Add(dict, 0x10));
            int count = _scanner.ReadInt32(IntPtr.Add(dict, 0x38));
            if (ent == IntPtr.Zero || count <= 0 || count > 150) return;
            string[] cats = ["Reduction", "Increase", "Conversion", "Automation", "Fun", "System"];
            for (int i = 0; i < count; i++) {
                IntPtr ep = IntPtr.Add(ent, 0x10 + i * 0x18);
                IntPtr mlp = IntPtr.Zero; int mt = -1;
                for (int off = 0; off <= 16; off += 8) {
                    int pt = _scanner.ReadInt32(IntPtr.Add(ep, off));
                    if (pt >= 0 && pt < cats.Length) {
                        IntPtr v1 = (off + 8 <= 16) ? _scanner.ReadIntPtr(IntPtr.Add(ep, off + 8)) : IntPtr.Zero;
                        IntPtr v2 = (off - 8 >= 0) ? _scanner.ReadIntPtr(IntPtr.Add(ep, off - 8)) : IntPtr.Zero;
                        if (v1 != IntPtr.Zero && v1.ToInt64() > 0x10000) { mlp = v1; mt = pt; break; }
                        if (v2 != IntPtr.Zero && v2.ToInt64() > 0x10000) { mlp = v2; mt = pt; break; }
                    }
                }
                if (mlp == IntPtr.Zero) continue;
                string cn = cats[mt];
                if (!ModsCategories.ContainsKey(gm) || !ModsCategories[gm].ContainsKey(cn)) continue;
                string[] exp = ModsCategories[gm][cn];
                int ls = _scanner.ReadInt32(IntPtr.Add(mlp, 0x10));
                IntPtr ia = _scanner.ReadIntPtr(IntPtr.Add(mlp, 0x8));
                if (ia == IntPtr.Zero || ls <= 0) continue;
                List<IntPtr> flats = new();
                for (int j = 0; j < ls; j++) {
                    IntPtr mp = _scanner.ReadIntPtr(IntPtr.Add(ia, 0x10 + j * 8));
                    if (mp == IntPtr.Zero) continue;
                    IntPtr vt = _scanner.ReadIntPtr(mp);
                    if (vt == IntPtr.Zero) continue;
                    string? da = TryReadAcr(mp);
                    bool multi = da == null && _scanner.ReadInt32(vt) == 16777216 && _scanner.ReadInt32(IntPtr.Add(vt, 3)) == 8193;
                    if (multi) {
                        IntPtr na = _scanner.ReadIntPtr(IntPtr.Add(mp, 0x8));
                        if (na.ToInt64() < 0x10000) na = _scanner.ReadIntPtr(IntPtr.Add(mp, 0x10));
                        if (na != IntPtr.Zero) {
                            int sz = _scanner.ReadInt32(IntPtr.Add(na, 0x8));
                            for (int k = 0; k < Math.Min(sz, 20); k++) {
                                IntPtr nm = _scanner.ReadIntPtr(IntPtr.Add(na, 0x10 + k * 8));
                                if (nm != IntPtr.Zero) flats.Add(nm);
                            }
                        }
                    } else flats.Add(mp);
                }
                for (int j = 0; j < flats.Count; j++) {
                    IntPtr mp = flats[j]; IntPtr vt = _scanner.ReadIntPtr(mp);
                    if (vt != IntPtr.Zero && !_modVTableMap.ContainsKey(vt)) {
                        string? acr = TryReadAcr(mp) ?? (j < exp.Length ? exp[j] : null);
                        if (acr != null) _modVTableMap[vt] = acr;
                    }
                }
            }
        } catch (Exception ex) { WriteLog($"ModMap Err: {ex.Message}"); }
    }

    private double ReadHUDPP(IntPtr pa)
    {
        if (pa == IntPtr.Zero) return 0;
        try {
            IntPtr ho = _scanner.ReadIntPtr(IntPtr.Add(pa, OffsetLoader.Player.HUDOverlay));
            IntPtr mc = _scanner.ReadIntPtr(IntPtr.Add(ho, OffsetLoader.HUDOverlay.mainComponents));
            if (mc == IntPtr.Zero) {
                for (int i = 0x8; i < 0xC00; i += 8) {
                    IntPtr p = _scanner.ReadIntPtr(IntPtr.Add(ho, i));
                    if (p != IntPtr.Zero && _scanner.ReadIntPtr(IntPtr.Add(_scanner.ReadIntPtr(IntPtr.Add(p, OffsetLoader.SkinnableContainer.components)), OffsetLoader.BindableList.list)) != IntPtr.Zero) { mc = p; break; }
                }
            }
            if (mc == IntPtr.Zero) return 0;
            IntPtr cl = IntPtr.Zero; int cs = 0; IntPtr cia = IntPtr.Zero;
            for (int i = 0x8; i < 0x200; i += 8) {
                IntPtr pb = _scanner.ReadIntPtr(IntPtr.Add(mc, i));
                if (pb == IntPtr.Zero) continue;
                IntPtr pl = _scanner.ReadIntPtr(IntPtr.Add(pb, OffsetLoader.BindableList.list));
                if (pl == IntPtr.Zero) continue;
                int sz = _scanner.ReadInt32(IntPtr.Add(pl, 0x10));
                if (sz >= 0 && sz < 1000) {
                    IntPtr items = _scanner.ReadIntPtr(IntPtr.Add(pl, 0x8));
                    if (sz > 0 && items != IntPtr.Zero) { cl = pl; cs = sz; cia = items; break; }
                }
            }
            if (cl == IntPtr.Zero) return 0;
            for (int i = 0; i < cs; i++) {
                IntPtr cp = _scanner.ReadIntPtr(IntPtr.Add(cia, 0x20 + i * 8));
                IntPtr cur = _scanner.ReadIntPtr(IntPtr.Add(cp, OffsetLoader.RollingCounter.current));
                if (cur == IntPtr.Zero) continue;
                int ppi = _scanner.ReadInt32(IntPtr.Add(cur, OffsetLoader.Bindable.Value));
                double ppd = _scanner.ReadDouble(IntPtr.Add(cur, OffsetLoader.Bindable.Value));
                if (ppi > 0 && ppi < 50000) return ppi;
                if (ppd > 0 && ppd < 50000) return (int)Math.Round(ppd);
            }
        } catch { }
        return 0;
    }

    public void SetDebugLogging(bool e) { }
    private void WriteLog(string m) => DebugService.Log(m, "LazerReader");
    private void WriteLogThrottled(string k, string m) => DebugService.Throttled(k, m, "LazerReader");
    public bool CheckIfMultiplayerScreen(IntPtr a)
    {
        if (a == IntPtr.Zero || _gameBaseAddress == IntPtr.Zero) return false;
        try {
            IntPtr ga = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.API));
            if (_scanner.ReadIntPtr(IntPtr.Add(a, OffsetLoader.SoloResultsScreen.api)) == ga) return false;
            return _scanner.ReadIntPtr(IntPtr.Add(a, OffsetLoader.Multiplayer.client)) == _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.MultiplayerClient));
        } catch { return false; }
    }

    public bool CheckIfSongSelect(IntPtr a)
    {
        if (_gameBaseAddress == IntPtr.Zero || a == IntPtr.Zero) return false;
        return _scanner.ReadIntPtr(IntPtr.Add(a, OffsetLoader.SoloSongSelect.game)) == _gameBaseAddress || _scanner.ReadIntPtr(IntPtr.Add(a, 1272)) == _gameBaseAddress;
    }

    public bool CheckIfPlayer(IntPtr a)
    {
        if (_gameBaseAddress == IntPtr.Zero) return false;
        IntPtr ga = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.API));
        if (_scanner.ReadIntPtr(IntPtr.Add(a, OffsetLoader.SubmittingPlayer.api)) == ga) return _scanner.ReadIntPtr(IntPtr.Add(a, OffsetLoader.SubmittingPlayer.spectatorClient)) == _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.SpectatorClient));
        return _scanner.ReadIntPtr(IntPtr.Add(a, OffsetLoader.Player.api)) == ga;
    }

    public bool CheckIfSubmittingPlayer(IntPtr a)
    {
        if (_gameBaseAddress == IntPtr.Zero || a == IntPtr.Zero) return false;
        IntPtr ga = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.API));
        if (ga == IntPtr.Zero) return true;
        if (_scanner.ReadIntPtr(IntPtr.Add(a, OffsetLoader.SubmittingPlayer.api)) == ga) return true;
        return _scanner.ReadIntPtr(IntPtr.Add(a, OffsetLoader.SubmittingPlayer.spectatorClient)) == _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.SpectatorClient));
    }

    public bool CheckIfResultScreen(IntPtr a)
    {
        if (_gameBaseAddress == IntPtr.Zero || a == IntPtr.Zero) return false;
        if (_scanner.ReadIntPtr(IntPtr.Add(a, OffsetLoader.SoloResultsScreen.api)) == _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.API))) {
            _lastResultScoreInfoPtr = IntPtr.Zero;
            IntPtr sb = _scanner.ReadIntPtr(IntPtr.Add(a, OffsetLoader.SoloResultsScreen.SelectedScore));
            if (sb != IntPtr.Zero) _lastResultScoreInfoPtr = _scanner.ReadIntPtr(IntPtr.Add(sb, 0x20));
            return true;
        }
        return false;
    }

    public bool CheckIfPlayerLoader(IntPtr a) => _gameBaseAddress != IntPtr.Zero && a != IntPtr.Zero && _scanner.ReadIntPtr(IntPtr.Add(a, OffsetLoader.PlayerLoader.osuLogo)) == _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGame.osuLogo));
    public bool CheckIfEditor() => _gameBaseAddress != IntPtr.Zero && CheckIfEditorScreen(GetCurrentScreen());
    public bool CheckIfEditorScreen(IntPtr a) => a != IntPtr.Zero && _gameBaseAddress != IntPtr.Zero && _scanner.ReadIntPtr(IntPtr.Add(a, OffsetLoader.Editor.api)) == _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGame.osuLogo)) && _scanner.ReadIntPtr(IntPtr.Add(a, OffsetLoader.Editor.realm)) == _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.realm));

    public bool CheckIfMultiplayer()
    {
        if (CheckIfEditor() || _gameBaseAddress == IntPtr.Zero) return false;
        try {
            IntPtr mc = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.MultiplayerClient));
            if (mc == IntPtr.Zero) return false;
            IntPtr icb = _scanner.ReadIntPtr(IntPtr.Add(mc, OffsetLoader.OnlineMultiplayerClient.IsConnected));
            bool conn = icb != IntPtr.Zero && (_scanner.ReadByte(IntPtr.Add(icb, 0x40)) == 1 || _scanner.ReadByte(IntPtr.Add(icb, 0x10)) == 1);
            return _scanner.ReadIntPtr(IntPtr.Add(mc, OffsetLoader.MultiplayerClient.room)) != IntPtr.Zero || _scanner.ReadIntPtr(IntPtr.Add(mc, OffsetLoader.MultiplayerClient.APIRoom)) != IntPtr.Zero || conn;
        } catch { return false; }
    }

    public IntPtr GetCurrentScreen()
    {
        if (_gameBaseAddress == IntPtr.Zero) return IntPtr.Zero;
        IntPtr ss = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGame.ScreenStack));
        IntPtr sp = _scanner.ReadIntPtr(IntPtr.Add(ss, OffsetLoader.ScreenStack.stack));
        int cnt = _scanner.ReadInt32(IntPtr.Add(sp, 0x10));
        if (cnt <= 0) return IntPtr.Zero;
        return _scanner.ReadIntPtr(IntPtr.Add(_scanner.ReadIntPtr(IntPtr.Add(sp, 0x8)), 0x10 + 0x8 * (cnt - 1)));
    }

    public LiveSnapshot GetStats()
    {
        try {
            var snap = new LiveSnapshot { StateNumber = -1 };
            if (_gameBaseAddress == IntPtr.Zero) { Initialize(); if (_gameBaseAddress == IntPtr.Zero) return snap; }
            IntPtr cs = (DateTime.Now - _lastScreenScan > TimeSpan.FromMilliseconds(250) || _cachedCurrentScreen == IntPtr.Zero) ? GetCurrentScreen() : _cachedCurrentScreen;
            _cachedCurrentScreen = cs; _lastScreenScan = DateTime.Now;
            if (cs == IntPtr.Zero) {
                snap.StateNumber = 0; ReadBeatmap(snap);
                if (DateTime.Now - _lastModScan > TimeSpan.FromMilliseconds(500)) { var m = ReadModsFromMemory(); snap.ModsList = m; snap.Mods = m.Count > 0 ? string.Join(",", m) : "NM"; _lastModScan = DateTime.Now; }
                else { snap.ModsList = _currentModsList; snap.Mods = _currentModsList.Count > 0 ? string.Join(",", _currentModsList) : "NM"; }
                snap.Stars = _staticStars; snap.BPM = (int?)Math.Round(_staticBpm); snap.MinBPM = (int?)Math.Round(_minBpm); snap.MaxBPM = (int?)Math.Round(_maxBpm); snap.MostlyBPM = (int?)Math.Round(_staticBpm);
                _detector?.Process(snap); return snap;
            }
            IntPtr bi = ReadBeatmap(snap); UpdateBeatmapFile(bi);
            if (bi != IntPtr.Zero) snap.MapPath = _currentOsuFilePath;
            IntPtr ps = CheckIfPlayer(cs) ? cs : IntPtr.Zero;
            IntPtr rs = (ps == IntPtr.Zero && CheckIfResultScreen(cs)) ? cs : IntPtr.Zero;
            if (ps != IntPtr.Zero || rs != IntPtr.Zero) _lastScreenScan = DateTime.MinValue;
            IntPtr sss = (ps == IntPtr.Zero && rs == IntPtr.Zero && CheckIfSongSelect(cs)) ? cs : IntPtr.Zero;
            IntPtr es = (ps == IntPtr.Zero && rs == IntPtr.Zero && sss == IntPtr.Zero && CheckIfEditorScreen(cs)) ? cs : IntPtr.Zero;
            IntPtr ms = (ps == IntPtr.Zero && rs == IntPtr.Zero && sss == IntPtr.Zero && es == IntPtr.Zero && CheckIfMultiplayerScreen(cs)) ? cs : IntPtr.Zero;
            IntPtr ssp = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGame.ScreenStack));
            if (ssp != IntPtr.Zero) {
                IntPtr stp = _scanner.ReadIntPtr(IntPtr.Add(ssp, OffsetLoader.ScreenStack.stack));
                if (stp != IntPtr.Zero) {
                    int cnt = _scanner.ReadInt32(IntPtr.Add(stp, 0x10)); IntPtr ip = _scanner.ReadIntPtr(IntPtr.Add(stp, 0x8));
                    if (ip != IntPtr.Zero && cnt > 0) {
                        for (int i = cnt - 1; i >= 0; i--) {
                            IntPtr s = _scanner.ReadIntPtr(IntPtr.Add(ip, 0x10 + 0x8 * i));
                            if (rs == IntPtr.Zero && CheckIfResultScreen(s)) rs = s;
                            if (ps == IntPtr.Zero && (CheckIfPlayer(s) || CheckIfPlayerLoader(s))) ps = s;
                            if (es == IntPtr.Zero && CheckIfEditorScreen(s)) es = s;
                            if (ms == IntPtr.Zero && CheckIfMultiplayerScreen(s)) ms = s;
                            if (sss == IntPtr.Zero && CheckIfSongSelect(s)) sss = s;
                        }
                    }
                }
            }
            if (rs != IntPtr.Zero) {
                snap.StateNumber = 7; snap.IsPlaying = false; snap.Passed = true; snap.Failed = false;
                if (_lastResultScoreInfoPtr != IntPtr.Zero) { UpdateResultScreenSnapshot(_lastResultScoreInfoPtr, snap); if ((snap.Score ?? 0) > 0 && snap.HitCounts != null) snap.IsResultsReady = true; }
            } else if (ps != IntPtr.Zero) {
                snap.StateNumber = 2; snap.IsPlaying = true; snap.IsReplay = !CheckIfSubmittingPlayer(ps);
                IntPtr sp = _scanner.ReadIntPtr(IntPtr.Add(ps, OffsetLoader.Player.Score));
                if (sp != IntPtr.Zero) {
                    IntPtr spi = _scanner.ReadIntPtr(IntPtr.Add(sp, 0x8));
                    if (spi != IntPtr.Zero) { ReadScoreInfo(spi, ps, snap); UpdateStaticAttributesIfNeeded(snap.ModsList ?? new(), GetClockRate()); }
                }
            } else if (es != IntPtr.Zero) snap.StateNumber = 1; else if (ms != IntPtr.Zero) snap.StateNumber = 11;
            else if (sss != IntPtr.Zero) {
                snap.StateNumber = 5; snap.IsPlaying = false; var m = ReadModsFromMemory(); snap.ModsList = m; snap.Mods = m.Count > 0 ? string.Join(",", m) : "NM";
                RawBeatmapInfo? rbi = (DateTime.Now - _lastBeatmapInfoScan > TimeSpan.FromMilliseconds(200) || _cachedRawBeatmapInfo == null) ? ReadRawBeatmapInfoCached() : _cachedRawBeatmapInfo;
                _cachedRawBeatmapInfo = rbi; _lastBeatmapInfoScan = DateTime.Now;
                if (rbi != null && !string.IsNullOrEmpty(rbi.MD5Hash)) {
                    string of = (rbi.FileHash == _lastResolvedMd5 && !string.IsNullOrEmpty(_currentOsuFilePath)) ? _currentOsuFilePath : (ResolveOsuFileByHash(rbi.BeatmapSetInfoPtr, rbi.FileHash) ?? "");
                    _lastResolvedMd5 = rbi.FileHash;
                    if (File.Exists(of)) {
                        if (of != _currentOsuFilePath) { _currentOsuFilePath = of; ParseMapDataFromFile(of); }
                        double cr = GetClockRate(); UpdateStaticAttributesIfNeeded(m, cr);
                        bool chg = _cachedStats != null && (Math.Abs(_cachedStats.AR - (float)(_currentModSettings.AR ?? -1)) > 0.001f || Math.Abs(_cachedStats.CS - (float)(_currentModSettings.CS ?? -1)) > 0.001f || Math.Abs(_cachedStats.OD - (float)(_currentModSettings.OD ?? -1)) > 0.001f || Math.Abs(_cachedStats.HP - (float)(_currentModSettings.HP ?? -1)) > 0.001f);
                        if (_cachedStats == null || _cachedStats.MD5Hash != rbi.MD5Hash || _cachedStats.RosuMods != GetModsBits(m) || Math.Abs(_cachedStats.ClockRate - cr) > 0.001 || chg) {
                            _rosuService.UpdateContext(_currentOsuFilePath);
                            var pps = _rosuService.CalculatePpIfFc(_currentOsuFilePath, m, 100.0, _currentModSettings.AR ?? -1, _currentModSettings.CS ?? -1, _currentModSettings.OD ?? -1, _currentModSettings.HP ?? -1, cr, true);
                            var at = _rosuService.GetDifficultyAttributes(_currentOsuFilePath, m, cr, _currentModSettings.AR ?? -1, _currentModSettings.CS ?? -1, _currentModSettings.OD ?? -1, _currentModSettings.HP ?? -1);
                            _cachedStats = new() { MD5Hash = rbi.MD5Hash, RosuMods = GetModsBits(m), ClockRate = cr, PPIfFC = pps.PP, MaxCombo = pps.MaxCombo, Stars = pps.Stars, MapLength = pps.MapLength, AR = (float)at.AR, CS = (float)at.CS, OD = (float)at.OD, HP = (float)at.HP, MaxScore = (long)Math.Round(Math.Pow(_totalObjects, 2) * 32.57 + 100000) };
                        }
                        if (_cachedStats != null) { snap.PPIfFC = _cachedStats.PPIfFC; snap.MaxCombo = _cachedStats.MaxCombo; snap.Combo = _cachedStats.MaxCombo; snap.Score = _cachedStats.MaxScore; snap.Stars = _cachedStats.Stars; snap.TotalObjects = _totalObjects; snap.AR = _cachedStats.AR; snap.CS = _cachedStats.CS; snap.OD = _cachedStats.OD; snap.HP = _cachedStats.HP; if (_cachedStats.MapLength > 0) { snap.TotalTimeMs = (int)(_cachedStats.MapLength / cr); snap.TimeMs = snap.TotalTimeMs; } }
                    }
                }
            } else { snap.StateNumber = 0; var m = ReadModsFromMemory(); snap.ModsList = m; snap.Mods = m.Count > 0 ? string.Join(",", m) : "NM"; UpdateStaticAttributesIfNeeded(m, GetClockRate()); }
            snap.Stars = _staticStars > 0 ? _staticStars : snap.Stars; snap.BaseStars = _baseStars; snap.BPM = (int?)Math.Round(_staticBpm); snap.BaseBPM = (int?)Math.Round(_baseModeBpm); snap.MinBPM = (int?)Math.Round(_minBpm); snap.MaxBPM = (int?)Math.Round(_maxBpm); snap.MostlyBPM = (int?)Math.Round(_staticBpm);
            snap.BaseCS = _baseCS; snap.BaseAR = _baseAR; snap.BaseOD = _baseOD; snap.BaseHP = _baseHP; snap.Circles = _circles; snap.Sliders = _sliders; snap.Spinners = _spinners;
            _detector?.Process(snap); return snap;
        } catch { return new LiveSnapshot { StateNumber = -1 }; }
    }

    private IntPtr ReadBeatmap(LiveSnapshot snap)
    {
        try {
            IntPtr bb = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.Beatmap));
            IntPtr wb = _scanner.ReadIntPtr(IntPtr.Add(bb, 0x20));
            IntPtr bi = _scanner.ReadIntPtr(IntPtr.Add(wb, 0x8));
            IntPtr md = _scanner.ReadIntPtr(IntPtr.Add(bi, OffsetLoader.BeatmapInfo.Metadata));
            if (md != IntPtr.Zero) { snap.Title = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(md, OffsetLoader.BeatmapMetadata.Title))); snap.Artist = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(md, OffsetLoader.BeatmapMetadata.Artist))); }
            snap.MD5Hash = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(bi, OffsetLoader.BeatmapInfo.MD5Hash)));
            IntPtr diff = _scanner.ReadIntPtr(IntPtr.Add(bi, OffsetLoader.BeatmapInfo.Difficulty));
            if (diff != IntPtr.Zero) { snap.CS = _scanner.ReadFloat(IntPtr.Add(diff, OffsetLoader.BeatmapDifficulty.CircleSize)); snap.AR = _scanner.ReadFloat(IntPtr.Add(diff, OffsetLoader.BeatmapDifficulty.ApproachRate)); snap.OD = _scanner.ReadFloat(IntPtr.Add(diff, OffsetLoader.BeatmapDifficulty.OverallDifficulty)); snap.HP = _scanner.ReadFloat(IntPtr.Add(diff, OffsetLoader.BeatmapDifficulty.DrainRate)); }
            snap.TotalObjects = _scanner.ReadInt32(IntPtr.Add(bi, OffsetLoader.BeatmapInfo.TotalObjectCount));
            double len = _scanner.ReadDouble(IntPtr.Add(bi, OffsetLoader.BeatmapInfo.Length)); if (len > 0) snap.TotalTimeMs = (int)len;
            string dn = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(bi, OffsetLoader.BeatmapInfo.DifficultyName)));
            snap.Version = dn; snap.Beatmap = !string.IsNullOrEmpty(dn) ? $"{snap.Artist} - {snap.Title} [{dn}]" : $"{snap.Artist} - {snap.Title}";
            if (md != IntPtr.Zero) {
                string bf = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(md, OffsetLoader.BeatmapMetadata.BackgroundFile)));
                if (!string.IsNullOrEmpty(bf)) {
                    IntPtr bsi = _scanner.ReadIntPtr(IntPtr.Add(wb, OffsetLoader.BeatmapManagerWorkingBeatmap.BeatmapSetInfo));
                    if (bsi != IntPtr.Zero) { string? h = FindFileHashByName(bsi, bf); if (h != null) { snap.BackgroundHash = h; snap.BackgroundPath = $"/api/background/{h}"; } else { snap.BackgroundHash = null; snap.BackgroundPath = null; } }
                }
            }
            return bi;
        } catch { return IntPtr.Zero; }
    }

    private string? FindFileHashByName(IntPtr bsi, string fn)
    {
        try {
            IntPtr fl = _scanner.ReadIntPtr(IntPtr.Add(bsi, OffsetLoader.BeatmapSetInfo.Files));
            IntPtr ia = _scanner.ReadIntPtr(IntPtr.Add(fl, 0x8));
            int cnt = _scanner.ReadInt32(IntPtr.Add(fl, 0x10));
            if (ia == IntPtr.Zero || cnt <= 0 || cnt > 500) return null;
            for (int i = 0; i < cnt; i++) {
                IntPtr item = _scanner.ReadIntPtr(IntPtr.Add(ia, 0x10 + i * 8));
                if (item == IntPtr.Zero) continue;
                if (string.Equals(_scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(item, OffsetLoader.RealmNamedFileUsage.Filename))), fn, StringComparison.OrdinalIgnoreCase)) {
                    IntPtr rf = _scanner.ReadIntPtr(IntPtr.Add(item, OffsetLoader.RealmNamedFileUsage.File));
                    string h = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(rf, OffsetLoader.RealmFile.Hash)));
                    if (h.Length > 10) return h;
                }
            }
        } catch { }
        return null;
    }

    private void ReadScoreInfo(IntPtr si, IntPtr p, LiveSnapshot snap)
    {
        try {
            _currentModSettings = new(); List<string> ml = ReadMods(si);
            long ss = _scanner.ReadInt64(IntPtr.Add(si, OffsetLoader.ScoreInfo.TotalScore));
            snap.Accuracy = _scanner.ReadDouble(IntPtr.Add(si, OffsetLoader.ScoreInfo.Accuracy));
            snap.MaxCombo = _scanner.ReadInt32(IntPtr.Add(si, OffsetLoader.ScoreInfo.MaxCombo));
            snap.ReplayHash = ReadScoreHash(si);
            IntPtr msd = _scanner.ReadIntPtr(IntPtr.Add(si, OffsetLoader.ScoreInfo.maximumStatistics));
            if (msd != IntPtr.Zero) { int to = GetObjectCountFromMaxStatistics(msd); if (to > 0) snap.TotalObjects = to; }
            if ((snap.TimeMs ?? 0) < 0) { snap.MaxCombo = 0; snap.Combo = 0; ss = 0; }
            else {
                IntPtr spr = _scanner.ReadIntPtr(IntPtr.Add(p, OffsetLoader.Player.ScoreProcessor));
                if (spr != IntPtr.Zero) {
                    IntPtr cb = _scanner.ReadIntPtr(IntPtr.Add(spr, OffsetLoader.OsuScoreProcessor.Combo));
                    if (cb != IntPtr.Zero) { int rc = _scanner.ReadInt32(IntPtr.Add(cb, 0x40)); snap.Combo = (rc < 0 || rc > 50000) ? 0 : rc; }
                    ReadHitEvents(spr, snap);
                }
            }
            IntPtr hpr = _scanner.ReadIntPtr(IntPtr.Add(p, OffsetLoader.Player.HealthProcessor));
            if (hpr != IntPtr.Zero) {
                IntPtr hb = _scanner.ReadIntPtr(IntPtr.Add(hpr, OffsetLoader.OsuHealthProcessor.Health));
                double hv = hb != IntPtr.Zero ? _scanner.ReadDouble(IntPtr.Add(hb, 0x40)) : -1;
                snap.Failed = (hv >= 0 && hv < 0.0001) && !(ml?.Contains("NF") ?? false);
            }
            ReadStatisticsDict(_scanner.ReadIntPtr(IntPtr.Add(si, OffsetLoader.ScoreInfo.statistics)), snap);
            int oc = snap.TotalObjects ?? 0;
            snap.Score = oc > 0 ? (long)Math.Round(((Math.Pow(oc, 2) * 32.57 + 100000) * ss) / 1000000.0) : ss;
            snap.Grade = CalculateGrade(snap);
            if (ml != null) _currentModsList = ml; snap.ModsList = ml; snap.Mods = ml != null && ml.Count > 0 ? string.Join(",", ml) : "NM";
            double cr = GetClockRate(); if (cr < 0.1 || cr > 5.0) cr = 1.0;
            if (!string.IsNullOrEmpty(_currentOsuFilePath) && ml != null) {
                var (ma, mc, mo, mh) = _rosuService.GetDifficultyAttributes(_currentOsuFilePath, ml, cr, _currentModSettings.AR ?? -1, _currentModSettings.CS ?? -1, _currentModSettings.OD ?? -1, _currentModSettings.HP ?? -1);
                snap.AR = (float)ma; snap.CS = (float)mc; snap.OD = (float)mo; snap.HP = (float)mh;
                if (_cachedStats != null && _cachedStats.MapLength > 0) snap.TotalTimeMs = (int)(_cachedStats.MapLength / cr);
            }
            if (snap.HitCounts != null) {
                if (!string.IsNullOrEmpty(_currentOsuFilePath)) _rosuService.UpdateContext(_currentOsuFilePath);
                snap.PP = _rosuService.CalculatePp(GetModsBits(ml), snap.Combo ?? 0, snap.HitCounts.Count300, snap.HitCounts.Count100, snap.HitCounts.Count50, snap.HitCounts.Misses, snap.HitCounts.Count300 + snap.HitCounts.Count100 + snap.HitCounts.Count50 + snap.HitCounts.Misses, snap.HitCounts.SliderTailHit, snap.HitCounts.SmallTickHit, snap.HitCounts.LargeTickHit, cr, _currentModSettings.AR ?? -1, _currentModSettings.CS ?? -1, _currentModSettings.OD ?? -1, _currentModSettings.HP ?? -1, true);
            }
            IntPtr bcp = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.beatmapClock));
            if (bcp != IntPtr.Zero) {
                IntPtr fcs = _scanner.ReadIntPtr(IntPtr.Add(bcp, OffsetLoader.FramedBeatmapClock.finalClockSource));
                if (fcs != IntPtr.Zero) {
                    double ct = _scanner.ReadDouble(IntPtr.Add(fcs, OffsetLoader.FramedClock.CurrentTime));
                    if (Math.Abs(ct - _lastTime) > 0.001) { _isPausedState = false; _lastTime = ct; _lastTimeChange = DateTime.Now; }
                    else if ((DateTime.Now - _lastTimeChange).TotalMilliseconds > 80) _isPausedState = true;
                    snap.IsPaused = _isPausedState; snap.TimeMs = (int)ct;
                }
            }
        } catch { }
    }

    private string? ReadScoreHash(IntPtr si)
    {
        if (si == IntPtr.Zero) return null;
        foreach (var off in new[] { 0x98, 0x80, 0xA0, 0x90, 0x88, 0x78 }) {
            try {
                IntPtr p = _scanner.ReadIntPtr(IntPtr.Add(si, off));
                if (p == IntPtr.Zero) continue;
                string s = _scanner.ReadString(p);
                if (!string.IsNullOrEmpty(s) && s.Length == 32 && s.All(c => "0123456789abcdefABCDEF".Contains(c))) return s;
            } catch { }
        }
        return null;
    }

    private void UpdateResultScreenSnapshot(IntPtr si, LiveSnapshot snap)
    {
        try {
            _currentModSettings = new();
            long ss = _scanner.ReadInt64(IntPtr.Add(si, OffsetLoader.ScoreInfo.TotalScore));
            snap.Accuracy = _scanner.ReadDouble(IntPtr.Add(si, OffsetLoader.ScoreInfo.Accuracy));
            snap.MaxCombo = Math.Max(_scanner.ReadInt32(IntPtr.Add(si, OffsetLoader.ScoreInfo.MaxCombo)), _scanner.ReadInt32(IntPtr.Add(si, OffsetLoader.ScoreInfo.Combo)));
            snap.Combo = snap.MaxCombo;
            try { long dt = _scanner.ReadInt64(IntPtr.Add(si, OffsetLoader.ScoreInfo.Date)); if (dt > 0) snap.ScoreDate = new DateTime(dt); } catch { }
            ReadStatisticsDict(_scanner.ReadIntPtr(IntPtr.Add(si, OffsetLoader.ScoreInfo.statistics)), snap);
            snap.ReplayHash = ReadScoreHash(si);
            IntPtr msd = _scanner.ReadIntPtr(IntPtr.Add(si, OffsetLoader.ScoreInfo.maximumStatistics));
            if (msd != IntPtr.Zero) { int to = GetObjectCountFromMaxStatistics(msd); if (to > 0) snap.TotalObjects = to; }
            List<string> ml = ReadMods(si); uint mb = GetModsBits(ml); double cr = RosuService.GetClockRateFromMods(mb);
            if (cr < 0.1 || cr > 5.0) cr = 1.0;
            if (!string.IsNullOrEmpty(_currentOsuFilePath)) {
                var at = _rosuService.GetDifficultyAttributes(_currentOsuFilePath, ml, cr, _currentModSettings.AR ?? -1, _currentModSettings.CS ?? -1, _currentModSettings.OD ?? -1, _currentModSettings.HP ?? -1);
                snap.AR = (float)at.AR; snap.CS = (float)at.CS; snap.OD = (float)at.OD; snap.HP = (float)at.HP;
                snap.Stars = _rosuService.GetStars(mb, 0, cr, _currentModSettings.AR ?? -1, _currentModSettings.CS ?? -1, _currentModSettings.OD ?? -1, _currentModSettings.HP ?? -1, true);
                snap.PP = _rosuService.CalculatePp(mb, snap.MaxCombo ?? 0, snap.HitCounts?.Count300 ?? 0, snap.HitCounts?.Count100 ?? 0, snap.HitCounts?.Count50 ?? 0, snap.HitCounts?.Misses ?? 0, snap.TotalObjects ?? _totalObjects, snap.HitCounts?.SliderTailHit ?? 0, snap.HitCounts?.SmallTickHit ?? 0, snap.HitCounts?.LargeTickHit ?? 0, cr, _currentModSettings.AR ?? -1, _currentModSettings.CS ?? -1, _currentModSettings.OD ?? -1, _currentModSettings.HP ?? -1, true);
                var ppr = _rosuService.CalculatePpIfFc(_currentOsuFilePath, ml, 100.0, _currentModSettings.AR ?? -1, _currentModSettings.CS ?? -1, _currentModSettings.OD ?? -1, _currentModSettings.HP ?? -1, cr, true);
                snap.PPIfFC = ppr.PP; if (snap.MaxCombo == null || snap.MaxCombo == 0) snap.MaxCombo = ppr.MaxCombo;
                if (ppr.MapLength > 0) snap.TotalTimeMs = (int)(ppr.MapLength / cr);
            }
            int oc = snap.TotalObjects ?? 0; snap.Score = oc > 0 ? (long)Math.Round(((Math.Pow(oc, 2) * 32.57 + 100000) * ss) / 1000000.0) : ss;
            snap.Grade = CalculateGrade(snap); snap.ModsList = ml; snap.Mods = ml.Count > 0 ? string.Join(",", ml) : "NM";
            try { IntPtr hl = _scanner.ReadIntPtr(IntPtr.Add(si, OffsetLoader.ScoreInfo.HitEvents)); if (hl != IntPtr.Zero) ReadHitEventsFromList(hl, snap); } catch { }
        } catch { }
    }

    private string CalculateGrade(LiveSnapshot snap)
    {
        double ac = snap.Accuracy ?? 0; int mi = snap.HitCounts?.Misses ?? 0, c3 = snap.HitCounts?.Count300 ?? 0, c1 = snap.HitCounts?.Count100 ?? 0, c5 = snap.HitCounts?.Count50 ?? 0, tl = c3 + c1 + c5 + mi;
        bool sl = snap.Mods != null && (snap.Mods.Contains("HD") || snap.Mods.Contains("FL"));
        double r3 = tl > 0 ? (double)c3 / tl : 0, r5 = tl > 0 ? (double)c5 / tl : 0;
        if (ac >= 1.0 || (mi == 0 && c1 == 0 && c5 == 0 && c3 > 0)) return sl ? "SSH" : "SS";
        if (mi == 0 && r3 > 0.9 && r5 < 0.01) return sl ? "SH" : "S";
        if ((mi == 0 && r3 > 0.8) || r3 > 0.9) return "A";
        if ((mi == 0 && r3 > 0.7) || r3 > 0.8) return "B";
        return r3 > 0.6 ? "C" : "D";
    }

    private void ReadHitEvents(IntPtr spr, LiveSnapshot snap)
    {
        try { IntPtr hl = _scanner.ReadIntPtr(IntPtr.Add(spr, OffsetLoader.ScoreProcessor.hitEvents)); ReadHitEventsFromList(hl, snap); } catch { }
    }

    private void ReadHitEventsFromList(IntPtr hl, LiveSnapshot snap)
    {
        if (hl == IntPtr.Zero) return;
        try {
            int cnt = _scanner.ReadInt32(IntPtr.Add(hl, 0x10)); if (cnt <= 0 || cnt > 30000) return;
            IntPtr ia = _scanner.ReadIntPtr(IntPtr.Add(hl, 0x8)); if (ia == IntPtr.Zero) return;
            List<double> offs = new(); var aims = new List<object[]>();
            for (int i = 0; i < cnt; i++) {
                IntPtr ha = IntPtr.Add(ia, 0x10 + (i * 0x40));
                double to = _scanner.ReadDouble(IntPtr.Add(ha, 0x10));
                int res = _scanner.ReadInt32(IntPtr.Add(ha, 0x18));
                LazerHitResults r = (LazerHitResults)res;
                if (r == LazerHitResults.Great || r == LazerHitResults.Perfect || r == LazerHitResults.Good || r == LazerHitResults.Ok || r == LazerHitResults.Meh) offs.Add(to);
                if (r >= LazerHitResults.Miss && r <= LazerHitResults.Perfect) aims.Add([to, res, 0.0, 0.0]);
            }
            snap.LiveHitOffsets = offs; snap.LiveUR = CalculateLiveUR(offs, RosuService.GetClockRateFromMods(GetModsBits(snap.ModsList)));
            var hist = new Dictionary<int, int>(); foreach (var o in offs) { int b = (int)Math.Round(o / 5.0) * 5; if (!hist.ContainsKey(b)) hist[b] = 0; hist[b]++; }
            snap.LiveHitOffsetHistogram = hist; snap.AimOffsetsJson = JsonSerializer.Serialize(aims);
        } catch { }
    }

    private double CalculateLiveUR(List<double> offs, double cr = 1.0)
    {
        if (offs == null || offs.Count == 0) return 0;
        if (cr <= 0) cr = 1.0;
        double mean = offs.Average() / cr, v = offs.Sum(o => Math.Pow((o / cr) - mean, 2)) / offs.Count;
        return Math.Sqrt(v) * 10.0;
    }

    private void ReadStatisticsDict(IntPtr da, LiveSnapshot snap)
    {
        if (da == IntPtr.Zero) return;
        int cnt = _scanner.ReadInt32(IntPtr.Add(da, 0x38)); if (cnt < 0 || cnt > 50000) cnt = _scanner.ReadInt32(IntPtr.Add(da, 0x18));
        IntPtr ep = _scanner.ReadIntPtr(IntPtr.Add(da, 0x10)); if (cnt < 0 || cnt > 50000 || ep == IntPtr.Zero) return;
        int h3 = 0, h1 = 0, h5 = 0, hm = 0, hst = 0, hstk = 0, hltk = 0;
        for (int i = 0; i < cnt; i++) {
            IntPtr ea = IntPtr.Add(ep, 0x10 + (i * 0x10));
            int k = _scanner.ReadInt32(IntPtr.Add(ea, 0x8)), v = _scanner.ReadInt32(IntPtr.Add(ea, 0xC));
            if (k <= 0 || v < 0) continue;
            LazerHitResults r = (LazerHitResults)k;
            switch (r) {
                case LazerHitResults.Great: case LazerHitResults.Perfect: h3 += v; break;
                case LazerHitResults.Good: case LazerHitResults.Ok: h1 += v; break;
                case LazerHitResults.Meh: h5 += v; break;
                case LazerHitResults.Miss: hm += v; break;
                case LazerHitResults.SliderTailHit: hst += v; break;
                case LazerHitResults.SmallTickHit: hstk += v; break;
                case LazerHitResults.LargeTickHit: hltk += v; break;
            }
        }
        snap.HitCounts = new(h3, h1, h5, hm, hst, hstk, hltk);
    }

    private void UpdateStaticAttributesIfNeeded(List<string> mods, double cr)
    {
        if (string.IsNullOrEmpty(_currentOsuFilePath)) return;
        uint mb = GetModsBits(mods);
        if (cr < 0.1 || cr > 5.0) cr = _currentModSettings.SpeedChange ?? RosuService.GetClockRateFromMods(mb);
        _staticStars = _rosuService.GetStars(mb, 0, cr, _currentModSettings.AR ?? -1, _currentModSettings.CS ?? -1, _currentModSettings.OD ?? -1, _currentModSettings.HP ?? -1, true);
        var nm = _rosuService.GetDifficultyAttributes(0, 1.0, -1, -1, -1, -1);
        _baseCS = (float)nm.CS; _baseAR = (float)nm.AR; _baseOD = (float)nm.OD; _baseHP = (float)nm.HP; _baseStars = _rosuService.GetStars(0, 0, 1.0, -1, -1, -1, -1, true);
        double rb = _rosuService.GetBpm(mb), eb = _baseModeBpm;
        if (eb <= 3.0 && rb > 0) eb = (mods.Contains("DT") || mods.Contains("NC")) ? rb / 1.5 : (mods.Contains("HT") ? rb / 0.75 : rb);
        if (eb > 0) { _staticBpm = eb * cr; _baseModeBpm = eb; } else { _staticBpm = rb; _baseModeBpm = _rosuService.BaseBpm; }
        _baseCS = (float)_rosuService.CS; _baseAR = (float)_rosuService.AR; _baseOD = (float)_rosuService.OD; _baseHP = (float)_rosuService.HP;
        _minBpm = _baseMinBpm * cr; _maxBpm = _baseMaxBpm * cr;
    }

    private string GetLazerFilesPath()
    {
        var ps = Process.GetProcessesByName("osu").Concat(Process.GetProcessesByName("osu!")).ToList();
        foreach (var p in ps) {
            try {
                if (!p.HasExited && p.MainModule != null) {
                    var dir = Path.GetDirectoryName(p.MainModule.FileName);
                    if (!string.IsNullOrEmpty(dir)) {
                        if (Directory.Exists(Path.Combine(dir, "files"))) return Path.Combine(dir, "files");
                        var prnt = Directory.GetParent(dir); if (prnt != null && Directory.Exists(Path.Combine(prnt.FullName, "files"))) return Path.Combine(prnt.FullName, "files");
                    }
                }
            } catch { }
        }
        var roam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu");
        if (Directory.Exists(Path.Combine(roam, "files"))) return Path.Combine(roam, "files");
        if (!string.IsNullOrEmpty(SettingsManager.Current.LazerPath) && Directory.Exists(Path.Combine(SettingsManager.Current.LazerPath, "files"))) return Path.Combine(SettingsManager.Current.LazerPath, "files");
        var loc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu");
        return Directory.Exists(Path.Combine(loc, "files")) ? Path.Combine(loc, "files") : Path.Combine(roam, "files");
    }

    private void UpdateBeatmapFile(IntPtr bi)
    {
        if (bi == IntPtr.Zero) return;
        try {
            string h = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(bi, OffsetLoader.BeatmapInfo.Hash)));
            if (h.Length > 10 && h != _currentBeatmapHash) {
                _currentBeatmapHash = h; string lfp = GetLazerFilesPath();
                if (h.Length >= 2) {
                    string p = Path.Combine(lfp, h.Substring(0, 1), h.Substring(0, 2), h);
                    if (File.Exists(p)) { _currentOsuFilePath = p; _rosuService.UpdateContext(p); var m = ReadModsFromMemory(); ParseMapDataFromFile(p); UpdateStaticAttributesIfNeeded(m, GetClockRate()); }
                    else { _currentOsuFilePath = null; _staticStars = 0; _staticBpm = 0; _minBpm = 0; _maxBpm = 0; _totalObjects = 0; }
                }
            }
        } catch { }
    }

    private void ParseMapDataFromFile(string p)
    {
        try {
            bool itp = false, iho = false; int oc = 0, c = 0, sl = 0, sp = 0; List<(double t, double bl)> tps = new(); double ft = -1, lt = 0; _objectStartTimes.Clear();
            foreach (var line in File.ReadLines(p)) {
                string l = line.Trim(); if (string.IsNullOrEmpty(l)) continue;
                if (l.StartsWith("[")) { itp = string.Equals(l, "[TimingPoints]", StringComparison.OrdinalIgnoreCase); iho = string.Equals(l, "[HitObjects]", StringComparison.OrdinalIgnoreCase); continue; }
                if (itp) {
                    var pts = l.Split(','); if (pts.Length >= 2 && double.TryParse(pts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double t) && double.TryParse(pts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double bl)) {
                        if ((pts.Length < 7 || pts[6].Trim() == "1") && bl > 0) tps.Add((t, bl));
                        if (ft < 0) ft = t; lt = t;
                    }
                } else if (iho) {
                    oc++; var pts = l.Split(','); if (pts.Length >= 4 && double.TryParse(pts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double t)) { _objectStartTimes.Add(t); if (t > lt) lt = t; }
                    if (int.TryParse(pts[3], out int ty)) { if ((ty & 1) != 0) c++; else if ((ty & 2) != 0) sl++; else if ((ty & 8) != 0) sp++; }
                }
            }
            _totalObjects = oc; _circles = c; _sliders = sl; _spinners = sp; _objectStartTimes.Sort();
            if (tps.Count > 0) {
                var bs = tps.Select(x => 60000.0 / x.bl).ToList(); _minBpm = bs.Min(); _maxBpm = bs.Max();
                if (tps.Count == 1) _staticBpm = bs[0];
                else {
                    tps.Sort((a, b) => a.t.CompareTo(b.t)); double md = 0, mb = bs[0]; Dictionary<double, double> bds = new();
                    for (int i = 0; i < tps.Count; i++) {
                        double st = tps[i].t, et = (i == tps.Count - 1) ? lt : tps[i + 1].t; if (et < st) et = st;
                        double rb = Math.Round(60000.0 / tps[i].bl); if (!bds.ContainsKey(rb)) bds[rb] = 0; bds[rb] += (et - st);
                    }
                    foreach (var kv in bds) { if (kv.Value > md) { md = kv.Value; mb = kv.Key; } }
                    _staticBpm = mb; _baseModeBpm = mb; _baseMinBpm = _minBpm; _baseMaxBpm = _maxBpm;
                }
            } else { _staticBpm = _minBpm = _maxBpm = _baseMinBpm = _baseMaxBpm = 0; }
        } catch { _totalObjects = 0; _staticBpm = 0; _baseMinBpm = 0; _baseMaxBpm = 0; }
    }

    private List<string> ReadModsFromMemory()
    {
        try {
            int gm = ReadGamemode(); if ((_modVTableMap.Count == 0 || gm != _lastGamemode) && _gameBaseAddress != IntPtr.Zero) BuildModVTableMap(gm);
            _currentModSettings = new(); IntPtr bm = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameDesktop.SelectedMods));
            if (bm == IntPtr.Zero) return new();
            IntPtr lp = IntPtr.Zero; foreach (var o in new[] { 0x18, 0x20, 0x10, 0x28, 0x30, 0x38, 0x40 }) { IntPtr p = _scanner.ReadIntPtr(IntPtr.Add(bm, o)); if (p != IntPtr.Zero) { lp = p; break; } }
            if (lp != IntPtr.Zero) {
                int sz = _scanner.ReadInt32(IntPtr.Add(lp, 0x10)); IntPtr ip = _scanner.ReadIntPtr(IntPtr.Add(lp, 0x8));
                if (ip != IntPtr.Zero && sz >= 0 && sz < 50) { List<string> m = new(); for (int i = 0; i < sz; i++) FlattenAndAddMod(_scanner.ReadIntPtr(IntPtr.Add(ip, 0x10 + i * 8)), m); var dm = m.Distinct().ToList(); _currentModsList = dm; return dm; }
            }
        } catch { }
        return new();
    }

    private void FlattenAndAddMod(IntPtr mp, List<string> m)
    {
        if (mp == IntPtr.Zero) return;
        IntPtr vt = _scanner.ReadIntPtr(mp); if (vt == IntPtr.Zero) return;
        if (_scanner.ReadInt32(vt) == 16777216 && _scanner.ReadInt32(IntPtr.Add(vt, 3)) == 8193) {
            IntPtr ap = _scanner.ReadIntPtr(IntPtr.Add(mp, 0x10)); if (ap != IntPtr.Zero) {
                int sz = _scanner.ReadInt32(IntPtr.Add(ap, 0x8)); if (sz > 0 && sz < 50) { for (int k = 0; k < sz; k++) FlattenAndAddMod(_scanner.ReadIntPtr(IntPtr.Add(ap, 0x10 + k * 8)), m); }
            }
        } else if (_modVTableMap.TryGetValue(vt, out string? acr)) {
            m.Add(acr);
            if (acr == "DT" || acr == "HT" || acr == "NC") { IntPtr sb = _scanner.ReadIntPtr(IntPtr.Add(mp, 0x10)); if (sb != IntPtr.Zero) { double r = _scanner.ReadDouble(IntPtr.Add(sb, 0x40)); if (r > 0.05 && r < 5.0) _currentModSettings.SpeedChange = r; } }
            else if (acr == "DA") {
                try {
                    IntPtr drb = _scanner.ReadIntPtr(IntPtr.Add(mp, 0x10)), odb = _scanner.ReadIntPtr(IntPtr.Add(mp, 0x18)), csb = _scanner.ReadIntPtr(IntPtr.Add(mp, 0x28)), arb = _scanner.ReadIntPtr(IntPtr.Add(mp, 0x30));
                    if (arb != IntPtr.Zero) { IntPtr c = _scanner.ReadIntPtr(IntPtr.Add(arb, 0x60)); float v = _scanner.ReadFloat(IntPtr.Add(c != IntPtr.Zero ? c : arb, 0x40)); _currentModSettings.AR = v != 0 ? (double)v : null; }
                    if (csb != IntPtr.Zero) { IntPtr c = _scanner.ReadIntPtr(IntPtr.Add(csb, 0x60)); float v = _scanner.ReadFloat(IntPtr.Add(c != IntPtr.Zero ? c : csb, 0x40)); _currentModSettings.CS = v != 0 ? (double)v : null; }
                    if (odb != IntPtr.Zero) { IntPtr c = _scanner.ReadIntPtr(IntPtr.Add(odb, 0x60)); float v = _scanner.ReadFloat(IntPtr.Add(c != IntPtr.Zero ? c : odb, 0x40)); _currentModSettings.OD = v != 0 ? (double)v : null; }
                    if (drb != IntPtr.Zero) { IntPtr c = _scanner.ReadIntPtr(IntPtr.Add(drb, 0x60)); float v = _scanner.ReadFloat(IntPtr.Add(c != IntPtr.Zero ? c : drb, 0x40)); if (v > 0) _currentModSettings.HP = v <= 10.1f ? (double)v : 10.0; else _currentModSettings.HP = null; }
                } catch { }
            }
        }
    }

    private int GetObjectCountFromMaxStatistics(IntPtr da)
    {
        if (da == IntPtr.Zero) return 0;
        int cnt = _scanner.ReadInt32(IntPtr.Add(da, 0x38)); IntPtr ep = _scanner.ReadIntPtr(IntPtr.Add(da, 0x10)); if (ep == IntPtr.Zero) return 0;
        int tl = 0; for (int i = 0; i < cnt; i++) {
            IntPtr ea = IntPtr.Add(ep, 0x10 + (i * 0x10)); int k = _scanner.ReadInt32(IntPtr.Add(ea, 0x8)), v = _scanner.ReadInt32(IntPtr.Add(ea, 0xC));
            if (k == 0) continue;
            LazerHitResults r = (LazerHitResults)k;
            if ((r == LazerHitResults.LegacyComboIncrease || r == LazerHitResults.ComboBreak || r == LazerHitResults.SliderTailHit || (r >= LazerHitResults.Miss && r < LazerHitResults.IgnoreMiss)) && !(r == LazerHitResults.LargeTickHit || r == LazerHitResults.LargeTickMiss || r == LazerHitResults.SmallTickHit || r == LazerHitResults.SmallTickMiss || r == LazerHitResults.SliderTailHit)) tl += v;
        }
        return tl;
    }

    private List<string> ReadMods(IntPtr si)
    {
        var m = new List<string>();
        try {
            string j = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(si, OffsetLoader.ScoreInfo.ModsJson)));
            if (!string.IsNullOrEmpty(j)) {
                var arr = Newtonsoft.Json.Linq.JArray.Parse(j);
                foreach (var x in arr) {
                    string? a = x["acronym"]?.ToString();
                    if (!string.IsNullOrEmpty(a)) { m.Add(a); var s = x["settings"]; if (s != null) { if (a == "DT" || a == "HT" || a == "NC") { double? spd = (double?)s["speed_change"]; if (spd.HasValue) _currentModSettings.SpeedChange = spd; } else if (a == "DA") { _currentModSettings.AR = (double?)s["approach_rate"]; _currentModSettings.CS = (double?)s["circle_size"]; _currentModSettings.OD = (double?)s["overall_difficulty"]; _currentModSettings.HP = (double?)s["drain_rate"]; } } }
                }
            }
        } catch { }
        if (m.Count == 0) m.Add("NM"); return m;
    }

    private uint GetModsBits(List<string>? m)
    {
        if (m == null) return 0; uint b = 0;
        foreach (var x in m) {
            switch (x.ToUpper()) {
                case "NF": b |= 1; break; case "EZ": b |= 2; break; case "TD": b |= 4; break; case "HD": b |= 8; break; case "HR": b |= 16; break; case "SD": b |= 32; break; case "DT": b |= 64; break; case "RX": b |= 128; break; case "HT": b |= 256; break; case "DC": b |= 256; break; case "NC": b |= 512 | 64; break; case "FL": b |= 1024; break; case "SO": b |= 4096; break; case "AP": b |= 8192; break; case "PF": b |= 16384 | 32; break; case "CL": b |= (1 << 24); break;
            }
        }
        return b;
    }

    private RawBeatmapInfo? ReadRawBeatmapInfoCached()
    {
        try {
            IntPtr bb = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.Beatmap));
            IntPtr wb = _scanner.ReadIntPtr(IntPtr.Add(bb, 0x20));
            IntPtr bi = _scanner.ReadIntPtr(IntPtr.Add(wb, OffsetLoader.BeatmapManagerWorkingBeatmap.BeatmapInfo));
            if (bi == _lastBeatmapInfoPtr && _cachedRawBeatmapInfo != null) return _cachedRawBeatmapInfo;
            _lastBeatmapInfoPtr = bi; _cachedRawBeatmapInfo = ReadRawBeatmapInfo(); return _cachedRawBeatmapInfo;
        } catch { return null; }
    }

    private double GetClockRate()
    {
        if (_currentModSettings.SpeedChange.HasValue) return _currentModSettings.SpeedChange.Value;
        try { IntPtr bcp = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.beatmapClock)); if (bcp != IntPtr.Zero) { double r = _scanner.ReadDouble(IntPtr.Add(bcp, OffsetLoader.FramedBeatmapClock.rate)); if (r > 0.05 && r < 5.0 && Math.Abs(r - 1.0) > 0.001) return r; } } catch { }
        if (_currentModsList != null) { if (_currentModsList.Contains("DT") || _currentModsList.Contains("NC")) return 1.5; if (_currentModsList.Contains("HT") || _currentModsList.Contains("DC")) return 0.75; }
        return 1.0;
    }

    private class RawBeatmapInfo { public float CS, AR, OD, HP; public double StarRating, Length; public string MD5Hash = "", FileHash = ""; public IntPtr BeatmapSetInfoPtr; }
    private RawBeatmapInfo? ReadRawBeatmapInfo()
    {
        try {
            IntPtr bb = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, OffsetLoader.OsuGameBase.Beatmap)), wb = _scanner.ReadIntPtr(IntPtr.Add(bb, 0x20)), bi = _scanner.ReadIntPtr(IntPtr.Add(wb, 0x8));
            if (bi == IntPtr.Zero) return null;
            var i = new RawBeatmapInfo { BeatmapSetInfoPtr = _scanner.ReadIntPtr(IntPtr.Add(wb, OffsetLoader.BeatmapManagerWorkingBeatmap.BeatmapSetInfo)), MD5Hash = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(bi, OffsetLoader.BeatmapInfo.MD5Hash))), FileHash = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(bi, OffsetLoader.BeatmapInfo.Hash))), Length = _scanner.ReadDouble(IntPtr.Add(bi, OffsetLoader.BeatmapInfo.Length)), StarRating = _scanner.ReadDouble(IntPtr.Add(bi, OffsetLoader.BeatmapInfo.StatusInt + 8)) };
            IntPtr d = _scanner.ReadIntPtr(IntPtr.Add(bi, OffsetLoader.BeatmapInfo.Difficulty));
            if (d != IntPtr.Zero) { i.CS = _scanner.ReadFloat(IntPtr.Add(d, OffsetLoader.BeatmapDifficulty.CircleSize)); i.AR = _scanner.ReadFloat(IntPtr.Add(d, OffsetLoader.BeatmapDifficulty.ApproachRate)); i.OD = _scanner.ReadFloat(IntPtr.Add(d, OffsetLoader.BeatmapDifficulty.OverallDifficulty)); i.HP = _scanner.ReadFloat(IntPtr.Add(d, OffsetLoader.BeatmapDifficulty.DrainRate)); }
            return i;
        } catch { return null; }
    }

    private string? ResolveOsuFileByHash(IntPtr bsi, string tfh)
    {
        if (bsi == IntPtr.Zero || string.IsNullOrEmpty(tfh)) return null;
        try {
            IntPtr fl = _scanner.ReadIntPtr(IntPtr.Add(bsi, OffsetLoader.BeatmapSetInfo.Files)), ia = _scanner.ReadIntPtr(IntPtr.Add(fl, 0x8)); int cnt = _scanner.ReadInt32(IntPtr.Add(fl, 0x10));
            if (ia == IntPtr.Zero || cnt <= 0 || cnt > 500) return null;
            for (int i = 0; i < cnt; i++) {
                IntPtr item = _scanner.ReadIntPtr(IntPtr.Add(ia, 0x10 + i * 8)); if (item == IntPtr.Zero) continue;
                string fh = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(_scanner.ReadIntPtr(IntPtr.Add(item, OffsetLoader.RealmNamedFileUsage.File)), OffsetLoader.RealmFile.Hash)));
                if (string.Equals(fh, tfh, StringComparison.OrdinalIgnoreCase)) { string fsp = GetLazerFilesPath(); return Path.Combine(fsp, fh.Substring(0, 1), fh.Substring(0, 2), fh); }
            }
        } catch { }
        return null;
    }

    public string? TryGetBeatmapPath(string md5)
    {
        if (string.IsNullOrEmpty(md5)) return null;
        if (string.Equals(md5, _currentBeatmapHash, StringComparison.OrdinalIgnoreCase)) return _currentOsuFilePath;
        try {
            string fp = GetLazerFilesPath(); if (Directory.Exists(fp)) { string p = Path.Combine(fp, md5.Substring(0, 1), md5.Substring(0, 2), md5); if (File.Exists(p)) return p; }
            foreach (var r in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu", "files"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu", "files") }) { if (Directory.Exists(r) && r != fp) { string p = Path.Combine(r, md5.Substring(0, 1), md5.Substring(0, 2), md5); if (File.Exists(p)) return p; } }
        } catch { }
        return null;
    }

    public enum LazerHitResults { None = 0, Miss = 1, Meh = 2, Ok = 3, Good = 4, Great = 5, Perfect = 6, SmallTickMiss = 7, SmallTickHit = 8, LargeTickMiss = 9, LargeTickHit = 10, SmallBonus = 11, LargeBonus = 12, IgnoreMiss = 13, IgnoreHit = 14, ComboBreak = 15, SliderTailHit = 16, LegacyComboIncrease = 17, LegacyHitAdornment = 18 }
}
