using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using OsuGrind.Models;
using OsuGrind.Services;

namespace OsuGrind.LiveReading;

public class StableScoreDetector
{
    private readonly TrackerDb _db;
    private readonly SoundPlayer _soundPlayer;
    private bool _hasRecordedCurrentPlay;
    private int _lastState;
    private double _lastTime;
    private bool _wasPlaying;
    
    private List<object[]> _liveTimeline = new();
    private List<object[]> _ppTimeline = new(); // Format: [pp, maxC, acc, c300, c100, c50, miss, timeMs, currentCombo, eventType]
    private List<int> _pendingHitIndices = new(); // Indices in _ppTimeline waiting for a hit result (slider heads/ticks)
    private int _lastCombo;
    private int _lastMisses;
    private int _lastH300, _lastH100, _lastH50;
    private bool _waitingForResults;
    private DateTime _playStartTime = DateTime.MinValue;
    private LiveSnapshot? _pendingPassSnapshot = null;

    private long _lastSeenResultScore = -1;
    private int _consecutiveResultScoreCount = 0;
    private long _lastGameplayScore = 0;

    public LiveSnapshot? LastSnapshot { get; private set; }

    public StableScoreDetector(TrackerDb db, SoundPlayer soundPlayer)
    {
        _db = db;
        _soundPlayer = soundPlayer;
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

            if (!isResults) _waitingForResults = false;

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
                    _lastGameplayScore = 0;
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
                        DebugService.Log($"[StableDetector] Left Results Screen. Recording Pass now.", "Detector");
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
                if (currentResultsScore == _lastSeenResultScore && currentResultsScore > 0)
                {
                    _consecutiveResultScoreCount++;
                }
                else
                {
                    _lastSeenResultScore = currentResultsScore;
                    _consecutiveResultScoreCount = 1;
                    return; 
                }

                if (_consecutiveResultScoreCount >= 3)
                {
                    if (ValidateSnapshot(snapshot))
                    {
                        DebugService.Log($"[StableDetector] Results Data Captured and Stabilized. Score={snapshot.Score}", "Detector");
                        _pendingPassSnapshot = snapshot.Clone(); 
                        _waitingForResults = false;
                        _wasPlaying = false;
                        _lastSeenResultScore = -1;
                        _consecutiveResultScoreCount = 0;
                    }
                    else
                    {
                        DebugService.Log($"[StableDetector] Results Data failed validation. Score={snapshot.Score}, Acc={snapshot.Accuracy}", "Detector");
                        _consecutiveResultScoreCount = 0; // Reset and try again
                    }
                }
            }

            // FAIL / RETRY DETECTION
            if (isPlaying && _wasPlaying && LastSnapshot != null)
            {
                _lastGameplayScore = snapshot.Score ?? 0;
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
                        _lastGameplayScore = 0;
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

                // Capture every state change (Head hit, Tick hit, End hit, Miss, Circle hit)
                bool hChanged = h300 != _lastH300 || h100 != _lastH100 || h50 != _lastH50;
                bool comboChanged = combo != _lastCombo;
                bool missesChanged = misses != _lastMisses;

                // FIX: Detect missed Slider Heads (Combo increased but Hits didn't)
                // If we missed a frame where a slider head was hit, we might see Combo+2 and Hits+1 (Head + End).
                // We need to inject the intermediate "Head" event.
                if (comboChanged && !missesChanged && combo > _lastCombo)
                {
                    int currentTotalHits = h300 + h100 + h50;
                    int lastTotalHits = _lastH300 + _lastH100 + _lastH50;
                    int deltaCombo = combo - _lastCombo;
                    int deltaHits = currentTotalHits - lastTotalHits;

                    if (deltaHits >= 0)
                    {
                        int nonHitSteps = deltaCombo - deltaHits;
                        int eventsToInject = nonHitSteps;
                        // If no hits occurred this frame, the final event IS the non-hit event, so don't duplicate it.
                        if (deltaHits == 0) eventsToInject--;

                        if (eventsToInject > 0)
                        {
                            DebugService.Log($"[StableDetector] Injecting {eventsToInject} intermediate slider events. dCombo={deltaCombo}, dHits={deltaHits}", "Detector");
                            for (int i = 0; i < eventsToInject; i++)
                            {
                                int intermediateCombo = _lastCombo + 1 + i;
                                int injectedIdx = _ppTimeline.Count;
                                var stats = new object[] { 
                                    snapshot.PP ?? 0, snapshot.MaxCombo ?? 0, snapshot.Accuracy ?? 0, 
                                    _lastH300, _lastH100, _lastH50, _lastMisses,
                                    snapshot.TimeMs ?? 0, intermediateCombo, 0 
                                };
                                _ppTimeline.Add(stats);
                                _pendingHitIndices.Add(injectedIdx);
                                _liveTimeline.Add(new object[] { snapshot.TimeMs ?? 0, intermediateCombo, 0 });
                            }
                        }
                    }
                }
                
                // Record whenever stats change. This is the only way to catch slider heads.
                if (hChanged || comboChanged || missesChanged || _ppTimeline.Count == 0)
                {
                    int eventType = missesChanged ? 1 : (combo < _lastCombo && !missesChanged ? 2 : 0);
                    
                    // HIT SHIFTING LOGIC
                    // 1. Determine if this current event should be shifted later (Head, Tick, Repeat)
                    bool isPending = comboChanged && !hChanged && !missesChanged && combo > _lastCombo;
                    
                    // 2. If hits increased (or we missed), and we have pending slider parts, 
                    // shift the hit back to ALL pending indices for this object.
                    if ((hChanged || missesChanged) && _pendingHitIndices.Count > 0)
                    {
                        foreach (int idx in _pendingHitIndices)
                        {
                            var entry = _ppTimeline[idx];
                            // Shift judgment results (Hits, PP, Acc) back to the head/ticks
                            entry[0] = snapshot.PP ?? (double)entry[0];
                            entry[2] = snapshot.Accuracy ?? (double)entry[2];
                            entry[3] = h300;
                            entry[4] = h100;
                            entry[5] = h50;
                            entry[6] = misses;
                        }
                        _pendingHitIndices.Clear();
                    }

                    _lastH300 = h300; _lastH100 = h100; _lastH50 = h50;

                    var stats = new object[] { 
                        snapshot.PP ?? 0,           // 0: PP
                        snapshot.MaxCombo ?? 0,     // 1: Max Combo
                        snapshot.Accuracy ?? 0,     // 2: Accuracy
                        h300,                       // 3: 300s
                        h100,                       // 4: 100s
                        h50,                        // 5: 50s
                        misses,                     // 6: Misses
                        snapshot.TimeMs ?? 0,       // 7: TimeMs
                        combo,                      // 8: CURRENT COMBO
                        eventType                   // 9: EventType
                    };

                    int statsIdx = _ppTimeline.Count;
                    _ppTimeline.Add(stats);
                    if (isPending) _pendingHitIndices.Add(statsIdx);
                    
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
        
        // Basic sanity checks
        if (s.Score < 0 || s.Score > 4000000000) return false; // Max possible score is usually < 2.1B, but 4B is safe garbage threshold
        if (s.Accuracy < 0 || s.Accuracy > 1.0001) return false;
        if (s.Combo < 0 || s.Combo > 20000) return false; // No map has > 20k combo
        if (s.MaxCombo < 0 || s.MaxCombo > 20000) return false;
        
        // PP sanity check
        if (s.PP.HasValue && (!double.IsFinite(s.PP.Value) || s.PP.Value < 0 || s.PP.Value > 100000)) return false;

        // If it's a results screen, we expect some hits
        if (s.StateNumber == 7)
        {
            int totalHits = (s.HitCounts?.Count300 ?? 0) + (s.HitCounts?.Count100 ?? 0) + (s.HitCounts?.Count50 ?? 0) + (s.HitCounts?.Misses ?? 0);
            if (totalHits == 0) return false;
            
            // Score vs Hits sanity check
            if (s.Score > 0 && totalHits > 0)
            {
                double scorePerHit = (double)s.Score / totalHits;
                if (scorePerHit > 2000000) return false; // Impossible score per hit
            }
        }

        return true;
    }

    private void RecordPlay(LiveSnapshot s, bool isPass)
    {
        if (_hasRecordedCurrentPlay) return;
        
        if (s.IsPreview) return;
        _hasRecordedCurrentPlay = true;

        if (s.IsReplay) return;

        if (string.IsNullOrEmpty(s.MD5Hash))
        {
            DebugService.Log("[StableDetector] Skip: Missing MD5", "Detector");
            return;
        }

        string outcome = isPass ? "pass" : "fail";
        DebugService.Log($"[StableDetector] Recording {outcome} for {s.Beatmap}. Score={s.Score}, Acc={s.Accuracy:P2}, Combo={s.MaxCombo}, Hits={s.HitCounts?.Count300}/{s.HitCounts?.Count100}/{s.HitCounts?.Count50}/{s.HitCounts?.Misses}", "Detector");
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
            for (int attempt = 1; attempt <= 5; attempt++)
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
                DebugService.Log($"[StableDetector] Recorded {play.Outcome}.", "Detector");
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
