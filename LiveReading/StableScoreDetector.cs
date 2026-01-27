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
            // Strictly define states
            bool isPlaying = snapshot.StateNumber == 2;
            bool isResults = snapshot.StateNumber == 7;
            bool isMenu = snapshot.StateNumber == 0 || snapshot.StateNumber == 1 || snapshot.StateNumber == 4 || snapshot.StateNumber == 5 || snapshot.StateNumber == 11;

            if (!isResults) 
            {
                _waitingForResults = false;
                _lastSeenResultScore = -1;
                _consecutiveResultScoreCount = 0;
            }

            // Correct replay to live if memory now says LIVE
            if (isPlaying && _isReplaySession && !snapshot.IsReplay)
            {
                _isReplaySession = false;
                _wasPlaying = true;
                DebugService.Log("[StableDetector] CORRECTED TO LIVE.", "Detector");
            }

            // Handle State Transitions
            if (snapshot.StateNumber != _lastState)
            {
                var now = DateTime.Now;

                if (isPlaying && _lastState != 2)
                {
                    // Started Playing - Reset everything
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
                    
                    // Add an initial clean 0-stats frame
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
                    // Record pending pass when leaving results screen
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

            // Results Screen Stabilization
            if (_waitingForResults && isResults && snapshot.IsResultsReady)
            {
                long currentResultsScore = snapshot.Score ?? 0;
                
                // DATA STABILIZATION (Wait for memory to stop flickering)
                if (currentResultsScore > 0 && currentResultsScore == _lastSeenResultScore)
                {
                    _consecutiveResultScoreCount++;
                }
                else
                {
                    _lastSeenResultScore = currentResultsScore;
                    _consecutiveResultScoreCount = 1;
                }

                // If score is stable for 5 frames, capture it
                if (_consecutiveResultScoreCount >= 5 && _pendingPassSnapshot == null)
                {
                    if (ValidateSnapshot(snapshot))
                    {
                        DebugService.Log($"[StableDetector] Results Data Captured and Stabilized. Score={snapshot.Score}, Combo={snapshot.MaxCombo}, Acc={snapshot.Accuracy:P2}", "Detector");
                        _pendingPassSnapshot = snapshot.Clone(); 
                        _waitingForResults = false;
                        // Keep _wasPlaying true until we actually record it or leave menu
                    }
                }
            }

            // FAIL / RETRY DETECTION
            if (isPlaying && _wasPlaying && LastSnapshot != null)
            {
                int passedObjects = (snapshot.HitCounts?.Count300 ?? 0) + (snapshot.HitCounts?.Count100 ?? 0) + (snapshot.HitCounts?.Count50 ?? 0) + (snapshot.HitCounts?.Misses ?? 0);

                // Record fail if HP = 0
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

            // EVENT-DRIVEN TIMELINE RECORDING
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
        if (s == null) return false;
        if (string.IsNullOrEmpty(s.MD5Hash)) return false;
        if (s.Score < 0 || s.Score > 4000000000) return false; 
        if (s.Accuracy < 0 || s.Accuracy > 1.0001) return false;
        if (s.StateNumber == 7)
        {
            int totalHits = (s.HitCounts?.Count300 ?? 0) + (s.HitCounts?.Count100 ?? 0) + (s.HitCounts?.Count50 ?? 0) + (s.HitCounts?.Misses ?? 0);
            if (totalHits == 0) return false;
        }
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
            ScoreId = 0,
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
