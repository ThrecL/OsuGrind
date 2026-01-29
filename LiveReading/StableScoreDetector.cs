using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using OsuGrind.Models;
using OsuGrind.Services;
using OsuGrind.Api;

namespace OsuGrind.LiveReading;

public class StableScoreDetector
{
    private readonly TrackerDb _db;
    private readonly SoundPlayer _soundPlayer;
    private readonly ApiServer _api;
    private bool _hasRecordedCurrentPlay;
    private int _lastState;
    private double _lastTime;
    private bool _wasPlaying;
    
    private List<object[]> _liveTimeline = new();
    private List<object[]> _ppTimeline = new(); 
    private List<int> _pendingHitIndices = new(); 
    private int _lastCombo;
    private int _lastMisses;
    private int _lastH300, _lastH100, _lastH50;
    private bool _waitingForResults;
    private DateTime _playStartTime = DateTime.MinValue;
    private LiveSnapshot? _pendingPassSnapshot = null;

    private long _lastSeenResultScore = -1;
    private int _consecutiveResultScoreCount = 0;

    public LiveSnapshot? LastSnapshot { get; private set; }

    public StableScoreDetector(TrackerDb db, SoundPlayer soundPlayer, ApiServer api)
    {
        _db = db;
        _soundPlayer = soundPlayer;
        _api = api;
    }

    public event Action<bool>? OnPlayRecorded;

    private int _prevState;
    private int _stateBeforeLast;
    private DateTime _lastStateChangeTime;
    private bool _isReplaySession;

    public void Process(LiveSnapshot snapshot)
    {
        try
        {
            bool isPlaying = snapshot.StateNumber == 2;
            bool isResults = snapshot.StateNumber == 7;
            bool isMenu = snapshot.StateNumber == 0 || snapshot.StateNumber == 1 || snapshot.StateNumber == 4 || snapshot.StateNumber == 5 || snapshot.StateNumber == 11;

            if (!isResults) 
            {
                _waitingForResults = false;
                _lastSeenResultScore = -1;
                _consecutiveResultScoreCount = 0;
            }

            if (isPlaying && _isReplaySession && !snapshot.IsReplay)
            {
                _isReplaySession = false;
                _wasPlaying = true;
                DebugService.Log("[StableDetector] CORRECTED TO LIVE.", "Detector");
            }

            if (snapshot.StateNumber != _lastState)
            {
                var now = DateTime.Now;

                if (isPlaying && _lastState != 2)
                {
                    _hasRecordedCurrentPlay = false;
                    _isReplaySession = snapshot.IsReplay;
                    _wasPlaying = !_isReplaySession;
                    _playStartTime = now; 
                    _liveTimeline.Clear();
                    _ppTimeline.Clear();
                    _pendingHitIndices.Clear();
                    _lastCombo = 0;
                    _lastMisses = 0;
                    _lastH300 = 0; _lastH100 = 0; _lastH50 = 0;
                    _lastTime = 0;
                    _pendingPassSnapshot = null;
                    
                    _ppTimeline.Add(new object[] { 0.0, 0, 1.0, 0, 0, 0, 0, 0, 0, 0 });
                    
                    DebugService.Log($"[StableDetector] Play Started. MD5={snapshot.MD5Hash}, IsReplay={_isReplaySession}", "Detector");
                }
                else if (isResults && _wasPlaying && !_hasRecordedCurrentPlay)
                {
                    DebugService.Log($"[StableDetector] Entering Results Screen.", "Detector");
                    _waitingForResults = true;
                }
                else if (isMenu)
                {
                    if (_pendingPassSnapshot != null)
                    {
                        DebugService.Log($"[StableDetector] Left Results Screen. Recording Pass now. Score={_pendingPassSnapshot.Score}", "Detector");
                        RecordPlay(_pendingPassSnapshot, true);
                        _pendingPassSnapshot = null;
                    }
                    else if (_wasPlaying && !_hasRecordedCurrentPlay)
                    {
                        DebugService.Log($"[StableDetector] Play Abandoned (Quit).", "Detector");
                    }
                    _wasPlaying = false;
                    _waitingForResults = false;
                }

                _stateBeforeLast = _prevState;
                _prevState = _lastState;
                _lastState = snapshot.StateNumber;
                _lastStateChangeTime = now;
            }

            if (_waitingForResults && isResults && snapshot.IsResultsReady)
            {
                long currentResultsScore = snapshot.Score ?? 0;
                
                if (currentResultsScore > 0 && currentResultsScore == _lastSeenResultScore)
                {
                    _consecutiveResultScoreCount++;
                }
                else
                {
                    _lastSeenResultScore = currentResultsScore;
                    _consecutiveResultScoreCount = 1;
                }

                if (_consecutiveResultScoreCount >= 5 && _pendingPassSnapshot == null)
                {
                    DebugService.Log($"[StableDetector] Score stable for 5 frames. Attempting capture. Score={snapshot.Score}, Combo={snapshot.MaxCombo}, Acc={snapshot.Accuracy:P2}, MapMaxCombo={snapshot.MapMaxCombo}, TotalObjects={snapshot.TotalObjects}", "Detector");
                    if (ValidateSnapshot(snapshot))
                    {
                        DebugService.Log($"[StableDetector] Results Data Captured and Stabilized. Score={snapshot.Score}, Combo={snapshot.MaxCombo}, Acc={snapshot.Accuracy:P2}", "Detector");
                        _pendingPassSnapshot = snapshot.Clone(); 
                        _waitingForResults = false;
                    }
                    else
                    {
                        DebugService.Log($"[StableDetector] Validation failed! Resetting stabilization counter to wait for real data.", "Detector");
                        _consecutiveResultScoreCount = 0;
                        _lastSeenResultScore = -1;
                    }
                }
            }

            if (isPlaying && _wasPlaying && LastSnapshot != null)
            {
                int passedObjects = (snapshot.HitCounts?.Count300 ?? 0) + (snapshot.HitCounts?.Count100 ?? 0) + (snapshot.HitCounts?.Count50 ?? 0) + (snapshot.HitCounts?.Misses ?? 0);

                if (snapshot.Failed && !_hasRecordedCurrentPlay && passedObjects > 0)
                {
                    if (ValidateSnapshot(snapshot))
                    {
                        DebugService.Log($"[StableDetector] Health reached 0. Recording Fail.", "Detector");
                        RecordPlay(snapshot, false);
                    }
                }
                else
                {
                    double currentTime = snapshot.TimeMs ?? 0;
                    double playDurationSec = (DateTime.Now - _playStartTime).TotalSeconds;

                    if (currentTime < _lastTime - 2000 && playDurationSec > 5)
                    {
                        DebugService.Log($"[StableDetector] Retry Detected. Resetting...", "Detector");
                        _hasRecordedCurrentPlay = false;
                        _playStartTime = DateTime.Now; 
                        _liveTimeline.Clear();
                        _ppTimeline.Clear();
                        _lastCombo = 0; _lastMisses = 0; _lastH300 = 0; _lastH100 = 0; _lastH50 = 0;
                        _ppTimeline.Add(new object[] { 0.0, 0, 1.0, 0, 0, 0, 0, 0, 0, 0 });
                    }
                }
            }

            if (isPlaying && _wasPlaying)
            {
                int combo = snapshot.Combo ?? 0;
                int misses = snapshot.HitCounts?.Misses ?? 0;
                int h300 = snapshot.HitCounts?.Count300 ?? 0;
                int h100 = snapshot.HitCounts?.Count100 ?? 0;
                int h50 = snapshot.HitCounts?.Count50 ?? 0;

                bool hChanged = h300 != _lastH300 || h100 != _lastH100 || h50 != _lastH50;
                bool comboChanged = combo != _lastCombo;
                bool missesChanged = misses != _lastMisses;

                if (hChanged || comboChanged || missesChanged || _ppTimeline.Count == 0)
                {
                    int eventType = missesChanged ? 1 : (combo < _lastCombo && !missesChanged ? 2 : 0);
                    
                    if ((hChanged || missesChanged) && _pendingHitIndices.Count > 0)
                    {
                        foreach (int idx in _pendingHitIndices)
                        {
                            var entry = _ppTimeline[idx];
                            entry[0] = snapshot.PP ?? (double)entry[0];
                            entry[2] = snapshot.Accuracy ?? (double)entry[2];
                            entry[3] = h300; entry[4] = h100; entry[5] = h50; entry[6] = misses;
                        }
                        _pendingHitIndices.Clear();
                    }

                    _lastH300 = h300; _lastH100 = h100; _lastH50 = h50;

                    var stats = new object[] { 
                        snapshot.PP ?? 0, snapshot.MaxCombo ?? 0, snapshot.Accuracy ?? 0,
                        h300, h100, h50, misses, snapshot.TimeMs ?? 0, combo, eventType
                    };

                    int statsIdx = _ppTimeline.Count;
                    _ppTimeline.Add(stats);
                    
                    if (comboChanged || missesChanged)
                    {
                        _liveTimeline.Add(new object[] { snapshot.TimeMs ?? 0, combo, eventType });
                        _lastCombo = combo;
                        _lastMisses = misses;
                    }
                }
            }

            _lastTime = snapshot.TimeMs ?? 0;
            LastSnapshot = snapshot;
        }
        catch (Exception ex)
        {
            DebugService.Error($"[StableDetector] Process Error: {ex.Message}", "Detector");
        }
    }

    private bool ValidateSnapshot(LiveSnapshot s)
    {
        if (s == null) { DebugService.Log("[StableDetector] Validate FAIL: snapshot is null", "Detector"); return false; }
        if (string.IsNullOrEmpty(s.MD5Hash)) { DebugService.Log("[StableDetector] Validate FAIL: MD5Hash is empty", "Detector"); return false; }
        if (s.Score < 0 || s.Score > 4000000000) { DebugService.Log($"[StableDetector] Validate FAIL: Score out of range ({s.Score})", "Detector"); return false; }
        if (s.Accuracy < 0 || s.Accuracy > 1.0001) { DebugService.Log($"[StableDetector] Validate FAIL: Accuracy out of range ({s.Accuracy})", "Detector"); return false; }
        
        int totalHits = (s.HitCounts?.Count300 ?? 0) + (s.HitCounts?.Count100 ?? 0) + (s.HitCounts?.Count50 ?? 0) + (s.HitCounts?.Misses ?? 0);
        
        if (s.StateNumber == 7)
        {
            if (totalHits == 0) { DebugService.Log("[StableDetector] Validate FAIL: Results screen but totalHits=0", "Detector"); return false; }
        }

        if (s.MapMaxCombo > 0)
        {
            int capturedCombo = s.MaxCombo ?? s.Combo ?? 0;
            if (capturedCombo > s.MapMaxCombo)
            {
                DebugService.Log($"[StableDetector] Validate FAIL: MaxCombo ({capturedCombo}) exceeds MapMaxCombo ({s.MapMaxCombo}). Likely stale data.", "Detector");
                return false;
            }
        }

        if (s.StateNumber == 7 && s.TotalObjects.HasValue && s.TotalObjects > 0)
        {
            double hitRatio = (double)totalHits / s.TotalObjects.Value;
            if (hitRatio < 0.8 || hitRatio > 1.05)
            {
                DebugService.Log($"[StableDetector] Validate FAIL: HitRatio ({hitRatio:F2}) out of expected range. TotalHits={totalHits}, TotalObjects={s.TotalObjects}. Likely stale data.", "Detector");
                return false;
            }
        }

        DebugService.Log($"[StableDetector] Validate PASS: Score={s.Score}, Acc={s.Accuracy:P2}, Combo={s.MaxCombo ?? s.Combo}, MapMax={s.MapMaxCombo}, TotalHits={totalHits}, TotalObj={s.TotalObjects}", "Detector");
        return true;
    }

    private void RecordPlay(LiveSnapshot s, bool isPass)
    {
        if (_hasRecordedCurrentPlay) return;
        if (s.IsPreview || s.IsReplay) return;
        _hasRecordedCurrentPlay = true;

        if (string.IsNullOrEmpty(s.MD5Hash)) return;

        string outcome = isPass ? "pass" : "fail";
        DebugService.Log($"[StableDetector] Saving {outcome} to DB. Score={s.Score}, Acc={s.Accuracy:P2}", "Detector");
        var play = CreateCompletedPlay(s, outcome);
        SaveToDb(play, isPass);
    }

    private CompletedPlay CreateCompletedPlay(LiveSnapshot s, string outcome)
    {
        string replayFile = "";
        if (outcome == "pass")
        {
            string stablePath = s.OsuFolder ?? SettingsManager.Current.StablePath ?? "";
            if (!string.IsNullOrEmpty(stablePath)) replayFile = FindLastReplay(stablePath, s.MD5Hash);
        }

        return new CompletedPlay
        {
            CreatedAtUtc = DateTime.UtcNow,
            BeatmapHash = s.MD5Hash ?? "",
            Beatmap = s.Beatmap,
            Artist = s.Artist ?? "",
            Title = s.Title ?? "",
            Version = s.Version ?? "",
            Mods = s.Mods,
            Outcome = outcome,
            DurationMs = s.TimeMs ?? 0,
            Stars = s.Stars,
            Accuracy = s.Accuracy ?? 0,
            Score = s.Score ?? 0,
            MaxCombo = s.MaxCombo ?? s.Combo ?? 0,
            Count300 = s.HitCounts?.Count300 ?? 0,
            Count100 = s.HitCounts?.Count100 ?? 0,
            Count50 = s.HitCounts?.Count50 ?? 0,
            Misses = s.HitCounts?.Misses ?? 0,
            PP = s.PP ?? 0,
            UR = s.LiveUR,
            HitOffsets = string.Join(",", s.LiveHitOffsets),
            HitErrorsJson = s.HitErrorsJson,
            KeyRatio = s.KeyRatio,
            TimelineJson = SerializeTimeline(),
            PpTimelineJson = SerializePpTimeline(),
            AimOffsetsJson = s.AimOffsetsJson,
            CS = s.CS ?? 0,
            AR = s.AR ?? 0,
            OD = s.OD ?? 0,
            HP = s.HP ?? 0,
            BPM = s.BPM ?? 0,
            LengthMs = s.TotalTimeMs ?? 0,
            Circles = s.Circles ?? 0,
            Sliders = s.Sliders ?? 0,
            Spinners = s.Spinners ?? 0,
            BackgroundHash = s.BackgroundHash,
            ReplayFile = replayFile,
            MapPath = s.MapPath ?? ""
        };
    }

    private string FindLastReplay(string osuFolder, string? md5)
    {
        try
        {
            var replayDir = Path.Combine(osuFolder, "Data", "r");
            if (!Directory.Exists(replayDir)) return "";
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                var files = new DirectoryInfo(replayDir).GetFiles("*.osr");
                var filtered = files
                    .Where(f => string.IsNullOrEmpty(md5) || f.Name.StartsWith(md5, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();
                if (filtered.Count > 0)
                {
                    var best = filtered[0];
                    if ((DateTime.Now - best.LastWriteTime).TotalSeconds < 120) return best.FullName;
                }
                System.Threading.Thread.Sleep(500);
            }
        }
        catch { }
        return "";
    }

    private void SaveToDb(CompletedPlay play, bool isPass)
    {
        var row = PlayRow.FromCompleted(play);
        
        if (isPass && !string.IsNullOrEmpty(play.ReplayFile) && File.Exists(play.MapPath))
        {
            try {
                var analysis = MissAnalysisService.Analyze(play.MapPath, play.ReplayFile);
                row.UR = analysis.UR;
                row.HitErrorsJson = System.Text.Json.JsonSerializer.Serialize(analysis.HitErrors);
                row.KeyRatio = analysis.KeyRatio;
            } catch { }
        }

        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                if (!string.IsNullOrEmpty(play.BeatmapHash))
                {
                    var beatmapRow = new BeatmapRow
                    {
                        Hash = play.BeatmapHash,
                        Title = play.Title,
                        Artist = play.Artist,
                        Version = play.Version,
                        CS = play.CS,
                        AR = play.AR,
                        OD = play.OD,
                        HP = play.HP,
                        BPM = play.BPM,
                        LengthMs = play.LengthMs,
                        Circles = play.Circles,
                        Sliders = play.Sliders,
                        Spinners = play.Spinners,
                        MaxCombo = play.MaxCombo,
                        Stars = play.Stars ?? 0,
                        BackgroundHash = play.BackgroundHash,
                        LastPlayedUtc = play.CreatedAtUtc
                    };
                    await _db.InsertOrUpdateBeatmapAsync(beatmapRow);
                }
                await _db.InsertPlayAsync(row);
                await _api.BroadcastRefresh();

                _ = GoalManager.CheckAndPlayGoalSound(_db, _soundPlayer);
            }
            catch (Exception ex) { DebugService.Error($"[StableDetector] DB Error: {ex.Message}", "Detector"); }
        });

        OnPlayRecorded?.Invoke(isPass);
        if (isPass && SettingsManager.Current.PassSoundEnabled) _soundPlayer.PlayPass();
        else if (!isPass && SettingsManager.Current.FailSoundEnabled) _soundPlayer.PlayFail();
    }

    private string SerializeTimeline() => Newtonsoft.Json.JsonConvert.SerializeObject(_liveTimeline);
    private string SerializePpTimeline() => Newtonsoft.Json.JsonConvert.SerializeObject(_ppTimeline);
}
