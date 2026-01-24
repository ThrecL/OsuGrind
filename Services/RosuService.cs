using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;

namespace OsuGrind.Services
{
    public class RosuService : IDisposable
    {
        private IntPtr _context = IntPtr.Zero;
        private string? _currentMapPath;
        private bool _isAvailable = false;
        private bool _checkedAvailability = false;

        public bool IsLoaded => _context != IntPtr.Zero;
        public double AR { get; private set; }
        public double CS { get; private set; }
        public double OD { get; private set; }
        public double HP { get; private set; }
        public double BaseBpm { get; private set; }
        public int TotalObjects { get; private set; }
        public int TotalSliders { get; private set; }
        public int TotalCircles { get; private set; }
        public int TotalSpinners { get; private set; }
        public double DrainTime { get; private set; }

        public RosuService()
        {
            CheckAvailability();
        }

        private void CheckAvailability()
        {
            if (_checkedAvailability) return;
            _checkedAvailability = true;
            try
            {
                string dllName = "rosu_pp_wrapper";
                string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib", "rosu_pp_wrapper.dll");
                
                if (File.Exists(localPath))
                {
                    NativeLibrary.Load(localPath);
                }
                else
                {
                    NativeLibrary.Load(dllName);
                }
                
                _isAvailable = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RosuService: Failed to load DLL: {ex.Message}");
                _isAvailable = false;
            }
        }

        public void UpdateContext(string mapPath)
        {
            if (!_isAvailable) return;
            if (_currentMapPath == mapPath && _context != IntPtr.Zero) return;

            if (_context != IntPtr.Zero)
            {
                try { NativeMethods.rosu_destroy_context(_context); } catch { }
                _context = IntPtr.Zero;
            }

            if (File.Exists(mapPath))
            {
                try
                {
                    _context = NativeMethods.rosu_create_context(mapPath);
                    _currentMapPath = mapPath;

                    var attrs = NativeMethods.rosu_get_difficulty_attributes(_context, 0, 1.0, -1, -1, -1, -1);
                    AR = attrs.AR;
                    CS = attrs.CS;
                    OD = attrs.OD;
                    HP = attrs.HP;
                    BaseBpm = NativeMethods.rosu_calculate_bpm(_context, 0);
                    
                    try {
                        var beatmap = OsuParsers.Decoders.BeatmapDecoder.Decode(mapPath);
                        TotalObjects = beatmap.HitObjects.Count;
                        TotalCircles = beatmap.HitObjects.Count(h => h.GetType().Name.Contains("Circle"));
                        TotalSliders = beatmap.HitObjects.Count(h => h.GetType().Name.Contains("Slider"));
                        TotalSpinners = beatmap.HitObjects.Count(h => h.GetType().Name.Contains("Spinner"));
                        
                        if (TotalObjects > 0)
                        {
                            float first = beatmap.HitObjects[0].StartTime;
                            float last = beatmap.HitObjects.Last().StartTime;
                            float breakTime = beatmap.EventsSection.Breaks.Sum(b => b.EndTime - b.StartTime);
                            DrainTime = Math.Max(1, (last - first - breakTime) / 1000.0);
                        }
                        else { DrainTime = 1.0; }
                    } catch { 
                        TotalObjects = 0; 
                        TotalCircles = 0;
                        TotalSliders = 0;
                        TotalSpinners = 0;
                        DrainTime = 1.0;
                    }

                    System.Diagnostics.Debug.WriteLine($"RosuService: Context CREATED for: {mapPath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"RosuService: Failed to create context: {ex.Message}");
                    _context = IntPtr.Zero;
                }
            }
        }

        /// <summary>
        /// Calculate PP for a play.
        /// </summary>
        /// <param name="isLazer">Set to true for osu!lazer scores, false for osu!stable scores</param>
        public double CalculatePp(uint mods, int combo, int n300, int n100, int n50, int nMisses, int passedObjects, int sliderEndHits = 0, int smallTickHits = 0, int largeTickHits = 0, double clockRate = 1.0, double arOverride = -1.0, double csOverride = -1.0, double odOverride = -1.0, double hpOverride = -1.0, bool isLazer = false)
        {
            if (!_isAvailable || _context == IntPtr.Zero) return 0;
            if (clockRate <= 0) clockRate = GetClockRateFromMods(mods);
            
            try { 
                return NativeMethods.rosu_calculate_pp(
                    _context, mods, combo, n300, n100, n50, nMisses, passedObjects, 
                    sliderEndHits, smallTickHits, largeTickHits, 
                    clockRate, arOverride, csOverride, odOverride, hpOverride,
                    isLazer ? 1 : 0); 
            }
            catch { return 0; }
        }

        /// <summary>
        /// Calculate star rating.
        /// </summary>
        /// <param name="isLazer">Set to true for osu!lazer, false for osu!stable</param>
        public double GetStars(uint mods, int passedObjects = 0, double clockRate = 0.0, double arOverride = -1.0, double csOverride = -1.0, double odOverride = -1.0, double hpOverride = -1.0, bool isLazer = false)
        {
            if (!_isAvailable || _context == IntPtr.Zero) return 0;
            try { 
                return NativeMethods.rosu_calculate_stars(
                    _context, mods, passedObjects, clockRate, 
                    arOverride, csOverride, odOverride, hpOverride,
                    isLazer ? 1 : 0); 
            }
            catch { return 0; }
        }

        public double GetBpm(uint mods)
        {
            if (!_isAvailable || _context == IntPtr.Zero) return 0;
            try { return NativeMethods.rosu_calculate_bpm(_context, mods); }
            catch { return 0; }
        }

        public (double AR, double CS, double OD, double HP) GetDifficultyAttributes(uint mods, double clockRate = 1.0, double arOverride = -1.0, double csOverride = -1.0, double odOverride = -1.0, double hpOverride = -1.0)
        {
            if (!_isAvailable || _context == IntPtr.Zero) return (0, 0, 0, 0);
            try
            {
                var attrs = NativeMethods.rosu_get_difficulty_attributes(_context, mods, clockRate, arOverride, csOverride, odOverride, hpOverride);
                return (attrs.AR, attrs.CS, attrs.OD, attrs.HP);
            }
            catch { return (0, 0, 0, 0); }
        }

        public (double AR, double CS, double OD, double HP) GetDifficultyAttributes(string osuFilePath, List<string> mods, double clockRate = 1.0, double arOverride = -1, double csOverride = -1, double odOverride = -1, double hpOverride = -1)
        {
            if (_context == IntPtr.Zero || _currentMapPath != osuFilePath) UpdateContext(osuFilePath);
            if (_context == IntPtr.Zero) return (0, 0, 0, 0);
            uint modsBits = ModsToRosuStats(mods);
            return GetDifficultyAttributes(modsBits, clockRate, arOverride, csOverride, odOverride, hpOverride);
        }

        /// <summary>
        /// Calculate PP for a full combo at the given accuracy.
        /// </summary>
        /// <param name="isLazer">Set to true for osu!lazer, false for osu!stable</param>
        public PerformanceResult CalculatePpIfFc(string osuFilePath, List<string> mods, double acc, double arOverride = -1, double csOverride = -1, double odOverride = -1, double hpOverride = -1, double clockRate = 1.0, bool isLazer = false)
        {
            if (!_isAvailable) return new PerformanceResult();
            if (_context == IntPtr.Zero || _currentMapPath != osuFilePath) UpdateContext(osuFilePath);
            if (_context == IntPtr.Zero) return new PerformanceResult();
            
            uint modsBits = ModsToRosuStats(mods);
            
            try { 
                return NativeMethods.rosu_calculate_pp_if_fc(
                    _context, modsBits, acc, clockRate, 
                    arOverride, csOverride, odOverride, hpOverride,
                    isLazer ? 1 : 0); 
            }
            catch { return new PerformanceResult(); }
        }

        /// <summary>
        /// Calculates the maximum possible score for a perfect SS play in osu! Standard (stable scoring).
        /// Uses the official osu! scoring formula.
        /// </summary>
        public long CalculateStableMaxScore(string osuFilePath, List<string> mods, int maxCombo, double clockRate)
        {
            if (!File.Exists(osuFilePath) || maxCombo <= 0) return 0;
            
            // ScoreV2 always gives 1,000,000
            if (mods != null && mods.Any(m => m.ToUpper() == "SV2" || m.ToUpper() == "V2")) 
                return 1000000;

            try
            {
                var beatmap = OsuParsers.Decoders.BeatmapDecoder.Decode(osuFilePath);

                // Scaling for difficulty multiplier stats (Stable)
                double hp = (double)beatmap.DifficultySection.HPDrainRate;
                double od = (double)beatmap.DifficultySection.OverallDifficulty;
                double cs = (double)beatmap.DifficultySection.CircleSize;

                if (mods != null)
                {
                    if (mods.Any(m => m.ToUpper() == "HR"))
                    {
                        hp = Math.Min(10.0, hp * 1.4);
                        od = Math.Min(10.0, od * 1.4);
                        cs = Math.Min(10.0, cs * 1.3);
                    }
                    else if (mods.Any(m => m.ToUpper() == "EZ"))
                    {
                        hp *= 0.5;
                        od *= 0.5;
                        cs *= 0.5;
                    }
                }

                // Local drain time calculation to avoid stale class property
                double firstObjTime = beatmap.HitObjects.Count > 0 ? (double)beatmap.HitObjects[0].StartTime : 0;
                double lastObjTime = beatmap.HitObjects.Count > 0 ? (double)beatmap.HitObjects.Last().StartTime : 0;
                double breakTime = (double)beatmap.EventsSection.Breaks.Sum(b => b.EndTime - b.StartTime);
                double localDrainTime = Math.Max(1.0, (lastObjTime - firstObjTime - breakTime) / 1000.0);

                // Calculate difficulty multiplier using official formula
                int objectCount = beatmap.HitObjects.Count;
                double densityBonus = Math.Min(16.0, Math.Max(8.0, (double)objectCount / localDrainTime * 8.0));
                double difficultyMultiplier = Math.Round((hp + od + cs + densityBonus) / 38.0 * 5.0);
                
                long totalScore = 0;
                int currentCombo = 0;

                // Mod multiplier for score (cumulative)
                double modMultiplier = 1.0;
                if (mods != null)
                {
                    foreach (var m in mods)
                    {
                        switch (m.ToUpper())
                        {
                            case "EZ": modMultiplier *= 0.5; break;
                            case "NF": modMultiplier *= 0.5; break;
                            case "HT": modMultiplier *= 0.3; break;
                            case "HR": modMultiplier *= 1.06; break;
                            case "DT": modMultiplier *= 1.12; break;
                            case "NC": modMultiplier *= 1.12; break;
                            case "HD": modMultiplier *= 1.06; break;
                            case "FL": modMultiplier *= 1.12; break;
                            case "SO": modMultiplier *= 0.9; break;
                        }
                    }
                }

                foreach (var obj in beatmap.HitObjects)
                {
                    string typeName = obj.GetType().Name;
                    
                    if (typeName.Contains("Circle"))
                    {
                        // Circle hit: 300 + combo bonus
                        totalScore += 300 + (long)Math.Floor(300.0 * currentCombo * difficultyMultiplier * modMultiplier / 25.0);
                        currentCombo++;
                    }
                    else if (typeName.Contains("Slider"))
                    {
                        dynamic slider = obj;
                        int repeats = 0;
                        int ticks = 0;
                        
                        try
                        {
                            // Fix property name: OsuParsers uses RepeatCount
                            repeats = (int)slider.RepeatCount;
                            
                            // Calculate slider ticks accurately
                            double tickRate = (double)beatmap.DifficultySection.SliderTickRate;
                            double pixelLength = (double)slider.PixelLength;
                            double sliderMultiplier = (double)beatmap.DifficultySection.SliderMultiplier;
                            
                            if (pixelLength > 0 && sliderMultiplier > 0 && tickRate > 0)
                            {
                                // Find the timing points for this slider
                                double sliderTime = (double)slider.StartTime;
                                
                                // Last non-inherited timing point
                                var activeTp = beatmap.TimingPoints.Where(t => !t.Inherited && t.Offset <= sliderTime + 1).OrderByDescending(t => t.Offset).FirstOrDefault();
                                // Last timing point (inherited or not)
                                var lastTp = beatmap.TimingPoints.Where(t => t.Offset <= sliderTime + 1).OrderByDescending(t => t.Offset).FirstOrDefault();
                                
                                double beatLength = activeTp?.BeatLength ?? 500;
                                double velocityMultiplier = 1.0;
                                
                                if (lastTp != null && lastTp.Inherited && lastTp.BeatLength < 0)
                                {
                                    velocityMultiplier = Math.Clamp(-100.0 / lastTp.BeatLength, 0.1, 10.0);
                                }
                                
                                double distancePerTick = (100.0 * sliderMultiplier * velocityMultiplier) / tickRate;
                                if (distancePerTick > 0)
                                {
                                    ticks = (int)Math.Max(0, Math.Ceiling(pixelLength / distancePerTick - 0.01) - 1);
                                    ticks *= (repeats + 1);
                                }
                            }
                        }
                        catch { }
                        
                        // Slider head (gives 30 pts, no combo bonus)
                        totalScore += 30;
                        currentCombo++;
                        
                        // Slider ticks (10 pts each, no combo bonus)
                        for (int i = 0; i < ticks; i++)
                        {
                            totalScore += 10;
                            currentCombo++;
                        }
                        
                        // Slider repeats/arrows (30 pts each, no combo bonus)
                        for (int i = 0; i < repeats; i++)
                        {
                            totalScore += 30;
                            currentCombo++;
                        }
                        
                        // Slider end/tail (gives 300 judgment with combo bonus)
                        totalScore += 300 + (long)Math.Floor(300.0 * currentCombo * difficultyMultiplier * modMultiplier / 25.0);
                        currentCombo++;
                    }
                    else if (typeName.Contains("Spinner"))
                    {
                        // Spinner completion gives 300 + combo bonus
                        totalScore += 300 + (long)Math.Floor(300.0 * currentCombo * difficultyMultiplier * modMultiplier / 25.0);
                        currentCombo++;
                        
                        // Add estimated spinner bonus (varies by spinner length)
                        try
                        {
                            dynamic spinner = obj;
                            double spinnerLength = (double)spinner.EndTime - (double)spinner.StartTime;
                            // Roughly 1 bonus spin per 100ms for skilled players
                            int bonusSpins = Math.Max(0, (int)(spinnerLength / 100) - 3);
                            totalScore += bonusSpins * 1000;
                        }
                        catch { }
                    }
                }

                return totalScore;
            }
            catch (Exception ex)
            { 
                System.Diagnostics.Debug.WriteLine($"RosuService: CalculateStableMaxScore error: {ex.Message}");
                return 0; 
            }
        }

        public static uint ModsToRosuStats(List<string> mods)
        {
            uint val = 0;
            if (mods == null) return 0;
            foreach (var m in mods)
            {
                switch (m.ToUpper())
                {
                    case "NF": val |= 1; break;
                    case "EZ": val |= 2; break;
                    case "TD": val |= 4; break;
                    case "HD": val |= 8; break;
                    case "HR": val |= 16; break;
                    case "SD": val |= 32; break;
                    case "DT": val |= 64; break;
                    case "RX": val |= 128; break;
                    case "HT": val |= 256; break;
                    case "NC": val |= 512 | 64; break;
                    case "FL": val |= 1024; break;
                    case "AT": val |= 2048; break;
                    case "SO": val |= 4096; break;
                    case "AP": val |= 8192; break;
                    case "PF": val |= 16384 | 32; break;
                    case "DC": val |= 256; break; // Daycore is HT variant
                    case "CL": val |= (1u << 24); break;
                    case "MR": val |= (1u << 30); break; // Mirror (if supported)
                    case "BL": val |= (1u << 31); break; // Blind (if supported)
                }
            }
            return val;
        }

        public static double GetClockRateFromMods(uint mods)
        {
            if ((mods & 512) != 0 || (mods & 64) != 0) return 1.5;
            if ((mods & 256) != 0) return 0.75;
            return 1.0;
        }

        public void Dispose()
        {
            if (_context != IntPtr.Zero)
            {
                try { NativeMethods.rosu_destroy_context(_context); } catch { }
                _context = IntPtr.Zero;
            }
        }

        ~RosuService() { Dispose(); }

        public static string? GetBeatmapPath(string hash, string? lazerPath = null)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length < 2) return null;
            try
            {
                string baseDir = !string.IsNullOrEmpty(lazerPath) && Directory.Exists(lazerPath) ? lazerPath : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu");
                string path = Path.Combine(baseDir, "files", hash.Substring(0, 1), hash.Substring(0, 2), hash);
                return File.Exists(path) ? path : null;
            }
            catch { return null; }
        }
    }
}
