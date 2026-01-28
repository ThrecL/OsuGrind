using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using OsuGrind.Models;
using OsuGrind.Services;
using OsuGrind.Api;

namespace OsuGrind.LiveReading;

public class LazerScoreDetector
{
    private readonly TrackerDb _db;
    private readonly SoundPlayer _soundPlayer;
    private readonly ApiServer _api;
    private bool _hasRecordedCurrentPlay;
    private int _lastState;
    private int _prevState;
    private int _stateBeforeLast;
    private DateTime _lastStateChangeTime;

    private bool _wasPlaying;
    private bool _isReplaySession;
    
    private double _lastTime;
    private int _lastCombo;
    private int _lastMisses;
    private int _lastH300, _lastH100, _lastH50;
    private int _lastSliderTail, _lastSmallTick, _lastLargeTick;
    private List<object[]> _liveTimeline = new();
    private List<object[]> _ppTimeline = new();

    public LiveSnapshot? LastSnapshot { get; private set; }

    private static DateTime _lastGoalSoundDate = DateTime.MinValue;

    public LazerScoreDetector(TrackerDb db, SoundPlayer soundPlayer, ApiServer api)
    {
        _db = db;
        _soundPlayer = soundPlayer;
        _api = api;
    }

    public event Action<bool>? OnPlayRecorded;

    public void Process(LiveSnapshot snapshot)
    {
        try
        {
            bool isPlaying = snapshot.StateNumber == 2;

            if (isPlaying && !_isReplaySession && _lastState == 2 && LastSnapshot != null)
            {
                double currentTime = snapshot.TimeMs ?? 0;
                if (currentTime < _lastTime - 500 && _lastTime > 1000)
                {
                    if (!_hasRecordedCurrentPlay && LastSnapshot.Failed && !_isReplaySession) 
                    {
                         DebugService.Log($"[LazerDetector] Retry detected while failed. Recording Fail.", "Detector");
                         RecordPlay(LastSnapshot, false);
                    }
                    
                    _hasRecordedCurrentPlay = false;
                    _wasPlaying = true;
                    _liveTimeline.Clear();
                    _ppTimeline.Clear();
                    _lastCombo = 0; _lastMisses = 0; _lastH300 = 0; _lastH100 = 0; _lastH50 = 0;
                    _lastSliderTail = 0; _lastSmallTick = 0; _lastLargeTick = 0;
                    _ppTimeline.Add(new object[] { 0.0, 0, 1.0, 0, 0, 0, 0, 0, 0, 0 });
                }
            }

            _lastTime = snapshot.TimeMs ?? 0;
            LastSnapshot = snapshot;

            if (isPlaying && _isReplaySession && !snapshot.IsReplay) _isReplaySession = false;

            if (snapshot.StateNumber == 0) return;

            if (snapshot.StateNumber != _lastState)
            {
                var now = DateTime.Now;
                if (isPlaying && _lastState != 2)
                {
                    _hasRecordedCurrentPlay = false;
                    _wasPlaying = true;
                    _isReplaySession = snapshot.IsReplay;
                    _liveTimeline.Clear();
                    _ppTimeline.Clear();
                    _lastCombo = 0; _lastMisses = 0; _lastH300 = 0; _lastH100 = 0; _lastH50 = 0;
                    _lastSliderTail = 0; _lastSmallTick = 0; _lastLargeTick = 0;
                    
                    _ppTimeline.Add(new object[] { 0.0, 0, 1.0, 0, 0, 0, 0, 0, 0, 0 });
                    
                    DebugService.Log($"[LazerDetector] Play Started. IsReplay={_isReplaySession}", "Detector");
                }
                _stateBeforeLast = _prevState; _prevState = _lastState; _lastState = snapshot.StateNumber; _lastStateChangeTime = now;
            }

            if (snapshot.StateNumber == 1 || snapshot.StateNumber == 4 || snapshot.StateNumber == 5 || snapshot.StateNumber == 11)
            {
                if (_wasPlaying && !_hasRecordedCurrentPlay && LastSnapshot != null && LastSnapshot.Failed && !_isReplaySession)
                {
                    DebugService.Log($"[LazerDetector] Exited to menu while failed. Recording Fail.", "Detector");
                    RecordPlay(LastSnapshot, false);
                }
                _hasRecordedCurrentPlay = false; _wasPlaying = false; _isReplaySession = false; _liveTimeline.Clear();
            }

            if (_hasRecordedCurrentPlay) return;

            if (snapshot.ModsList != null && (snapshot.ModsList.Contains("CN") || snapshot.ModsList.Contains("AT") || snapshot.ModsList.Contains("RX") || snapshot.ModsList.Contains("AP"))) return;

            if (snapshot.StateNumber == 7 && _wasPlaying && !_isReplaySession && !_hasRecordedCurrentPlay)
            {
                if ((snapshot.Score ?? 0) > 0)
                {
                    DebugService.Log($"[LazerDetector] Recording PASS. Score={snapshot.Score}, Acc={snapshot.Accuracy:P2}", "Detector");
                    RecordPlay(snapshot, true);
                }
            }

            if (isPlaying && !_isReplaySession)
            {
                int combo = snapshot.Combo ?? 0;
                int misses = snapshot.HitCounts?.Misses ?? 0;
                int h300 = snapshot.HitCounts?.Count300 ?? 0;
                int h100 = snapshot.HitCounts?.Count100 ?? 0;
                int h50 = snapshot.HitCounts?.Count50 ?? 0;
                int sliderTail = snapshot.HitCounts?.SliderTailHit ?? 0;
                int smallTick = snapshot.HitCounts?.SmallTickHit ?? 0;
                int largeTick = snapshot.HitCounts?.LargeTickHit ?? 0;

                bool hChanged = h300 != _lastH300 || h100 != _lastH100 || h50 != _lastH50;
                bool sliderChanged = sliderTail != _lastSliderTail || smallTick != _lastSmallTick || largeTick != _lastLargeTick;
                bool statsChanged = combo != _lastCombo || misses != _lastMisses || hChanged || sliderChanged || _ppTimeline.Count == 0;

                if (statsChanged)
                {
                    _lastH300 = h300; _lastH100 = h100; _lastH50 = h50;
                    _lastSliderTail = sliderTail; _lastSmallTick = smallTick; _lastLargeTick = largeTick;
                    int eventType = (misses > _lastMisses) ? 1 : (combo < _lastCombo && misses == _lastMisses ? 2 : 0);

                    var stats = new object[] { 
                        snapshot.PP ?? 0, snapshot.MaxCombo ?? 0, snapshot.Accuracy ?? 0,
                        h300, h100, h50, misses, snapshot.TimeMs ?? 0, combo, eventType
                    };

                    _ppTimeline.Add(stats);

                    if (combo != _lastCombo || misses != _lastMisses)
                    {
                        double timeMs = snapshot.TimeMs ?? 0;
                        if (timeMs > 300 || _liveTimeline.Count > 0) _liveTimeline.Add(new object[] { timeMs, combo, eventType });
                        _lastCombo = combo; _lastMisses = misses;
                    }
                }
            }
        }
        catch (Exception ex) { DebugService.Exception(ex, "Process", "Detector"); }
    }

    private CompletedPlay CreateCompletedPlay(LiveSnapshot s, string outcome)
    {
        string hitErrorsJson = "";
        if (s.LiveHitOffsets != null && s.LiveHitOffsets.Count > 0)
        {
            try {
                hitErrorsJson = System.Text.Json.JsonSerializer.Serialize(s.LiveHitOffsets);
            } catch { }
        }

        return new CompletedPlay
        {
            ScoreId = 0, 
            CreatedAtUtc = DateTime.UtcNow, // Use current time for live plays to ensure proper ordering
            BeatmapHash = s.MD5Hash ?? "", 
            Beatmap = s.Beatmap,
            Artist = s.Artist ?? "", 
            Title = s.Title ?? "", 
            Version = s.Version ?? "", 
            Mods = s.Mods,
            Outcome = outcome, 
            UR = s.LiveUR, 
            HitOffsets = string.Join(",", s.LiveHitOffsets ?? new List<double>()),
            HitErrorsJson = !string.IsNullOrEmpty(s.HitErrorsJson) ? s.HitErrorsJson : hitErrorsJson,
            DurationMs = s.TimeMs ?? s.TotalTimeMs ?? 0, 
            Stars = s.Stars, 
            Accuracy = s.Accuracy ?? 0,
            Score = s.Score ?? 0, 
            MaxCombo = s.MaxCombo ?? s.Combo ?? 0,
            Count300 = s.HitCounts?.Count300 ?? 0, 
            Count100 = s.HitCounts?.Count100 ?? 0,
            Count50 = s.HitCounts?.Count50 ?? 0, 
            Misses = s.HitCounts?.Misses ?? 0, 
            PP = s.PP ?? 0,
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
            ReplayHash = s.ReplayHash ?? "",
            KeyRatio = s.KeyRatio,
            MapPath = s.MapPath ?? ""
        };
    }


    private bool IsGoalMet(TrackerDb.GoalProgress p)
    {
        int targetPlays = SettingsManager.Current.GoalPlays;
        int targetHits = SettingsManager.Current.GoalHits;
        double targetStars = SettingsManager.Current.GoalStars;
        int targetPP = SettingsManager.Current.GoalPP;

        if (targetPlays > 0 && p.Plays < targetPlays) return false;
        if (targetHits > 0 && p.Hits < targetHits) return false;
        if (targetStars > 0 && p.StarPlays < 1) return false;
        if (targetPP > 0 && p.TotalPP < targetPP) return false;
        return true;
    }

    private void RecordPlay(LiveSnapshot s, bool isPass)
    {
        if (_hasRecordedCurrentPlay) return;
        if (s.IsPreview || s.IsReplay) return;
        _hasRecordedCurrentPlay = true; 

        string outcome = isPass ? "pass" : "fail";
        DebugService.Log($"[LazerDetector] Saving {outcome} to DB. Score={s.Score}, Acc={s.Accuracy:P2}, MapPath={s.MapPath}", "Detector");
        var play = CreateCompletedPlay(s, outcome);
        
        System.Threading.Tasks.Task.Run(async () =>
        {
            try {
                var row = PlayRow.FromCompleted(play);

                if (!string.IsNullOrEmpty(play.BeatmapHash)) await _db.InsertOrUpdateBeatmapAsync(new BeatmapRow {
                    Hash = play.BeatmapHash, Title = play.Title, Artist = play.Artist, Version = play.Version,
                    CS = play.CS, AR = play.AR, OD = play.OD, HP = play.HP, BPM = play.BPM,
                    LengthMs = play.LengthMs, Circles = play.Circles, Sliders = play.Sliders, Spinners = play.Spinners,
                    MaxCombo = play.MaxCombo, Stars = play.Stars ?? 0, BackgroundHash = play.BackgroundHash, LastPlayedUtc = play.CreatedAtUtc
                });
                
                long newId = await _db.InsertPlayAsync(row);
                row.Id = newId;
                await _api.BroadcastRefresh(); // Refresh UI IMMEDIATELY with memory data (UR + Histogram)

                // Goal Sound Logic (5s delay)
                _ = Task.Run(async () => {
                    if (!SettingsManager.Current.GoalSoundEnabled) return;
                    if (_lastGoalSoundDate.Date == DateTime.Today) return;

                    await Task.Delay(5000);
                    var progress = await _db.GetTodayGoalProgressAsync(SettingsManager.Current.GoalStars);
                    if (IsGoalMet(progress)) {
                        _lastGoalSoundDate = DateTime.Today;
                        DebugService.Log("[LazerDetector] Goals completed! Playing streak.ogg", "Detector");
                        _soundPlayer.PlayStreak();
                    }
                });

                // Discover replay and run detailed tapping analysis in the background
                if (isPass)
                {
                    try {
                        var exportService = new RealmExportService();
                        string? replayPath = null;
                        
                        // Lazer can be slow to write scores to Realm. Retry a few times.
                        for (int attempt = 1; attempt <= 3; attempt++)
                        {
                            DebugService.Log($"[LazerDetector] Searching for replay (Attempt {attempt})...", "Detector");
                            replayPath = await exportService.SearchReplayByTimeAsync(play.CreatedAtUtc, TimeSpan.FromMinutes(2), play.BeatmapHash);
                            if (!string.IsNullOrEmpty(replayPath) && File.Exists(replayPath)) break;
                            await Task.Delay(2000 * attempt);
                        }
                        
                        if (!string.IsNullOrEmpty(replayPath) && File.Exists(replayPath))
                        {
                            string mapPath = play.MapPath;
                            if (string.IsNullOrEmpty(mapPath) || !File.Exists(mapPath))
                            {
                                DebugService.Log($"[LazerDetector] MapPath missing or invalid: '{mapPath}'. Attempting lookup by hash...", "Detector");
                                // Try one last time to find the map path if it wasn't in the snapshot
                                mapPath = RosuService.GetBeatmapPath(play.BeatmapHash, SettingsManager.Current.LazerPath);
                            }

                            if (!string.IsNullOrEmpty(mapPath) && File.Exists(mapPath))
                            {
                                DebugService.Log($"[LazerDetector] Starting MissAnalysis for {play.BeatmapHash}...", "Detector");
                                var analysis = MissAnalysisService.Analyze(mapPath, replayPath);
                                
                                row.UR = analysis.UR;
                                row.HitErrorsJson = System.Text.Json.JsonSerializer.Serialize(analysis.HitErrors);
                                row.KeyRatio = analysis.KeyRatio;
                                row.ReplayFile = replayPath;
                                
                                // Update the row in DB with detailed analysis (Tapping stats)
                                await _db.UpdatePlayAsync(row);
                                await _api.BroadcastRefresh(); // Refresh UI again for tapping stats
                                DebugService.Log($"[LazerDetector] Background analysis successful! UR={row.UR:F2}, KeyRatio={row.KeyRatio:P1}, Hits={analysis.HitErrors.Count}", "Detector");
                            }
                            else
                            {
                                DebugService.Error($"[LazerDetector] Analysis failed: Could not locate map file for hash {play.BeatmapHash}", "Detector");
                            }
                        }
                        else
                        {
                            DebugService.Log($"[LazerDetector] Replay not found in Realm after 3 attempts.", "Detector");
                        }
                    } catch (Exception ex) {
                         DebugService.Error($"[LazerDetector] Background Analysis failed: {ex.Message}", "Detector");
                    }
                }
            } catch (Exception ex) { DebugService.Error($"RecordPlay: DB Error - {ex.Message}", "Detector"); }
        });

        OnPlayRecorded?.Invoke(isPass);
        if (isPass && SettingsManager.Current.PassSoundEnabled) _soundPlayer.PlayPass();
        else if (!isPass && SettingsManager.Current.FailSoundEnabled) _soundPlayer.PlayFail();
    }
    private string SerializeTimeline() => Newtonsoft.Json.JsonConvert.SerializeObject(_liveTimeline);
    private string SerializePpTimeline() => Newtonsoft.Json.JsonConvert.SerializeObject(_ppTimeline);
}
