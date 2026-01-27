using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BMAPI.v1;
using osuDodgyMomentsFinder;
using ReplayAPI;

namespace OsuGrind.Services;

public class MissAnalysisService
{
    public class AnalysisResult
    {
        public double UR { get; set; }
        public List<float> HitErrors { get; set; } = new();
        public double KeyRatio { get; set; }
    }

    public static AnalysisResult Analyze(string osuPath, string osrPath)
    {
        try
        {
            var beatmap = new Beatmap(osuPath);
            var replay = new Replay(osrPath, true, true);
            var analyzer = new ReplayAnalyzer(beatmap, replay);

            // 1. UR and Hit Errors
            var hits = analyzer.hits;
            if (hits == null || hits.Count == 0)
            {
                Console.WriteLine($"[MissAnalysisService] No hits found in replay {osrPath}");
                return new AnalysisResult { UR = 0, HitErrors = new(), KeyRatio = 0.5 };
            }

            var hitErrors = hits.Where(h => h.note != null && h.frame != null).Select(h => h.frame.Time - h.note.StartTime).ToList();
            double ur = 0;
            if (hitErrors.Count > 0)
            {
                double avg = hitErrors.Average();
                double sumSq = hitErrors.Sum(d => Math.Pow(d - avg, 2));
                ur = Math.Sqrt(sumSq / hitErrors.Count) * 10;
            }
            else {
                Console.WriteLine($"[MissAnalysisService] Replay {osrPath} has {hits.Count} raw hits but 0 valid hit errors.");
            }

            // 2. Key Ratio (Hits based)
            // ReplayAPI.Keys: K1 = 4, K2 = 8, M1 = 1, M2 = 2.
            var counts = new Dictionary<ReplayAPI.Keys, int>
            {
                { ReplayAPI.Keys.K1, 0 }, { ReplayAPI.Keys.K2, 0 }, { ReplayAPI.Keys.M1, 0 }, { ReplayAPI.Keys.M2, 0 }
            };

            // TRUTH: If we have hits, use them. If not, fallback to raw frame transitions.
            if (hits != null && hits.Count > 0)
            {
                foreach(var hit in hits)
                {
                    var keyVal = hit.key;
                    bool hasK1 = (keyVal & (ReplayAPI.Keys)(1 << 2)) != 0;
                    bool hasK2 = (keyVal & (ReplayAPI.Keys)(1 << 3)) != 0;
                    bool hasM1 = (keyVal & (ReplayAPI.Keys)(1 << 0)) != 0;
                    bool hasM2 = (keyVal & (ReplayAPI.Keys)(1 << 1)) != 0;

                    if (hasK1) counts[ReplayAPI.Keys.K1]++;
                    else if (hasK2) counts[ReplayAPI.Keys.K2]++;
                    else if (hasM1) counts[ReplayAPI.Keys.M1]++;
                    else if (hasM2) counts[ReplayAPI.Keys.M2]++;
                }
            }
            else
            {
                // Fallback: Detect key-down transitions from raw frames
                ReplayAPI.Keys lastKeys = ReplayAPI.Keys.None;
                foreach (var frame in replay.ReplayFrames)
                {
                    var currentKeys = frame.Keys;
                    var pressed = (currentKeys ^ lastKeys) & currentKeys;
                    
                    bool hasK1 = (pressed & (ReplayAPI.Keys)(1 << 2)) != 0;
                    bool hasK2 = (pressed & (ReplayAPI.Keys)(1 << 3)) != 0;
                    bool hasM1 = (pressed & (ReplayAPI.Keys)(1 << 0)) != 0;
                    bool hasM2 = (pressed & (ReplayAPI.Keys)(1 << 1)) != 0;

                    if (hasK1) counts[ReplayAPI.Keys.K1]++;
                    else if (hasK2) counts[ReplayAPI.Keys.K2]++;
                    else if (hasM1) counts[ReplayAPI.Keys.M1]++;
                    else if (hasM2) counts[ReplayAPI.Keys.M2]++;
                    
                    lastKeys = currentKeys;
                }
            }

            var sortedKeys = counts.Where(x => x.Value > 0).OrderByDescending(x => x.Value).ToList();
            double keyRatio = 0.5;

            // Debug log
            if (sortedKeys.Count > 0)
            {
                string debugInfo = string.Join(", ", sortedKeys.Select(k => $"{k.Key}:{k.Value}"));
                DebugService.Log($"[MissAnalysis] Tapping: {debugInfo}", "Analyzer");
            }
            
            if (sortedKeys.Count >= 2) {
                int top1 = sortedKeys[0].Value;
                int top2 = sortedKeys[1].Value;
                keyRatio = (double)top1 / (top1 + top2);
            }
            else if (sortedKeys.Count == 1) {
                keyRatio = 1.0;
            }

            return new AnalysisResult
            {
                UR = ur,
                HitErrors = hitErrors,
                KeyRatio = keyRatio
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MissAnalysisService] Error: {ex.Message}");
            return new AnalysisResult();
        }
    }
}
