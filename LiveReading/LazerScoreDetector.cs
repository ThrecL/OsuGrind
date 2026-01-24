using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using OsuGrind.Models;
using OsuGrind.Services;

namespace OsuGrind.LiveReading;

public class LazerScoreDetector
{
    private readonly TrackerDb _db;
    private readonly SoundPlayer _soundPlayer;
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
    private List<object[]> _liveTimeline = new(); // List of [time, combo, eventType]
    private List<object[]> _ppTimeline = new(); // Format: [pp, combo, acc, c300, c100, c50, miss, timeMs, currentCombo, eventType]
    public LiveSnapshot? LastSnapshot { get; private set; }

    public LazerScoreDetector(TrackerDb db, SoundPlayer soundPlayer)
    {
        _db = db;
        _soundPlayer = soundPlayer;
    }

    public event Action<bool>? OnPlayRecorded;

    public void Process(LiveSnapshot snapshot)
    {
        try
        {
            bool isPlaying = snapshot.StateNumber == 2;

            // RETRY DETECTION
            if (isPlaying && !_isReplaySession && _lastState == 2 && LastSnapshot != null)
            {
                double currentTime = snapshot.TimeMs ?? 0;
                if (currentTime < _lastTime - 500 && _lastTime > 1000)
                {
                    if (!_hasRecordedCurrentPlay && LastSnapshot.Failed && !_isReplaySession) RecordPlay(CreateCompletedPlay(LastSnapshot, "fail"), false);
                    _hasRecordedCurrentPlay = false;
                    _wasPlaying = true;
                    _liveTimeline.Clear();
                    _ppTimeline.Clear();
                    _lastCombo = 0; _lastMisses = 0; _lastH300 = 0; _lastH100 = 0; _lastH50 = 0;
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
                    _ppTimeline.Add(new object[] { 0.0, 0, 1.0, 0, 0, 0, 0, 0, 0, 0 });
                    DebugService.Log($"[LazerDetector] Play Started. IsReplay={_isReplaySession}", "Detector");
                }
                _stateBeforeLast = _prevState; _prevState = _lastState; _lastState = snapshot.StateNumber; _lastStateChangeTime = now;
            }

            if (snapshot.StateNumber == 1 || snapshot.StateNumber == 4 || snapshot.StateNumber == 5 || snapshot.StateNumber == 11)
            {
                _hasRecordedCurrentPlay = false; _wasPlaying = false; _isReplaySession = false; _liveTimeline.Clear();
            }

            if (_hasRecordedCurrentPlay) return;

            if (snapshot.ModsList != null && (snapshot.ModsList.Contains("CN") || snapshot.ModsList.Contains("AT") || snapshot.ModsList.Contains("RX") || snapshot.ModsList.Contains("AP"))) return;

            // PASS
            if (snapshot.StateNumber == 7 && _wasPlaying && !_isReplaySession && (snapshot.Score ?? 0) > 0 && !snapshot.Failed)
            {
                DebugService.Log($"[LazerDetector] Recording PASS.", "Detector");
                RecordPlay(CreateCompletedPlay(snapshot, "pass"), true);
            }

            // EVENT-DRIVEN TIMELINE
            if (isPlaying && !_isReplaySession)
            {
                int combo = snapshot.Combo ?? 0;
                int misses = snapshot.HitCounts?.Misses ?? 0;
                int h300 = snapshot.HitCounts?.Count300 ?? 0;
                int h100 = snapshot.HitCounts?.Count100 ?? 0;
                int h50 = snapshot.HitCounts?.Count50 ?? 0;

                bool hChanged = h300 != _lastH300 || h100 != _lastH100 || h50 != _lastH50;
                bool statsChanged = combo != _lastCombo || misses != _lastMisses || hChanged || _ppTimeline.Count == 0;

                if (statsChanged)
                {
                    _lastH300 = h300; _lastH100 = h100; _lastH50 = h50;
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
        return new CompletedPlay
        {
            ScoreId = 0, CreatedAtUtc = DateTime.UtcNow, BeatmapHash = s.MD5Hash ?? "", Beatmap = s.Beatmap,
            Artist = s.Artist ?? "", Title = s.Title ?? "", Version = s.Version ?? "", Mods = s.Mods,
            Outcome = outcome, UR = s.LiveUR, HitOffsets = string.Join(",", s.LiveHitOffsets),
            DurationMs = s.TimeMs ?? s.TotalTimeMs ?? 0, Stars = s.Stars, Accuracy = s.Accuracy ?? 0,
            Score = s.Score ?? 0, MaxCombo = s.MaxCombo ?? s.Combo ?? 0,
            Count300 = s.HitCounts?.Count300 ?? 0, Count100 = s.HitCounts?.Count100 ?? 0,
            Count50 = s.HitCounts?.Count50 ?? 0, Misses = s.HitCounts?.Misses ?? 0, PP = s.PP ?? 0,
            TimelineJson = SerializeTimeline(), PpTimelineJson = SerializePpTimeline(),
            AimOffsetsJson = s.AimOffsetsJson, CS = s.CS ?? 0, AR = s.AR ?? 0, OD = s.OD ?? 0, HP = s.HP ?? 0,
            BPM = s.BPM ?? 0, LengthMs = s.TotalTimeMs ?? 0, Circles = s.Circles ?? 0,
            Sliders = s.Sliders ?? 0, Spinners = s.Spinners ?? 0, BackgroundHash = s.BackgroundHash
        };
    }

    private void RecordPlay(CompletedPlay play, bool isPass)
    {
        if (_hasRecordedCurrentPlay) return;
        _hasRecordedCurrentPlay = true; 
        var row = PlayRow.FromCompleted(play);
        System.Threading.Tasks.Task.Run(async () =>
        {
            try {
                if (!string.IsNullOrEmpty(play.BeatmapHash)) await _db.InsertOrUpdateBeatmapAsync(new BeatmapRow {
                    Hash = play.BeatmapHash, Title = play.Title, Artist = play.Artist, Version = play.Version,
                    CS = play.CS, AR = play.AR, OD = play.OD, HP = play.HP, BPM = play.BPM,
                    LengthMs = play.LengthMs, Circles = play.Circles, Sliders = play.Sliders, Spinners = play.Spinners,
                    MaxCombo = play.MaxCombo, Stars = play.Stars ?? 0, BackgroundHash = play.BackgroundHash, LastPlayedUtc = play.CreatedAtUtc
                });
                await _db.InsertPlayAsync(row);
            } catch (Exception ex) { DebugService.Error($"RecordPlay: DB Error - {ex.Message}", "Detector"); }
        });
        OnPlayRecorded?.Invoke(isPass);
        if (isPass && SettingsManager.Current.PassSoundEnabled) _soundPlayer.PlayPass();
        else if (!isPass && SettingsManager.Current.FailSoundEnabled) _soundPlayer.PlayFail();
    }
    private string SerializeTimeline() => Newtonsoft.Json.JsonConvert.SerializeObject(_liveTimeline);
    private string SerializePpTimeline() => Newtonsoft.Json.JsonConvert.SerializeObject(_ppTimeline);
}
