using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using OsuGrind.Models;
using OsuGrind.Services;
using OsuGrind.Api;

namespace OsuGrind.LiveReading
{
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
        private int _circles;
        private int _sliders;
        private int _spinners;
        private double _staticStars;
        private double _staticBpm;
        private double _minBpm;
        private double _maxBpm;
        private double _baseMinBpm;
        private double _baseMaxBpm;
        private double _baseModeBpm;
        private int _totalObjects;
        public RosuService _rosuService;
        // PP+ REMOVED\n        // public PlusDifficultyService _plusService = new PlusDifficultyService();
        private Dictionary<IntPtr, string> _modVTableMap = new();
        private int _lastGamemode = -1;

        private static readonly Dictionary<int, Dictionary<string, string[]>> ModsCategories = new()
        {
            [0] = new() { // osu
                ["Reduction"] = new[] { "EZ", "NF", "HT", "DC" },
                ["Increase"] = new[] { "HR", "SD", "PF", "DT", "NC", "HD", "FL", "BL", "ST", "AC" },
                ["Automation"] = new[] { "AT", "CN", "RX", "AP", "SO" },
                ["Conversion"] = new[] { "TP", "DA", "CL", "RD", "MR", "AL", "SG" },
                ["Fun"] = new[] { "TR", "WG", "SI", "GR", "DF", "WU", "WD", "TC", "BR", "AD", "MU", "NS", "MG", "RP", "AS", "FR", "BU", "SY", "DP", "BM" },
                ["System"] = new[] { "TD", "SV2" }
            },
            [1] = new() { // taiko
                ["Reduction"] = new[] { "EZ", "NF", "HT", "DC", "SR" },
                ["Increase"] = new[] { "HR", "SD", "PF", "DT", "NC", "HD", "FL", "AC" },
                ["Automation"] = new[] { "AT", "CN", "RX" },
                ["Conversion"] = new[] { "RD", "DA", "CL", "SW", "SG", "CS" },
                ["Fun"] = new[] { "WU", "WD", "MU", "AS" },
                ["System"] = new[] { "TD", "SV2" }
            },
            [2] = new() { // catch
                ["Reduction"] = new[] { "EZ", "NF", "HT", "DC" },
                ["Increase"] = new[] { "HR", "SD", "PF", "DT", "NC", "HD", "FL", "AC" },
                ["Automation"] = new[] { "AT", "CN", "RX" },
                ["Conversion"] = new[] { "DA", "CL", "MR" },
                ["Fun"] = new[] { "WU", "WD", "FF", "MU", "NS", "MF" },
                ["System"] = new[] { "TD", "SV2" }
            },
            [3] = new() { // mania
                ["Reduction"] = new[] { "EZ", "NF", "HT", "DC", "NR" },
                ["Increase"] = new[] { "HR", "SD", "PF", "DT", "NC", "FI", "HD", "CO", "FL", "AC" },
                ["Automation"] = new[] { "AT", "CN" },
                ["Conversion"] = new[] { "RD", "DS", "MR", "DA", "CL", "IN", "CS", "HO", "1K", "2K", "3K", "4K", "5K", "6K", "7K", "8K", "9K", "10K" },
                ["Fun"] = new[] { "WU", "WD", "MU", "AS" },
                ["System"] = new[] { "TD", "SV2" }
            }
        };
        private string? _lastResolvedMd5;
        private DateTime _lastConnectionAttempt = DateTime.MinValue;
        private CachedBeatmapStats? _cachedStats;
        private DateTime _lastTimeChange = DateTime.Now;
        private DateTime _lastScreenScan = DateTime.MinValue;
        private IntPtr _cachedCurrentScreen = IntPtr.Zero;
        private DateTime _lastModScan = DateTime.MinValue;
        private DateTime _lastBeatmapInfoScan = DateTime.MinValue;
        private RawBeatmapInfo? _cachedRawBeatmapInfo;

        private LazerScoreDetector? _detector;
        private IntPtr _lastResultScoreInfoPtr = IntPtr.Zero;
        private float _baseCS;
        private float _baseAR;
        private float _baseOD;
        private float _baseHP;
        private double _baseStars;

        private class CachedBeatmapStats
        {
            public string MD5Hash { get; set; } = string.Empty;
            public uint RosuMods { get; set; }
            public double ClockRate { get; set; }
            public int? BPM { get; set; }
            public int TotalTimeMs { get; set; }
            public double MapLength { get; set; } // From rosu-pp
            public float CS { get; set; }
            public float AR { get; set; }
            public float OD { get; set; }
            public float HP { get; set; }
            public double PPIfFC { get; set; }
            public double Stars { get; set; }
            public int MaxCombo { get; set; }
            public long MaxScore { get; set; }
        }

        // Constructor
        public LazerMemoryReader(TrackerDb db, SoundPlayer soundPlayer, ApiServer api)
        {
            _rosuService = new RosuService();
            _detector = new LazerScoreDetector(db, soundPlayer, api);
            DebugLog("LazerMemoryReader constructor CALLED.");
        }

        public void Dispose()
        {
            _scanner?.Dispose();
            _rosuService?.Dispose();
        }

        private const string ScalingContainerTargetDrawSizePattern = "00 00 80 44 00 00 40 44 00 00 00 00 ?? ?? ?? ?? 00 00 00 00";

        public bool IsConnected => _process != null && !_process.HasExited && _gameBaseAddress != IntPtr.Zero;
        public bool IsScanning { get; private set; }
        public string ProcessName => "Lazer";
        
        // Interface members
        #pragma warning disable CS0067
        public event Action<bool>? OnPlayRecorded;
        #pragma warning restore CS0067
        public LiveSnapshot? LastRecordedSnapshot { get; private set; }


        public void Initialize()
        {
            DebugLog("Initialize CALLED.");

            // Load offsets from JSON if not already loaded
            if (!Offsets.IsLoaded)
            {
                Offsets.Load();
                WriteLog($"Loaded offsets for osu! version: {Offsets.Version}");
            }

            // If already connected and process is alive, don't re-init
            if (IsConnected) return;

            if ((DateTime.Now - _lastConnectionAttempt).TotalSeconds < 5)
            {
                DebugLog("Initialize: Throttled (last attempt < 5s ago).");
                return;
            }
            _lastConnectionAttempt = DateTime.Now;

            DebugLog("Initializing LazerMemoryReader (Scanning processes)...");
            var processes = Process.GetProcessesByName("osu").Concat(Process.GetProcessesByName("osu!")).ToArray();
            if (processes.Length == 0)
            {
                WriteLog("No 'osu' or 'osu!' process found.");
                _process = null!;
                _gameBaseAddress = IntPtr.Zero;
                return;
            }

            _process = processes.First();
            WriteLog($"Found process: {_process.ProcessName} (ID: {_process.Id})");

            if (_scanner != null) _scanner.Dispose();
            _scanner = new MemoryScanner(_process);

            _modVTableMap.Clear();
            UpdateGameBaseAddress();
        }

        public void UpdateGameBaseAddress()
        {
            if (_scanner == null) return;

            WriteLog("Scanning for pattern...");
            IsScanning = true;
            try
            {
                // Try optimized scan first (Private/Heap memory only - skips file mappings)
                WriteLog("Scanning private memory regions...");
                var candidates = _scanner.ScanAll(ScalingContainerTargetDrawSizePattern, false, false, false, true);

                if (candidates.Count == 0)
                {
                    WriteLog("Optimization candidates empty. Trying full scan (slow)...");
                    candidates = _scanner.ScanAll(ScalingContainerTargetDrawSizePattern, false, false, false, false);
                }

                WriteLog($"Pattern scan found {candidates.Count} candidates.");

                foreach (var patternAddr in candidates)
                {
                    IntPtr externalLinkOpenerAddr = _scanner.ReadIntPtr(IntPtr.Subtract(patternAddr, 0x24));
                    // WriteLog($"Checking Candidate {patternAddr:X} -> ExtLink {externalLinkOpenerAddr:X}");
                    
                    if (externalLinkOpenerAddr == IntPtr.Zero) continue;

                    IntPtr apiAddr = _scanner.ReadIntPtr(IntPtr.Add(externalLinkOpenerAddr, Offsets.ExternalLinkOpener.api));
                    
                    if (apiAddr == IntPtr.Zero) continue;

                    IntPtr gameBase = _scanner.ReadIntPtr(IntPtr.Add(apiAddr, Offsets.APIAccess.game));
                   
                    if (gameBase != IntPtr.Zero)
                    {
                        WriteLog($"Valid GameBase found: {gameBase:X} (via candidate {patternAddr:X})");
                        _gameBaseAddress = gameBase;
                        
                        _modVTableMap.Clear(); // Force rebuild
                        BuildModVTableMap();
                        DebugLog("LazerMemoryReader Initialized.");
                        return;
                    }
                }

                WriteLog("UpdateGameBaseAddress: No valid candidates found.");
                _gameBaseAddress = IntPtr.Zero;
            }
            finally
            {
                IsScanning = false;
            }
        }

        private int ReadGamemode()
        {
            if (_gameBaseAddress == IntPtr.Zero) return 0;
            try
            {
                IntPtr rulesetBindable = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameDesktop.Ruleset));
                if (rulesetBindable == IntPtr.Zero) return 0;
                IntPtr rulesetInfo = _scanner.ReadIntPtr(IntPtr.Add(rulesetBindable, 0x20)); // Bindable<RulesetInfo>.Value
                if (rulesetInfo == IntPtr.Zero) return 0;

                int gamemode = _scanner.ReadInt32(IntPtr.Add(rulesetInfo, Offsets.RulesetInfo.OnlineID));
                WriteLog($"ReadGamemode: {gamemode}");
                return (gamemode >= 0 && gamemode <= 3) ? gamemode : 0;
            }
            catch { return 0; }
        }

        private void BuildModVTableMap()
        {
            int gamemode = ReadGamemode();
            BuildModVTableMap(gamemode);
        }

        private static readonly HashSet<string> KnownAcronyms = new() { 
            "EZ", "NF", "HT", "DC", "HR", "SD", "PF", "DT", "NC", "HD", "FL", "BL", "ST", "AC",
            "TP", "DA", "CL", "RD", "MR", "AL", "SG", "AT", "CN", "RX", "AP", "SO",
            "TR", "WG", "SI", "GR", "DF", "WU", "WD", "TC", "BR", "AD", "MU", "NS", "MG", "RP",
            "AS", "FR", "BU", "SY", "DP", "BM", "TD", "SV2", "1K", "2K", "3K", "4K", "5K", "6K", "7K", "8K", "9K", "10K"
        };

        private string? TryReadAcronymDirectly(IntPtr modPtr)
        {
            if (modPtr == IntPtr.Zero) return null;
            // Scan the object for a string pointer.
            // Acronym is usually one of the first fields or returned by a property.
            // If it's a field, it will be a pointer to a string object.
            for (int offset = 0; offset <= 0x60; offset += 8)
            {
                try
                {
                    IntPtr potPtr = _scanner.ReadIntPtr(IntPtr.Add(modPtr, offset));
                    if (potPtr == IntPtr.Zero) continue;
                    
                    string s = _scanner.ReadString(potPtr);
                    if (!string.IsNullOrEmpty(s) && s.Length >= 2 && s.Length <= 4 && KnownAcronyms.Contains(s.ToUpper()))
                    {
                        return s.ToUpper();
                    }
                }
                catch { }
            }
            return null;
        }

        private void BuildModVTableMap(int gamemode)
        {
            if (_gameBaseAddress == IntPtr.Zero) return;
            DebugLog($"BuildModVTableMap STARTED for gamemode {gamemode}.");
            _modVTableMap.Clear();
            _lastGamemode = gamemode;

            try
            {
                IntPtr availableModsBindable = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameDesktop.AvailableMods));
                if (availableModsBindable == IntPtr.Zero) return;

                // Bindable<T> Value is usually at 0x10, 0x18, 0x20, or 0x28
                IntPtr availableModsDict = IntPtr.Zero;
                int[] dictOffsets = { 0x20, 0x10, 0x18, 0x28, 0x30 };
                foreach (var off in dictOffsets)
                {
                    IntPtr pot = _scanner.ReadIntPtr(IntPtr.Add(availableModsBindable, off));
                    if (pot != IntPtr.Zero)
                    {
                        // Basic check for Dictionary: has _entries at 0x10 or 0x18?
                        IntPtr entries = _scanner.ReadIntPtr(IntPtr.Add(pot, 0x10));
                        if (entries != IntPtr.Zero) { availableModsDict = pot; break; }
                    }
                }
                
                if (availableModsDict == IntPtr.Zero) return;

                IntPtr entriesArray = _scanner.ReadIntPtr(IntPtr.Add(availableModsDict, 0x10));
                int count = _scanner.ReadInt32(IntPtr.Add(availableModsDict, 0x38)); // _count
                
                WriteLog($"BuildModVTableMap: dict={availableModsDict:X}, entries={entriesArray:X}, count={count}");
                
                if (entriesArray == IntPtr.Zero || count <= 0 || count > 150) return;

                string[] categoryNames = { "Reduction", "Increase", "Conversion", "Automation", "Fun", "System" };

                for (int i = 0; i < count; i++)
                {
                    IntPtr entryPtr = IntPtr.Add(entriesArray, 0x10 + i * 0x18);
                    
                    // .NET Dictionary Entry: hash, next, key, value
                    // Try to find the Key (0-5) and Value (Pointer)
                    IntPtr modsListPtr = IntPtr.Zero;
                    int modType = -1;

                    // Scan the entry (24 bytes) for a valid ModType and List pointer
                    for (int off = 0; off <= 16; off += 8)
                    {
                        int potType = _scanner.ReadInt32(IntPtr.Add(entryPtr, off));
                        if (potType >= 0 && potType < categoryNames.Length)
                        {
                            // If this is the key, the value is likely at off+8 or off-8
                            IntPtr v1 = (off + 8 <= 16) ? _scanner.ReadIntPtr(IntPtr.Add(entryPtr, off + 8)) : IntPtr.Zero;
                            IntPtr v2 = (off - 8 >= 0) ? _scanner.ReadIntPtr(IntPtr.Add(entryPtr, off - 8)) : IntPtr.Zero;
                            
                            if (v1 != IntPtr.Zero && v1.ToInt64() > 0x10000) { modsListPtr = v1; modType = potType; break; }
                            if (v2 != IntPtr.Zero && v2.ToInt64() > 0x10000) { modsListPtr = v2; modType = potType; break; }
                        }
                    }

                    if (modsListPtr == IntPtr.Zero) continue;

                    string categoryName = categoryNames[modType];
                    if (!ModsCategories.ContainsKey(gamemode) || !ModsCategories[gamemode].ContainsKey(categoryName)) continue;

                    string[] expectedAcronyms = ModsCategories[gamemode][categoryName];

                    // List<T> layout: _items (0x8), _size (0x10)
                    int listSize = _scanner.ReadInt32(IntPtr.Add(modsListPtr, 0x10));
                    IntPtr itemsArray = _scanner.ReadIntPtr(IntPtr.Add(modsListPtr, 0x8));

                    if (itemsArray == IntPtr.Zero || listSize <= 0) continue;

                    List<IntPtr> flattenedModPtrs = new();
                    for (int j = 0; j < listSize; j++)
                    {
                        IntPtr modPtr = _scanner.ReadIntPtr(IntPtr.Add(itemsArray, 0x10 + j * 8));
                        if (modPtr == IntPtr.Zero) continue;

                        IntPtr vtable = _scanner.ReadIntPtr(modPtr);
                        if (vtable == IntPtr.Zero) continue;

                        // Identify MultiMod (using signature OR direct acronym read)
                        string? directAcr = TryReadAcronymDirectly(modPtr);
                        bool isMultiMod = (directAcr == null); // If no acronym found, check if it's MultiMod
                        
                        if (isMultiMod)
                        {
                            // Check MultiMod signature as backup
                            int s1 = _scanner.ReadInt32(vtable);
                            int s2 = _scanner.ReadInt32(IntPtr.Add(vtable, 3));
                            if (s1 != 16777216 || s2 != 8193) isMultiMod = false;
                        }

                        if (isMultiMod)
                        {
                            // MultiMod expansion
                            IntPtr nestedArrayPtr = _scanner.ReadIntPtr(IntPtr.Add(modPtr, 0x8));
                            if (nestedArrayPtr == IntPtr.Zero || nestedArrayPtr.ToInt64() < 0x10000)
                                nestedArrayPtr = _scanner.ReadIntPtr(IntPtr.Add(modPtr, 0x10));
                            
                            if (nestedArrayPtr != IntPtr.Zero)
                            {
                                int size = _scanner.ReadInt32(IntPtr.Add(nestedArrayPtr, 0x8));
                                if (size > 0 && size < 20)
                                {
                                    for (int k = 0; k < size; k++)
                                    {
                                        IntPtr nestedModPtr = _scanner.ReadIntPtr(IntPtr.Add(nestedArrayPtr, 0x10 + k * 8));
                                        if (nestedModPtr != IntPtr.Zero) flattenedModPtrs.Add(nestedModPtr);
                                    }
                                }
                            }
                        }
                        else
                        {
                            flattenedModPtrs.Add(modPtr);
                        }
                    }

                    // Map VTables to Acronyms
                    for (int j = 0; j < flattenedModPtrs.Count; j++)
                    {
                        IntPtr modPtr = flattenedModPtrs[j];
                        IntPtr vtable = _scanner.ReadIntPtr(modPtr);
                        if (vtable == IntPtr.Zero) continue;

                        if (!_modVTableMap.ContainsKey(vtable))
                        {
                            // 1. Try direct read from mod object (Best)
                            string? acr = TryReadAcronymDirectly(modPtr);
                            
                            // 2. Fallback to index mapping
                            if (acr == null && j < expectedAcronyms.Length)
                            {
                                acr = expectedAcronyms[j];
                            }

                            if (acr != null)
                            {
                                _modVTableMap[vtable] = acr;
                                WriteLog($"Mapped VTable {vtable:X} to {acr} (Type: {categoryName}, Index: {j})");
                            }
                        }
                    }
                }
                
                if (_modVTableMap.Count == 0)
                {
                    WriteLog("BuildModVTableMap: Failed to extract any mods.");
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error building mod vtable map: {ex.Message}");
            }
        }


        private double ReadPerformancePointsFromHUD(IntPtr playerAddress)
        {
            if (playerAddress == IntPtr.Zero) return 0;

            try
            {
                // Player -> HUDOverlay
                IntPtr hudOverlay = _scanner.ReadIntPtr(IntPtr.Add(playerAddress, Offsets.Player.HUDOverlay));
                if (hudOverlay == IntPtr.Zero)
                {
                    WriteLog("HUDPP Trace: hudOverlay is 0");
                    return 0;
                }

                // HUDOverlay -> mainComponents (SkinnableContainer)
                // Offset 0x90 confirmed
                IntPtr mainComponents = _scanner.ReadIntPtr(IntPtr.Add(hudOverlay, Offsets.HUDOverlay.mainComponents));
                if (mainComponents == IntPtr.Zero)
                {
                    WriteLog("HUDPP Trace: mainComponents base offset is 0, scanning wider range...");
                    // Fallback: search for a SkinnableContainer pointer in the HUDOverlay memory range
                    int foundCount = 0;
                    for (int offset = 0x8; offset < 0xC00; offset += 8)
                    {
                        IntPtr pot = _scanner.ReadIntPtr(IntPtr.Add(hudOverlay, offset));
                        if (pot != IntPtr.Zero)
                        {
                            foundCount++;
                            if (foundCount <= 10) WriteLog($"HUDPP Trace: Non-zero pointer at 0x{offset:X} = {pot:X}");

                            IntPtr comps = _scanner.ReadIntPtr(IntPtr.Add(pot, Offsets.SkinnableContainer.components));
                            if (comps != IntPtr.Zero)
                            {
                                // BindableList components list
                                IntPtr list = _scanner.ReadIntPtr(IntPtr.Add(comps, Offsets.BindableList.list));
                                if (list != IntPtr.Zero)
                                {
                                    mainComponents = pot;
                                    WriteLog($"HUDPP Trace: Found potential mainComponents at offset 0x{offset:X} -> {pot:X}");
                                    break;
                                }
                            }
                        }
                    }
                }

                if (mainComponents == IntPtr.Zero) return 0;

                // Scan for components BindableList inside SkinnableContainer
                IntPtr componentsBindableList = IntPtr.Zero;
                IntPtr componentsList = IntPtr.Zero;
                int compSize = 0;
                IntPtr compItemsArray = IntPtr.Zero;

                for (int off = 0x8; off < 0x200; off += 8) // Scan up to 512 bytes
                {
                    IntPtr potBindable = _scanner.ReadIntPtr(IntPtr.Add(mainComponents, off));
                    if (potBindable == IntPtr.Zero) continue;

                    // Check if it's a BindableList by checking for inner List
                    // Try offsets 0x18 or maybe 0x8 etc. LazerOffsets uses 0x18.
                    // Also try scanning inner BindableList for a List

                    IntPtr potList = _scanner.ReadIntPtr(IntPtr.Add(potBindable, Offsets.BindableList.list));
                    if (potList == IntPtr.Zero) continue;

                    // Valid List?
                    int size = _scanner.ReadInt32(IntPtr.Add(potList, 0x10)); // Size at 0x10
                    if (size >= 0 && size < 1000)
                    {
                        IntPtr items = _scanner.ReadIntPtr(IntPtr.Add(potList, 0x8)); // Items at 0x8

                        if (size > 0 && items != IntPtr.Zero)
                        {
                            // Found a better one (populated)
                            componentsBindableList = potBindable;
                            componentsList = potList;
                            compSize = size;
                            compItemsArray = items;
                            WriteLog($"HUDPP Trace: Selected populated components at offset 0x{off:X}. Size={size}");
                            break;
                        }

                        // Keep the first empty one as fallback if nothing else found
                        if (componentsBindableList == IntPtr.Zero)
                        {
                            componentsBindableList = potBindable;
                            componentsList = potList;
                            compSize = size;
                            compItemsArray = items;
                        }
                    }
                }

                if (componentsList == IntPtr.Zero)
                {
                    WriteLog("HUDPP Trace: Failed to find components list in SkinnableContainer");
                    return 0;
                }

                // Iterate through components to find a PerformancePointsCounter (RollingCounter<int>)
                for (int i = 0; i < compSize; i++)
                {
                    IntPtr comp = _scanner.ReadIntPtr(IntPtr.Add(compItemsArray, 0x20 + i * 8));
                    if (comp == IntPtr.Zero) continue;

                    // RollingCounter<int> -> current (BindableWithCurrent<int>)
                    // Offset 0x80 usually correct for RollingCounter backing field
                    IntPtr currentBindableWithCurrent = _scanner.ReadIntPtr(IntPtr.Add(comp, Offsets.RollingCounter.current));
                    if (currentBindableWithCurrent == IntPtr.Zero) continue;

                    // BindableWithCurrent<T> inherits Bindable<T>, so we can read Value directly from it.
                    // We try reading both Int32 (classic) and Double (modern) to be safe.
                    // Offset Bindable.Value is 0x28.

                    int ppInt = _scanner.ReadInt32(IntPtr.Add(currentBindableWithCurrent, Offsets.Bindable.Value));
                    double ppDouble = _scanner.ReadDouble(IntPtr.Add(currentBindableWithCurrent, Offsets.Bindable.Value));

                    // WriteLog($"HUDPP Trace: Comp={comp:X} Val={ppInt}/{ppDouble:F2}");

                    if (ppInt > 0 && ppInt < 50000) return ppInt;

                    WriteLog($"HUDPP Trace: Comp={comp:X} Bindable={currentBindableWithCurrent:X} Int={ppInt} Dbl={ppDouble:F2}");

                    if (ppInt > 0 && ppInt < 50000) return ppInt;
                    if (ppDouble > 0 && ppDouble < 50000) return (int)Math.Round(ppDouble);
                }

                WriteLog("HUDPP Trace: PerformancePointsCounter not found in HUD components");
            }
            catch (Exception ex)
            {
                WriteLog($"Error reading PP from HUD: {ex.Message}");
            }

            return 0;
        }

        private bool _debugLoggingEnabled = false;

        public void SetDebugLogging(bool enabled)
        {
            _debugLoggingEnabled = enabled;
        }

        private void WriteLog(string message)
        {
            DebugService.Log(message, "LazerReader");
        }

        private void WriteLogThrottled(string key, string message)
        {
            DebugService.Throttled(key, message, "LazerReader");
        }

        private void DebugLog(string message)
        {
            DebugService.Verbose(message, "LazerReader");
        }

        /// <summary>
        /// Checks if the screen is a multiplayer screen by reading its type name.
        /// </summary>
        public bool CheckIfMultiplayerScreen(IntPtr address)
        {
            if (address == IntPtr.Zero || _gameBaseAddress == IntPtr.Zero) return false;

            try
            {
                // IMPORTANT: Exclude result screens - they may have data at similar offsets
                IntPtr gameApi = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.API));
                IntPtr resultScreenApi = _scanner.ReadIntPtr(IntPtr.Add(address, Offsets.SoloResultsScreen.api));
                if (resultScreenApi != IntPtr.Zero && resultScreenApi == gameApi)
                {
                    // This is a Result screen, not a Multiplayer screen
                    return false;
                }

                // tosu logic: 
                // screen[OnlinePlayScreen.API] == gameBase[API] AND 
                // screen[Multiplayer.Client] == gameBase[MultiplayerClient]

                IntPtr screenClient = _scanner.ReadIntPtr(IntPtr.Add(address, Offsets.Multiplayer.client));
                IntPtr gameClient = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.MultiplayerClient));

                return screenClient == gameClient && screenClient != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        private readonly Dictionary<string, int> _foundOffsets = new Dictionary<string, int>();

        private bool ScanForPointer(IntPtr address, IntPtr target, int startOffset, int endOffset, string cacheKey)
        {
            if (address == IntPtr.Zero || target == IntPtr.Zero) return false;

            if (_foundOffsets.TryGetValue(cacheKey, out int cachedOffset))
            {
                if (_scanner.ReadIntPtr(IntPtr.Add(address, cachedOffset)) == target)
                    return true;
            }

            for (int offset = startOffset; offset <= endOffset; offset += 8)
            {
                if (_scanner.ReadIntPtr(IntPtr.Add(address, offset)) == target)
                {
                    _foundOffsets[cacheKey] = offset;
                    // DebugLog($"Found {cacheKey} at offset {offset}");
                    return true;
                }
            }

            return false;
        }

        public bool CheckIfSongSelect(IntPtr address)
        {
            if (_gameBaseAddress == IntPtr.Zero || address == IntPtr.Zero) return false;
            
            // Try current offset from json
            IntPtr songSelectGame = _scanner.ReadIntPtr(IntPtr.Add(address, Offsets.SoloSongSelect.game));

            // Try backup offset if failed
            if (songSelectGame != _gameBaseAddress)
            {
                songSelectGame = _scanner.ReadIntPtr(IntPtr.Add(address, 1272));
            }

            WriteLogThrottled("check-songselect", $"CheckIfSongSelect: Screen={address:X}, ScreenGame={songSelectGame:X}, GameBase={_gameBaseAddress:X}");

            return songSelectGame == _gameBaseAddress;
        }

        public bool CheckIfPlayer(IntPtr address)
        {
            // From tosu:
            // this.process.readIntPtr(address + this.offsets['osu.Game.Screens.Play.SubmittingPlayer']['<api>k__BackingField']) === 
            // this.process.readIntPtr(this.gameBase() + this.offsets['osu.Game.OsuGameBase']['<API>k__BackingField'])
            // && ...

            if (_gameBaseAddress == IntPtr.Zero) return false;

            IntPtr playerApi = _scanner.ReadIntPtr(IntPtr.Add(address, Offsets.SubmittingPlayer.api));
            IntPtr gameApi = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.API));

            WriteLogThrottled("check-player-api", $"CheckIfPlayer: P_API={playerApi:X} G_API={gameApi:X}");

            if (playerApi != gameApi)
            {
                // Fallback: Check if it's a base Player (offset 1008)
                // offsets.json says "osu.Game.Screens.Play.Player": { "<api>k__BackingField": 1008 }

                IntPtr playerApiBase = _scanner.ReadIntPtr(IntPtr.Add(address, Offsets.Player.api));
                WriteLogThrottled("check-player-fallback", $"CheckIfPlayer Fallback: P_API_Base={playerApiBase:X} G_API={gameApi:X}");

                if (playerApiBase == gameApi)
                {
                    // It matches base Player offset!
                    // Check other base dependencies if needed, or just return true.
                    // tosu checks spectator client too.
                    // SpectatorClient backing field for Player is not listed in offsets.json?
                    // Wait, PlayerLoader has it? SpectatorPlayer has it at 1248.
                    // SubmittingPlayer has it at 1256.

                    return true;
                }

                return false;
            }

            IntPtr playerSpectator = _scanner.ReadIntPtr(IntPtr.Add(address, Offsets.SubmittingPlayer.spectatorClient));
            IntPtr gameSpectator = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.SpectatorClient));

            WriteLogThrottled("check-player-spec", $"CheckIfPlayer: P_Spec={playerSpectator:X} G_Spec={gameSpectator:X}");

            return playerSpectator == gameSpectator;
        }


        public bool CheckIfSubmittingPlayer(IntPtr address)
        {
            if (_gameBaseAddress == IntPtr.Zero || address == IntPtr.Zero) return false;

            // Get the game's API object
            IntPtr gameApi = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.API));
            
            // If we can't find the game API, assume it's live to avoid missing recordings
            if (gameApi == IntPtr.Zero)
            {
                WriteLogThrottled("check-submitting-null", "CheckIfSubmittingPlayer: gameApi is null, assuming LIVE");
                return true;
            }

            // Check if this player has the api_k__BackingField at SubmittingPlayer's offset
            // SubmittingPlayer.api_k__BackingField = 1264
            IntPtr playerApi = _scanner.ReadIntPtr(IntPtr.Add(address, Offsets.SubmittingPlayer.api));
            
            if (playerApi == gameApi)
            {
                // This is a SubmittingPlayer (LIVE)
                WriteLogThrottled("check-submitting-live", "CheckIfSubmittingPlayer: API match -> LIVE (SubmittingPlayer)");
                return true;
            }
            
            // Also check spectatorClient as backup
            IntPtr gameSpectator = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.SpectatorClient));
            if (gameSpectator != IntPtr.Zero)
            {
                IntPtr playerSpectator = _scanner.ReadIntPtr(IntPtr.Add(address, Offsets.SubmittingPlayer.spectatorClient));
                if (playerSpectator == gameSpectator)
                {
                    WriteLogThrottled("check-submitting-spec", "CheckIfSubmittingPlayer: SpectatorClient match -> LIVE (SubmittingPlayer)");
                    return true;
                }
            }
            
            // Neither matches - this is likely a ReplayPlayer or SpectatorPlayer
            WriteLogThrottled("check-submitting-replay", "CheckIfSubmittingPlayer: No API/Spectator match -> REPLAY");
            return false;
        }


        public bool CheckIfResultScreen(IntPtr address)
        {
            if (_gameBaseAddress == IntPtr.Zero || address == IntPtr.Zero) return false;

            // Strategy: Verify it is a SoloResultsScreen by checking the API backing field.
            // OsuGame_Screens_Ranking_SoloResultsScreen.api_k__BackingField = 1040
            IntPtr screenApi = _scanner.ReadIntPtr(IntPtr.Add(address, Offsets.SoloResultsScreen.api));
            IntPtr gameApi = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.API));

            if (screenApi != IntPtr.Zero && screenApi == gameApi)
            {
                WriteLog($"CheckIfResultScreen: API match confirmed for screen {address:X}");
                // Confirmed Result Screen
                _lastResultScoreInfoPtr = IntPtr.Zero; // Reset cache

                // Now get the SelectedScore (Bindable<ScoreInfo>)
                // OsuGame_Screens_Ranking_SoloResultsScreen.SelectedScore = 920
                IntPtr selectedScoreBindable = _scanner.ReadIntPtr(IntPtr.Add(address, Offsets.SoloResultsScreen.SelectedScore));
                if (selectedScoreBindable != IntPtr.Zero)
                {
                    // Bindable.Value is usually at 0x20 in Lazer for this type
                    IntPtr scoreInfoPtr = _scanner.ReadIntPtr(IntPtr.Add(selectedScoreBindable, 0x20));
                    if (scoreInfoPtr != IntPtr.Zero)
                    {
                        _lastResultScoreInfoPtr = scoreInfoPtr;
                    }
                }
                // Return true if API matched - Score info is optional for state detection
                return true;
            }
            
            return false;
        }

        private bool CheckResultScoreAtOffset(IntPtr address, int offset)
        {
            // Deprecated helper, kept/stubbed if needed or removed.
            return false;
        }

        public bool CheckIfPlayerLoader(IntPtr address)
        {
            if (_gameBaseAddress == IntPtr.Zero || address == IntPtr.Zero) return false;
            // PlayerLoader has osuLogo injected
            IntPtr loaderLogo = _scanner.ReadIntPtr(IntPtr.Add(address, Offsets.PlayerLoader.osuLogo));
            IntPtr gameLogo = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGame.osuLogo));

            // Logic: If loaderLogo matches Game.osuLogo, it's likely a PlayerLoader or similar dependent screen
            // Since SongSelect doesn't have this specific field at this offset, it's a decent differentiator
            return loaderLogo != IntPtr.Zero && loaderLogo == gameLogo;
        }

        /// <summary>
        /// Checks if the user is in a multiplayer lobby by detecting an active room in the MultiplayerClient.
        /// </summary>
        /// <summary>
        /// Checks if the user is in the Editor by verifying API and Realm back-references.
        /// </summary>
        public bool CheckIfEditor()
        {
            if (_gameBaseAddress == IntPtr.Zero) return false;
            IntPtr currentScreen = GetCurrentScreen();
            return CheckIfEditorScreen(currentScreen);
        }

        public bool CheckIfEditorScreen(IntPtr address)
        {
            if (address == IntPtr.Zero || _gameBaseAddress == IntPtr.Zero) return false;

            try
            {
                IntPtr editorApi = _scanner.ReadIntPtr(IntPtr.Add(address, Offsets.Editor.api));
                IntPtr osuLogo = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGame.osuLogo));

                IntPtr editorRealm = _scanner.ReadIntPtr(IntPtr.Add(address, Offsets.Editor.realm));
                IntPtr gameRealm = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.realm));

                // tosu logic: editor.api === osuGame.logo && editor.realm === osuGameBase.realm
                return editorApi == osuLogo && editorRealm == gameRealm;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Checks if the user is in a multiplayer lobby by detecting an active room in the MultiplayerClient.
        /// </summary>
        public bool CheckIfMultiplayer()
        {
            WriteLog("CheckIfMultiplayer: Called");
            // If we are in the Editor, force Multiplayer detection to false.
            // This handles the case where the MultiplayerClient still holds a room reference.
            if (CheckIfEditor())
            {
                WriteLog("CheckIfMultiplayer: Editor detected, returning false");
                return false;
            }

            if (_gameBaseAddress == IntPtr.Zero)
            {
                WriteLog("CheckIfMultiplayer: GameBaseAddress is Zero");
                return false;
            }

            try
            {
                IntPtr multiplayerClient = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.MultiplayerClient));
                if (multiplayerClient == IntPtr.Zero)
                {
                    WriteLog("Multiplayer Check: Global MultiplayerClient is null.");
                    return false;
                }

                // Check IsConnected
                IntPtr isConnectedBindable = _scanner.ReadIntPtr(IntPtr.Add(multiplayerClient, Offsets.OnlineMultiplayerClient.IsConnected));
                byte val40 = 0;
                if (isConnectedBindable != IntPtr.Zero)
                {
                    val40 = _scanner.ReadByte(IntPtr.Add(isConnectedBindable, 0x40));
                }
                bool isConnected = (val40 == 1);
                WriteLog($"Multiplayer Check: IsConnected={isConnected} (Bindable={isConnectedBindable:X}, Val40={val40})");

                if (isConnectedBindable != IntPtr.Zero)
                {
                    // Check standard Bindable.Value offset 0x10
                    byte val10 = _scanner.ReadByte(IntPtr.Add(isConnectedBindable, 0x10));

                    // The original WriteLog line is now redundant or needs adjustment.
                    // The instruction only shows the new log line, implying the old one is removed or replaced.
                    // Let's assume the new log line replaces the old one, and the `if (val40 == 1) isConnected = true;` logic is replaced by the direct assignment.
                    // The `if (val10 == 1) isConnected = true;` fallback might still be needed if `val40` isn't always reliable.
                    // The instruction provided `bool isConnected = (val40 == 1);` which implies `val40` is the primary source.
                    // Let's stick to the instruction and assume `val10` fallback is no longer needed for `isConnected` determination.
                    // The instruction also shows `if (isConnectedBindable != IntPtr.Zero)` block *after* the new `isConnected` and `WriteLog` lines. This is syntactically incorrect as `val40` is read inside that block.

                    // Original logic for val10 fallback, keeping it for robustness if val40 isn't always correct.
                    if (!isConnected && val10 == 1) isConnected = true;
                }
                else
                {
                    WriteLog("Multiplayer Check: IsConnected bindable is null.");
                }

                // Check Current Room
                IntPtr roomPtr = _scanner.ReadIntPtr(IntPtr.Add(multiplayerClient, Offsets.MultiplayerClient.room));

                if (roomPtr != IntPtr.Zero)
                {
                    WriteLog($"Multiplayer Check: RoomPtr={roomPtr:X}");
                }
                else
                {
                    WriteLog("Multiplayer Check: RoomPtr is null.");
                }

                // Check APIRoom (Bindable<APIRoom> or just APIRoom?)
                IntPtr apiRoomPtr = _scanner.ReadIntPtr(IntPtr.Add(multiplayerClient, Offsets.MultiplayerClient.APIRoom));
                if (apiRoomPtr != IntPtr.Zero)
                {
                    WriteLog($"Multiplayer Check: APIRoomPtr={apiRoomPtr:X}");
                    // Sometimes APIRoom is populated even if Room is null (e.g. connecting)
                    // If we have APIRoom, we are likely in multiplayer context
                }
                else
                {
                    WriteLog("Multiplayer Check: APIRoomPtr is null.");
                }

                // Relaxed check for debugging/robustness:
                if (roomPtr != IntPtr.Zero || apiRoomPtr != IntPtr.Zero) return true;

                // FORCE DUMP even if connected, if we have no room.
                // if (isConnected) return true; 

                // FAILURE DEBUGGING: Dump Memory
                WriteLog("Multiplayer Check: FAILED (No Room/APIRoom). Dumping MultiplayerClient memory...");
                // DumpMemory(multiplayerClient, 0x400, "MultiplayerClientDump"); // Dump 1KB

                return isConnected; // Still return valid if connected, but AFTER dumping
            }
            catch (Exception ex)
            {
                WriteLog($"Multiplayer Check Error: {ex.Message}");
                return false;
            }
        }

        private void DumpMemory(IntPtr address, int size, string label)
        {
            // Logging disabled
        }

        public IntPtr GetCurrentScreen()
        {
            if (_gameBaseAddress == IntPtr.Zero) return IntPtr.Zero;

            // tosu: const screenStack = this.screenStack();
            // const stack = this.process.readIntPtr(screenStack + offsets['osu.Framework.Screens.ScreenStack'].stack);

            IntPtr screenStackPtr = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGame.ScreenStack));
            if (screenStackPtr == IntPtr.Zero)
            {
                WriteLog("GetCurrentScreen: ScreenStackPtr is 0. GameBase found but ScreenStack is null.");
                return IntPtr.Zero;
            }

            IntPtr stackPtr = _scanner.ReadIntPtr(IntPtr.Add(screenStackPtr, Offsets.ScreenStack.stack));
            if (stackPtr == IntPtr.Zero)
            {
                WriteLog("GetCurrentScreen: StackPtr (internal stack list) is 0.");
                return IntPtr.Zero;
            }

            // List<T> layout (64-bit):
            // 0x0: MethodTable
            // 0x8: _items (Array)
            // 0x10: _size (int)
            // 0x14: _version (int)

            int count = _scanner.ReadInt32(IntPtr.Add(stackPtr, 0x10));
            WriteLogThrottled("screen-count", $"GetCurrentScreen: Count={count}");
            if (count <= 0) return IntPtr.Zero;

            IntPtr itemsPtr = _scanner.ReadIntPtr(IntPtr.Add(stackPtr, 0x8));
            if (itemsPtr == IntPtr.Zero) return IntPtr.Zero;

            // Array layout:
            // 0x0: MethodTable
            // 0x8: Length
            // 0x10: Start of data

            // To get (count - 1)th item:
            // itemsPtr + 0x10 + 0x8 * (count - 1)
            IntPtr currentScreenPtr = _scanner.ReadIntPtr(IntPtr.Add(itemsPtr, 0x10 + 0x8 * (count - 1)));
            WriteLogThrottled("screen-found", $"GetCurrentScreen: Found Screen {currentScreenPtr:X}");

            return currentScreenPtr;
        }

        public LiveSnapshot GetStats()
        {
            try
            {
                var snapshot = new LiveSnapshot { StateNumber = -1 };

                if (_gameBaseAddress == IntPtr.Zero)
                {
                    Initialize();
                    if (_gameBaseAddress == IntPtr.Zero) return snapshot;
                }

                // 1. Read Basic State (Screens) - Throttle screen stack scanning unless playing
                IntPtr currentScreen = _cachedCurrentScreen;
                if (DateTime.Now - _lastScreenScan > TimeSpan.FromMilliseconds(250) || _cachedCurrentScreen == IntPtr.Zero)
                {
                    currentScreen = GetCurrentScreen();
                    _cachedCurrentScreen = currentScreen;
                    _lastScreenScan = DateTime.Now;
                }

                if (currentScreen == IntPtr.Zero)
                {
                    WriteLogThrottled("no-screen", "GetStats: No current screen");
                    snapshot.StateNumber = 0; // Connected but can't find screen (Menu?)
                    // Even if we can't find the screen, we might be able to read the beatmap
                    ReadBeatmap(snapshot);
                    
                    // Throttle idle mod reads
                    if (DateTime.Now - _lastModScan > TimeSpan.FromMilliseconds(500))
                    {
                        var mods = ReadModsFromMemory();
                        snapshot.ModsList = mods;
                        snapshot.Mods = (mods != null && mods.Count > 0) ? string.Join(",", mods) : "NM";
                        _lastModScan = DateTime.Now;
                    }
                    else
                    {
                        snapshot.ModsList = _currentModsList;
                        snapshot.Mods = (_currentModsList != null && _currentModsList.Count > 0) ? string.Join(",", _currentModsList) : "NM";
                    }

                    snapshot.Stars = _staticStars;
                    snapshot.BPM = (int?)Math.Round(_staticBpm);
                    snapshot.MinBPM = (int?)Math.Round(_minBpm);
                    snapshot.MaxBPM = (int?)Math.Round(_maxBpm);
                    snapshot.MostlyBPM = (int?)Math.Round(_staticBpm);
                    _detector?.Process(snapshot);
                    return snapshot;
                }



                // Read Beatmap Info FIRST so TotalObjects is available for score conversion and PP calculation
                // DebugService.Log("GetStats: Calling ReadBeatmap...");
                IntPtr beatmapInfoPtr = ReadBeatmap(snapshot);
                // DebugService.Log($"GetStats: ReadBeatmap returned {beatmapInfoPtr:X}");
                UpdateBeatmapFile(beatmapInfoPtr); // Update _currentOsuFilePath for PP calculation

                if (beatmapInfoPtr != IntPtr.Zero)
                {
                    snapshot.MapPath = _currentOsuFilePath;
                }
                
                IntPtr playerScreen = CheckIfPlayer(currentScreen) ? currentScreen : IntPtr.Zero;

                IntPtr resultScreen = (playerScreen == IntPtr.Zero && CheckIfResultScreen(currentScreen)) ? currentScreen : IntPtr.Zero;
                
                // If we are playing, boost screen scan rate next frame
                if (playerScreen != IntPtr.Zero || resultScreen != IntPtr.Zero) _lastScreenScan = DateTime.MinValue; 

                IntPtr songSelectScreen = (playerScreen == IntPtr.Zero && resultScreen == IntPtr.Zero && CheckIfSongSelect(currentScreen)) ? currentScreen : IntPtr.Zero;
                IntPtr editorScreen = (playerScreen == IntPtr.Zero && resultScreen == IntPtr.Zero && songSelectScreen == IntPtr.Zero && CheckIfEditorScreen(currentScreen)) ? currentScreen : IntPtr.Zero;
                IntPtr multiplayerScreen = (playerScreen == IntPtr.Zero && resultScreen == IntPtr.Zero && songSelectScreen == IntPtr.Zero && editorScreen == IntPtr.Zero && CheckIfMultiplayerScreen(currentScreen)) ? currentScreen : IntPtr.Zero;

                    // Refactored Logic: Prioritize Playing > Results > SongSelect > Menu
                    
                    // Scan screen stack to find active screens
                    IntPtr screenStackPtr = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGame.ScreenStack));
                    if (screenStackPtr != IntPtr.Zero)
                    {
                        IntPtr stackPtr = _scanner.ReadIntPtr(IntPtr.Add(screenStackPtr, Offsets.ScreenStack.stack));
                        if (stackPtr != IntPtr.Zero)
                        {
                            int count = _scanner.ReadInt32(IntPtr.Add(stackPtr, 0x10));
                            IntPtr itemsPtr = _scanner.ReadIntPtr(IntPtr.Add(stackPtr, 0x8));

                            WriteLogThrottled("screen-count", $"GetStats: Screen count = {count}");

                            if (itemsPtr != IntPtr.Zero && count > 0)
                            {
                                for (int i = count - 1; i >= 0; i--)
                                {
                                    IntPtr screen = _scanner.ReadIntPtr(IntPtr.Add(itemsPtr, 0x10 + 0x8 * i));
                                    
                                    // Check for Results screen
                                    if (resultScreen == IntPtr.Zero && CheckIfResultScreen(screen))
                                    {
                                        resultScreen = screen;
                                    }
                                    
                                    if (playerScreen == IntPtr.Zero && CheckIfPlayer(screen))
                                    {
                                        playerScreen = screen;
                                    }

                                    if (playerScreen == IntPtr.Zero && CheckIfPlayerLoader(screen))
                                    {
                                        WriteLogThrottled("loader-detected", $"GetStats: PlayerLoader detected at {screen:X}");
                                        playerScreen = screen;
                                    }

                                    // Check for Editor (State 1)
                                    if (editorScreen == IntPtr.Zero && CheckIfEditorScreen(screen))
                                    {
                                         editorScreen = screen;
                                    }

                                    // Check for Multiplayer (State 11)
                                    if (multiplayerScreen == IntPtr.Zero && CheckIfMultiplayerScreen(screen))
                                    {
                                         multiplayerScreen = screen;
                                    }

                                    if (songSelectScreen == IntPtr.Zero && CheckIfSongSelect(screen))
                                    {
                                        songSelectScreen = screen;
                                    }
                                }
                            }
                        }
                    }

                    // Priority 1: Results Screen (Overlays playing)
                    if (resultScreen != IntPtr.Zero)
                    {
                        WriteLogThrottled("state-results", "GetStats: User is on Results! (State 7)");
                        snapshot.StateNumber = 7; // Results
                        snapshot.IsPlaying = false;
                        snapshot.Passed = true; 
                        snapshot.Failed = false; // Results screen implies a pass, ignore any lingering fail flags
                        
                        if (_lastResultScoreInfoPtr != IntPtr.Zero)
                        {
                            UpdateResultScreenSnapshot(_lastResultScoreInfoPtr, snapshot);
                            // Verify data is "ready" by checking if we have valid-looking stats
                            if ((snapshot.Score ?? 0) > 0 && snapshot.HitCounts != null)
                            {
                                snapshot.IsResultsReady = true;
                            }
                        }
                    }


                    // Priority 2: Playing (Player Screen Active)
                    else if (playerScreen != IntPtr.Zero)
                    {
                        WriteLogThrottled("state-playing", "GetStats: User IS Playing! (State 2)");
                        snapshot.StateNumber = 2; // Playing
                        snapshot.IsPlaying = true;

                        // Check if this is a replay/spectator player
                        // We check if it is NOT a SubmittingPlayer (Live)
                        bool isSubmitting = CheckIfSubmittingPlayer(playerScreen);
                        
                        if (!isSubmitting)
                        {
                            snapshot.IsReplay = true;
                            WriteLogThrottled("replay-detected", "GetStats: Not SubmittingPlayer - This is a REPLAY/SPECTATOR");
                        }
                        else
                        {
                            // Explicitly set to false for live plays
                            snapshot.IsReplay = false;
                            WriteLogThrottled("live-detected", "GetStats: SubmittingPlayer detected - LIVE");
                        }

                        IntPtr scorePtr = _scanner.ReadIntPtr(IntPtr.Add(playerScreen, Offsets.Player.Score));
                        if (scorePtr != IntPtr.Zero)
                        {
                            IntPtr playingScoreInfoPtr = _scanner.ReadIntPtr(IntPtr.Add(scorePtr, 0x8));
                            if (playingScoreInfoPtr != IntPtr.Zero)
                            {
                                ReadScoreInfo(playingScoreInfoPtr, playerScreen, snapshot);
                                var currentMods = snapshot.ModsList ?? new List<string>();
                                UpdateStaticAttributesIfNeeded(currentMods, GetClockRate());
                            }
                        }
                    }

                    // Priority 3: Editor
                    else if (editorScreen != IntPtr.Zero)
                    {
                        WriteLogThrottled("state-editor", "GetStats: Editor detected (State 1)");
                        snapshot.StateNumber = 1;
                        snapshot.IsPlaying = false;
                    }
                    // Priority 4: Multiplayer
                    else if (multiplayerScreen != IntPtr.Zero)
                    {
                        WriteLogThrottled("state-multiplayer", "GetStats: Multiplayer detected (State 11)");
                        snapshot.StateNumber = 11; 
                        snapshot.IsPlaying = false;
                    }
                    // Priority 5: Song Select
                    else if (songSelectScreen != IntPtr.Zero)
                    {
                        WriteLogThrottled("state-songselect", "GetStats: User is on Song Select! (State 5)");
                        snapshot.StateNumber = 5; // Song Select
                        snapshot.IsPlaying = false;

                        var mods = ReadModsFromMemory();
                        uint modsBits = GetModsBits(mods);
                        double clockRate = GetClockRate();

                        snapshot.ModsList = mods;
                        snapshot.Mods = mods.Count > 0 ? string.Join(",", mods) : "NM";

                        // 4. Update RosuService context using Raw Beatmap Info
                        RawBeatmapInfo? rawBeatmapInfo = _cachedRawBeatmapInfo;
                        if (DateTime.Now - _lastBeatmapInfoScan > TimeSpan.FromMilliseconds(200) || _cachedRawBeatmapInfo == null)
                        {
                            rawBeatmapInfo = ReadRawBeatmapInfoCached();
                            _cachedRawBeatmapInfo = rawBeatmapInfo;
                            _lastBeatmapInfoScan = DateTime.Now;
                        }

                        if (rawBeatmapInfo != null && !string.IsNullOrEmpty(rawBeatmapInfo.MD5Hash))
                        {
                            // Beatmap metadata and attributes are already read by ReadBeatmap(snapshot) early in GetStats
                            // MapName and stats are already populated.
                            
                            WriteLog($"GetStats: Song Select map detected: {rawBeatmapInfo.MD5Hash}");
                            string osuFile;
                            if (rawBeatmapInfo.FileHash == _lastResolvedMd5 && !string.IsNullOrEmpty(_currentOsuFilePath))
                            {
                                osuFile = _currentOsuFilePath;
                            }
                            else
                            {
                                string? resolvedOsuFile = ResolveOsuFileByHash(rawBeatmapInfo.BeatmapSetInfoPtr, rawBeatmapInfo.FileHash);
                                osuFile = resolvedOsuFile ?? "";
                                _lastResolvedMd5 = rawBeatmapInfo.FileHash;
                            }

                            WriteLog($"GetStats: Resolved osu file - Path='{osuFile}', Exists={File.Exists(osuFile)}, FileHash={rawBeatmapInfo.FileHash}");

                            if (File.Exists(osuFile))
                            {
                                if (osuFile != _currentOsuFilePath)
                                {
                                    _currentOsuFilePath = osuFile;
                                    ParseMapDataFromFile(osuFile);
                                }
                                modsBits = GetModsBits(mods);
                                clockRate = GetClockRate();


                                UpdateStaticAttributesIfNeeded(mods, clockRate);

                                bool settingsChanged = false;
                                if (_cachedStats != null)
                                {
                                    settingsChanged = (Math.Abs(_cachedStats.AR - (float)(_currentModSettings.AR ?? -1.0)) > 0.001f) ||
                                                      (Math.Abs(_cachedStats.CS - (float)(_currentModSettings.CS ?? -1.0)) > 0.001f) ||
                                                      (Math.Abs(_cachedStats.OD - (float)(_currentModSettings.OD ?? -1.0)) > 0.001f) ||
                                                      (Math.Abs(_cachedStats.HP - (float)(_currentModSettings.HP ?? -1.0)) > 0.001f);
                                }

                                if (_cachedStats == null || _cachedStats.MD5Hash != rawBeatmapInfo.MD5Hash ||
                                    _cachedStats.RosuMods != modsBits || Math.Abs(_cachedStats.ClockRate - clockRate) > 0.001 ||
                                    settingsChanged)
                                {
                                    _rosuService.UpdateContext(_currentOsuFilePath);
                                    // Calculate PP if FC and Map Length via rosu-pp
                                    WriteLog($"GetStats: Calling CalculatePpIfFc with Path='{_currentOsuFilePath}', Mods={modsBits}, Rate={clockRate}");
                                    var ppStats = _rosuService.CalculatePpIfFc(
                                        _currentOsuFilePath,
                                        mods,
                                        100.0,
                                        _currentModSettings.AR ?? -1,
                                        _currentModSettings.CS ?? -1,
                                        _currentModSettings.OD ?? -1,
                                        _currentModSettings.HP ?? -1,
                                        clockRate,
                                        isLazer: true
                                    );


                                    // Get mod-adjusted difficulty attributes for Song Select display
                                    var attrs = _rosuService.GetDifficultyAttributes(
                                        _currentOsuFilePath,
                                        mods,
                                        clockRate,
                                        _currentModSettings.AR ?? -1.0,
                                        _currentModSettings.CS ?? -1.0,
                                        _currentModSettings.OD ?? -1.0,
                                        _currentModSettings.HP ?? -1.0
                                    );

                                    WriteLog($"GetStats: Rosu Result - PP={ppStats.PP}, Stars={ppStats.Stars}, Length={ppStats.MapLength}, Combo={ppStats.MaxCombo}");

                                    _cachedStats = new CachedBeatmapStats
                                    {
                                        MD5Hash = rawBeatmapInfo.MD5Hash,
                                        RosuMods = modsBits,
                                        ClockRate = clockRate,
                                        PPIfFC = ppStats.PP,
                                        MaxCombo = ppStats.MaxCombo,
                                        Stars = ppStats.Stars,
                                        MapLength = ppStats.MapLength,
                                        AR = (float)attrs.AR,
                                        CS = (float)attrs.CS,
                                        OD = (float)attrs.OD,
                                        HP = (float)attrs.HP
                                    };

                                     // Classic Max Score calculation (Matching gameplay formula)
                                     int objectCount = _totalObjects; // Using parsed objects count
                                     double maxScore = Math.Pow(objectCount, 2) * 32.57 + 100000;
                                     _cachedStats.MaxScore = (long)Math.Round(maxScore);
                                    WriteLog($"GetStats: Calculated Song Select Stats - PP: {ppStats.PP:F1}, MaxCombo: {ppStats.MaxCombo}, Length: {ppStats.MapLength}");
                                }

                                 // Apply cached stats to snapshot (unconditional - rosu-pp values include mod adjustments)
                                 if (_cachedStats != null)
                                 {
                                     snapshot.PPIfFC = _cachedStats.PPIfFC;
                                     snapshot.MaxCombo = _cachedStats.MaxCombo;
                                     snapshot.Combo = _cachedStats.MaxCombo; // Show max combo in song select
                                     snapshot.Score = _cachedStats.MaxScore;
                                     snapshot.Stars = _cachedStats.Stars;
                                     snapshot.TotalObjects = _totalObjects; // Ensure total objects is passed through

                                    // Apply mod-adjusted stats to snapshot
                                    snapshot.AR = _cachedStats.AR;
                                    snapshot.CS = _cachedStats.CS;
                                    snapshot.OD = _cachedStats.OD;
                                    snapshot.HP = _cachedStats.HP;

                                     // Set map duration from rosu-pp (more reliable)
                                     if (_cachedStats.MapLength > 0)
                                     {
                                         snapshot.TotalTimeMs = (int)(_cachedStats.MapLength / clockRate);
                                         snapshot.TimeMs = snapshot.TotalTimeMs; // Show full length in Song Select
                                     }
                                }
                            }
                            else
                            {
                                WriteLog($"GetStats: Osu file not found/resolved for {rawBeatmapInfo.MD5Hash}");
                            }

                            // Fallback map duration if rosu-pp failed or file missing
                            if ((snapshot.TotalTimeMs == null || snapshot.TotalTimeMs == 0) && rawBeatmapInfo.Length > 0.001) // Check > 0.001 to avoid double epsilon issues
                            {
                                snapshot.TotalTimeMs = (int)(rawBeatmapInfo.Length / clockRate);
                                WriteLog($"GetStats: Song Select TotalTimeMs={snapshot.TotalTimeMs}");
                            }
                        }
                        else
                        {
                            WriteLog("GetStats: ReadRawBeatmapInfoCached returned null or empty hash");
                        }

                    }
                    // Priority 4: Menu/Other (No specific screen or multiplayer detected)
                    else
                    {
                        snapshot.StateNumber = 0; // Menu/Other
                        var mods = ReadModsFromMemory();
                        snapshot.ModsList = mods;
                        snapshot.Mods = mods.Count > 0 ? string.Join(",", mods) : "NM";

                        UpdateStaticAttributesIfNeeded(mods, GetClockRate());
                    }

                // Always populate attributes from static cache
                snapshot.Stars = (_staticStars > 0) ? _staticStars : snapshot.Stars;
                snapshot.BaseStars = _baseStars;
                snapshot.BPM = (int?)Math.Round(_staticBpm);
                snapshot.BaseBPM = (int?)Math.Round(_baseModeBpm);
                snapshot.MinBPM = (int?)Math.Round(_minBpm);
                snapshot.MaxBPM = (int?)Math.Round(_maxBpm);
                snapshot.MostlyBPM = (int?)Math.Round(_staticBpm);
                
                snapshot.BaseCS = _baseCS;
                snapshot.BaseAR = _baseAR;
                snapshot.BaseOD = _baseOD;
                snapshot.BaseHP = _baseHP;

                snapshot.Circles = _circles;
                snapshot.Sliders = _sliders;
                snapshot.Spinners = _spinners;

                // PP+ REMOVED
                // WriteLog($"GetStats: About to call UpdatePlusAttributes - path='{_currentOsuFilePath}'");
                // UpdatePlusAttributes(snapshot);
                _detector?.Process(snapshot);
                return snapshot;
            }
            catch (Exception ex)
            {
                WriteLog($"GetStats Error: {ex.Message}");
                return new LiveSnapshot { StateNumber = -1 };
            }
        }

        private IntPtr ReadBeatmap(LiveSnapshot snapshot)
        {
            try
            {
                IntPtr bindableBeatmap = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.Beatmap));
                if (bindableBeatmap == IntPtr.Zero) 
                { 
                    DebugService.Log($"ReadBeatmap: BindableBeatmap is NULL (GameBase={_gameBaseAddress:X} + Offset={Offsets.OsuGameBase.Beatmap:X})"); 
                    return IntPtr.Zero; 
                }

                IntPtr workingBeatmap = _scanner.ReadIntPtr(IntPtr.Add(bindableBeatmap, 0x20));
                if (workingBeatmap == IntPtr.Zero) 
                { 
                    DebugService.Log($"ReadBeatmap: WorkingBeatmap is NULL (Bindable={bindableBeatmap:X} + 0x20)"); 
                    return IntPtr.Zero; 
                }

                // DebugService.Log($"ReadBeatmap: WorkingBeatmap={workingBeatmap:X}");

                IntPtr beatmapInfo = _scanner.ReadIntPtr(IntPtr.Add(workingBeatmap, 0x8));
                if (beatmapInfo == IntPtr.Zero) 
                { 
                    DebugService.Log($"ReadBeatmap: BeatmapInfo is NULL (Working={workingBeatmap:X} + 0x8)"); 
                    return IntPtr.Zero; 
                }

                // Metadata
                IntPtr metadata = IntPtr.Zero;
                try 
                {
                    metadata = _scanner.ReadIntPtr(IntPtr.Add(beatmapInfo, Offsets.BeatmapInfo.Metadata));
                    if (metadata != IntPtr.Zero)
                    {
                        string title = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(metadata, Offsets.BeatmapMetadata.Title)));
                        string artist = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(metadata, Offsets.BeatmapMetadata.Artist)));
                        snapshot.Title = title;
                        snapshot.Artist = artist;
                    }
                }
                catch { }

                // MD5 Hash
                try
                {
                    snapshot.MD5Hash = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(beatmapInfo, Offsets.BeatmapInfo.MD5Hash)));
                }
                catch { }

                // Difficulty
                try
                {
                    IntPtr difficulty = _scanner.ReadIntPtr(IntPtr.Add(beatmapInfo, Offsets.BeatmapInfo.Difficulty));
                    if (difficulty != IntPtr.Zero)
                    {
                        snapshot.CS = _scanner.ReadFloat(IntPtr.Add(difficulty, Offsets.BeatmapDifficulty.CircleSize));
                        snapshot.AR = _scanner.ReadFloat(IntPtr.Add(difficulty, Offsets.BeatmapDifficulty.ApproachRate));
                        snapshot.OD = _scanner.ReadFloat(IntPtr.Add(difficulty, Offsets.BeatmapDifficulty.OverallDifficulty));
                        snapshot.HP = _scanner.ReadFloat(IntPtr.Add(difficulty, Offsets.BeatmapDifficulty.DrainRate));
                    }
                }
                catch { }

                // Validation & Assignment
                snapshot.TotalObjects = _scanner.ReadInt32(IntPtr.Add(beatmapInfo, Offsets.BeatmapInfo.TotalObjectCount));
                double length = _scanner.ReadDouble(IntPtr.Add(beatmapInfo, Offsets.BeatmapInfo.Length));
                if (length > 0) snapshot.TotalTimeMs = (int)length;

                // Diff Name
                string diffName = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(beatmapInfo, Offsets.BeatmapInfo.DifficultyName)));
                snapshot.Version = diffName;
                if (!string.IsNullOrEmpty(diffName))
                    snapshot.Beatmap = $"{snapshot.Artist} - {snapshot.Title} [{diffName}]";
                else
                    snapshot.Beatmap = $"{snapshot.Artist} - {snapshot.Title}";

                // Background Path Logic
                if (metadata != IntPtr.Zero)
                {
                    string bgFileName = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(metadata, Offsets.BeatmapMetadata.BackgroundFile)));

                    if (!string.IsNullOrEmpty(bgFileName))
                    {
                        IntPtr beatmapSetInfo = _scanner.ReadIntPtr(IntPtr.Add(workingBeatmap, Offsets.BeatmapManagerWorkingBeatmap.BeatmapSetInfo));

                        if (beatmapSetInfo != IntPtr.Zero)
                        {
                            string? fileHash = FindFileHashByName(beatmapSetInfo, bgFileName);
                                if (!string.IsNullOrEmpty(fileHash))
                                {
                                    snapshot.BackgroundHash = fileHash; // For WebViewWindow broadcast
                                    snapshot.BackgroundPath = $"/api/background/{fileHash}";
                                }
                                else
                                {
                                    // If we can't find a hash, it's either not a lazer beatmap or local file missing
                                    snapshot.BackgroundHash = null;
                                    snapshot.BackgroundPath = null;
                                }
                        }
                    }
                }

                return beatmapInfo;

            }
            catch (Exception ex)
            {
                DebugService.Log($"ReadBeatmap: Exception caught - {ex.Message} at {ex.StackTrace}");
                return IntPtr.Zero;
            }
        }

        private string? FindFileHashByName(IntPtr beatmapSetInfo, string fileName)
        {
            try
            {
                // Strict logic based on tosu implementation
                // BeatmapSetInfo.Files is at 0x20
                IntPtr filesList = _scanner.ReadIntPtr(IntPtr.Add(beatmapSetInfo, Offsets.BeatmapSetInfo.Files));
                if (filesList == IntPtr.Zero) return null;

                // C# List<T> internal structure
                // _items at 0x8, _size at 0x10
                IntPtr itemsArray = _scanner.ReadIntPtr(IntPtr.Add(filesList, 0x8));
                int count = _scanner.ReadInt32(IntPtr.Add(filesList, 0x10));

                if (itemsArray == IntPtr.Zero || count <= 0 || count > 500) return null;

                for (int i = 0; i < count; i++)
                {
                    // Items are pointers to RealmNamedFileUsage objects
                    IntPtr itemPtr = _scanner.ReadIntPtr(IntPtr.Add(itemsArray, 0x10 + (i * 0x8)));
                    if (itemPtr == IntPtr.Zero) continue;

                    // RealmNamedFileUsage offsets from tosu:
                    // Filename at 0x20 (pointer to string)
                    // File at 0x18 (pointer to RealmFile)

                    IntPtr filenamePtr = _scanner.ReadIntPtr(IntPtr.Add(itemPtr, Offsets.RealmNamedFileUsage.Filename));
                    string name = _scanner.ReadString(filenamePtr);

                    if (IsMatchingFileName(name, fileName))
                    {
                        // Found matching file usage
                        IntPtr realmFilePtr = _scanner.ReadIntPtr(IntPtr.Add(itemPtr, Offsets.RealmNamedFileUsage.File));
                        if (realmFilePtr == IntPtr.Zero) continue;

                        // RealmFile offset from tosu:
                        // Hash at 0x18 (pointer to string)
                        IntPtr hashPtr = _scanner.ReadIntPtr(IntPtr.Add(realmFilePtr, Offsets.RealmFile.Hash));
                        string hash = _scanner.ReadString(hashPtr);

                        if (IsValidHash(hash))
                        {
                            WriteLog($"Background: file={fileName}, hash={hash}");
                            return hash;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog($"FindFileHashByName error: {ex.Message}");
            }
            return null;
        }

        private string? ReadStringSafely(IntPtr ptrPtr)
        {
            IntPtr strPtr = _scanner.ReadIntPtr(ptrPtr);
            if (strPtr == IntPtr.Zero) return null;
            return _scanner.ReadString(strPtr);
        }

        private bool IsMatchingFileName(string? candidate, string target)
        {
            return !string.IsNullOrEmpty(candidate) && string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase);
        }

        private string? ReadRealmFileHash(IntPtr realmFilePtrPtr)
        {
            IntPtr realmFile = _scanner.ReadIntPtr(realmFilePtrPtr);
            if (realmFile == IntPtr.Zero) return null;
            // RealmFile: Header(16), Hash(string) at 0x10? or 0x8?
            // Try 0x10 first (standard object)
            // Or maybe 0x8 if inherited differently?
            // Let's try 0x8 and 0x10

            // Try 0x10 (First field)
            string h1 = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(realmFile, 0x10)));
            if (IsValidHash(h1)) return h1;

            // Try 0x8 (Maybe backing field?)
            string h2 = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(realmFile, 0x8)));
            if (IsValidHash(h2)) return h2;

            return null;
        }

        private bool IsValidHash(string? h)
        {
            return !string.IsNullOrEmpty(h) && h.Length > 10; // Simple check
        }


        private double _lastTime;
        private DateTime _lastUpdate = DateTime.MinValue;
        private bool _isPausedState = false; // Persist pause state between reads

        private void ReadScoreInfo(IntPtr scoreInfoPtr, IntPtr playerPtr, LiveSnapshot snapshot)
        {
            try
            {
                // Reset mod settings before reading
                _currentModSettings = new ModSettings();

                // Read Mods and Clock Rate EARLY for use in Fail detection
                List<string> modsList = ReadMods(scoreInfoPtr);

                // Basic scoring info
                long standardizedScore = _scanner.ReadInt64(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.TotalScore));
                snapshot.Accuracy = _scanner.ReadDouble(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.Accuracy));
                snapshot.MaxCombo = _scanner.ReadInt32(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.MaxCombo));

                // Read Replay Hash from ScoreInfo
                snapshot.ReplayHash = ReadScoreHash(scoreInfoPtr);


                // Read Maximum Statistics for reliable total object count
                IntPtr maxStatsDict = _scanner.ReadIntPtr(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.maximumStatistics));
                if (maxStatsDict != IntPtr.Zero)
                {
                    int totalObjects = GetObjectCountFromMaxStatistics(maxStatsDict);
                    if (totalObjects > 0) snapshot.TotalObjects = totalObjects;
                }

                // Read current combo from ScoreProcessor (Bindable) instead of ScoreInfo for live updates
                // Validation: If heavily negative time (intro), assume stats are zero
                if ((snapshot.TimeMs ?? 0) < 0)
                {
                    snapshot.MaxCombo = 0;
                    snapshot.Combo = 0;
                    standardizedScore = 0;
                }
                else
                {
                    IntPtr scoreProcessorPtr = _scanner.ReadIntPtr(IntPtr.Add(playerPtr, Offsets.Player.ScoreProcessor));
                    if (scoreProcessorPtr != IntPtr.Zero)
                    {
                        IntPtr comboBindable = _scanner.ReadIntPtr(IntPtr.Add(scoreProcessorPtr, Offsets.OsuScoreProcessor.Combo));
                        if (comboBindable != IntPtr.Zero)
                        {
                            int rawCombo = _scanner.ReadInt32(IntPtr.Add(comboBindable, 0x40)); // Bindable<int>.Value
                            
                            // Sanity Check
                            if (rawCombo < 0 || rawCombo > 50000) rawCombo = 0;
                            snapshot.Combo = rawCombo;
                        }

                        // Read HitEvents for Live UR
                        ReadHitEvents(scoreProcessorPtr, snapshot);
                    }
                }
                
                // Read HealthProcessor for Fail Detection
                IntPtr healthProcPtr = _scanner.ReadIntPtr(IntPtr.Add(playerPtr, Offsets.Player.HealthProcessor));
                if (healthProcPtr != IntPtr.Zero)
                {
                     // Health is a BindableDouble
                     IntPtr healthBindable = _scanner.ReadIntPtr(IntPtr.Add(healthProcPtr, Offsets.OsuHealthProcessor.Health));
                     double healthVal = -1;
                     if (healthBindable != IntPtr.Zero)
                     {
                         healthVal = _scanner.ReadDouble(IntPtr.Add(healthBindable, 0x40)); // Bindable<double>.Value
                     }

                     // HasFailed offset is unreliable. Use Health == 0 check.
                     // Filter out NoFail (NF) which keeps health at 0 without failing.
                     bool healthZero = healthVal >= 0 && healthVal < 0.0001;
                     bool noFail = modsList != null && modsList.Contains("NF");
                     
                     snapshot.Failed = healthZero && !noFail;
                     
                     // Debug Fail State
                     if (healthZero)
                     {
                         WriteLog($"FailCheck: Health={healthVal:F4}, Failed={snapshot.Failed} (Health~0 & !NF)");
                     }
                }

                // Stats dictionary for hit counts
                IntPtr statisticsDict = _scanner.ReadIntPtr(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.statistics));
                ReadStatisticsDict(statisticsDict, snapshot);

                // Convert standardized score to classic display score
                int objectCount = snapshot.TotalObjects ?? 0;
                if (objectCount > 0)
                {
                    double classicScore = ((Math.Pow(objectCount, 2) * 32.57 + 100000) * standardizedScore) / 1000000.0;
                    snapshot.Score = (long)Math.Round(classicScore);
                }
                else
                {
                    snapshot.Score = standardizedScore;
                }

                // Grade Calculation
                snapshot.Grade = CalculateGrade(snapshot);

                // Read Mods and Clock Rate (Already read above)
                // List<string> modsList = ReadMods(scoreInfoPtr);
                if (modsList != null) _currentModsList = modsList;
                snapshot.ModsList = modsList;
                snapshot.Mods = modsList != null && modsList.Count > 0 ? string.Join(",", modsList) : "NM";


                double clockRate = GetClockRate();
                uint currentModsBits = GetModsBits(modsList);

                // Guard against invalid clockRate
                if (clockRate < 0.1 || clockRate > 5.0) clockRate = 1.0;

                // Sync Difficulty Attributes if mods are active
                if (!string.IsNullOrEmpty(_currentOsuFilePath) && modsList != null)
                {
                    var (modAR, modCS, modOD, modHP) = _rosuService.GetDifficultyAttributes(
                        _currentOsuFilePath,
                        modsList,
                        clockRate,
                        _currentModSettings.AR ?? -1.0,
                        _currentModSettings.CS ?? -1.0,
                        _currentModSettings.OD ?? -1.0,
                        _currentModSettings.HP ?? -1.0
                    );

                    snapshot.AR = (float)modAR;
                    snapshot.CS = (float)modCS;
                    snapshot.OD = (float)modOD;
                    snapshot.HP = (float)modHP;

                    if (_cachedStats != null && _cachedStats.MapLength > 0)
                    {
                        snapshot.TotalTimeMs = (int)(_cachedStats.MapLength / clockRate);
                    }
                }

                // Final PP Calculation
                if (snapshot.HitCounts != null)
                {
                    int passedObjects = snapshot.HitCounts.Count300 + snapshot.HitCounts.Count100 + snapshot.HitCounts.Count50 + snapshot.HitCounts.Misses;

                    // DEBUG: Log PP inputs (throttled)
                    WriteLogThrottled("pp-input", $"CalculatePp Input: Path='{_currentOsuFilePath}', Mods={currentModsBits}, Combo={snapshot.Combo}, Acc={snapshot.Accuracy:F4}, Clock={clockRate}");

                    // CRITICAL: Ensure context is updated even if GetDifficultyAttributes wasn't called (e.g. if we had cached attributes but context was lost/reset)
                    if (!string.IsNullOrEmpty(_currentOsuFilePath))
                    {
                        _rosuService.UpdateContext(_currentOsuFilePath);
                    }
                    else
                    {
                        WriteLog("CalculatePp WARNING: _currentOsuFilePath is null/empty!");
                    }

                    snapshot.PP = _rosuService.CalculatePp(
                        currentModsBits,
                        snapshot.Combo ?? 0,
                        snapshot.HitCounts.Count300,
                        snapshot.HitCounts.Count100,
                        snapshot.HitCounts.Count50,
                        snapshot.HitCounts.Misses,
                        passedObjects,
                        snapshot.HitCounts.SliderTailHit,
                        snapshot.HitCounts.SmallTickHit,
                        snapshot.HitCounts.LargeTickHit,
                        clockRate,
                        _currentModSettings.AR ?? -1.0,
                        _currentModSettings.CS ?? -1.0,
                        _currentModSettings.OD ?? -1.0,
                        _currentModSettings.HP ?? -1.0,
                        isLazer: true
                    );

                    
                    WriteLogThrottled("pp-result", $"CalculatePp Result: {snapshot.PP:F2}");
                }

                // Pause Detection via Beatmap Clock
                IntPtr beatmapClockPtr = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.beatmapClock));
                if (beatmapClockPtr != IntPtr.Zero)
                {
                    IntPtr finalClockSourcePtr = _scanner.ReadIntPtr(IntPtr.Add(beatmapClockPtr, Offsets.FramedBeatmapClock.finalClockSource));
                    if (finalClockSourcePtr != IntPtr.Zero)
                    {
                        double currentTime = _scanner.ReadDouble(IntPtr.Add(finalClockSourcePtr, Offsets.FramedClock.CurrentTime));
                        // Robust Pause Detection (V4)
                        if (Math.Abs(currentTime - _lastTime) > 0.001)
                        {
                            _isPausedState = false;
                            _lastTime = currentTime;
                            _lastTimeChange = DateTime.Now; 
                        }
                        else
                        {
                            // If time hasn't changed for > 80ms, consider it paused.
                            if ((DateTime.Now - _lastTimeChange).TotalMilliseconds > 80)
                            {
                                _isPausedState = true;
                            }
                        }
                        snapshot.IsPaused = _isPausedState;
                        snapshot.TimeMs = (int)currentTime;
                    }
                }

            }
            catch (Exception ex)
            {
                WriteLog($"ReadScoreInfo Error: {ex.Message}");
            }
        }

        private string? ReadScoreHash(IntPtr scoreInfoPtr)
        {
            if (scoreInfoPtr == IntPtr.Zero) return null;
            // Scan for a 32-character hex string (MD5 hash) in the ScoreInfo object
            // These offsets cover most Lazer versions
            int[] hashOffsets = { 0x98, 0x80, 0xA0, 0x90, 0x88, 0x78 };
            foreach (var offset in hashOffsets)
            {
                try
                {
                    IntPtr ptr = _scanner.ReadIntPtr(IntPtr.Add(scoreInfoPtr, offset));
                    if (ptr == IntPtr.Zero) continue;
                    string s = _scanner.ReadString(ptr);
                    if (!string.IsNullOrEmpty(s) && s.Length == 32 && s.All(c => "0123456789abcdefABCDEF".Contains(c)))
                    {
                        return s;
                    }
                }
                catch { }
            }
            return null;
        }

        private long ReadResultScoreInfo(IntPtr scoreInfoPtr, LiveSnapshot snapshot)

        {
            long standardizedScore = _scanner.ReadInt64(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.TotalScore));
            snapshot.Accuracy = _scanner.ReadDouble(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.Accuracy));
            
            // Try reading both fields to ensure we catch the highest combo
            int maxC = _scanner.ReadInt32(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.MaxCombo));
            int curC = _scanner.ReadInt32(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.Combo));
            
            snapshot.MaxCombo = Math.Max(maxC, curC);
            snapshot.Combo = snapshot.MaxCombo;

            try

            {
                long dateTicks = _scanner.ReadInt64(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.Date));
                if (dateTicks > 0)
                {
                    snapshot.ScoreDate = new DateTime(dateTicks);
                }
            }
            catch { }

            IntPtr statisticsDict = _scanner.ReadIntPtr(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.statistics));
            ReadStatisticsDict(statisticsDict, snapshot);
            return standardizedScore;
        }

        private void UpdateResultScreenSnapshot(IntPtr scoreInfoPtr, LiveSnapshot snapshot)
        {
            try
            {
                // Reset settings before reading from result screen score
                _currentModSettings = new ModSettings();

                long standardizedScore = ReadResultScoreInfo(scoreInfoPtr, snapshot);
                
                // Read Replay Hash from ScoreInfo
                snapshot.ReplayHash = ReadScoreHash(scoreInfoPtr);


                // Read Maximum Statistics for reliable total object count
                IntPtr maxStatsDict = _scanner.ReadIntPtr(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.maximumStatistics));
                if (maxStatsDict != IntPtr.Zero)
                {
                    int totalObjects = GetObjectCountFromMaxStatistics(maxStatsDict);
                    if (totalObjects > 0) snapshot.TotalObjects = totalObjects;
                }
                
                // FIXED: Read mods from the SCORE itself, not the global selected mods (which might be reset)
                List<string> modsList = ReadMods(scoreInfoPtr);
                uint modsBits = GetModsBits(modsList);
                
                // FIXED: Calculate clock rate from MODS, because global clock is likely 1.0 at Results screen
                double clockRate = RosuService.GetClockRateFromMods(modsBits);
                
                // If clock rate is invalid, force 1.0
                if (clockRate < 0.1 || clockRate > 5.0) clockRate = 1.0;
                
                WriteLog($"UpdateResultScreen: Mods={string.Join(",", modsList)}, Rate={clockRate}, Path={_currentOsuFilePath}");


                if (!string.IsNullOrEmpty(_currentOsuFilePath))
                {
                    var attrs = _rosuService.GetDifficultyAttributes(_currentOsuFilePath, modsList, clockRate, _currentModSettings.AR ?? -1.0, _currentModSettings.CS ?? -1.0, _currentModSettings.OD ?? -1.0, _currentModSettings.HP ?? -1.0);
                    snapshot.AR = (float)attrs.AR;
                    snapshot.CS = (float)attrs.CS;
                    snapshot.OD = (float)attrs.OD;
                    snapshot.HP = (float)attrs.HP;

                    snapshot.Stars = _rosuService.GetStars(modsBits, 0, clockRate, _currentModSettings.AR ?? -1.0, _currentModSettings.CS ?? -1.0, _currentModSettings.OD ?? -1.0, _currentModSettings.HP ?? -1.0, isLazer: true);

                    // Calculate Achieved PP
                    snapshot.PP = _rosuService.CalculatePp(
                        modsBits,
                        snapshot.MaxCombo ?? 0,
                        snapshot.HitCounts?.Count300 ?? 0,
                        snapshot.HitCounts?.Count100 ?? 0,
                        snapshot.HitCounts?.Count50 ?? 0,
                        snapshot.HitCounts?.Misses ?? 0,
                        snapshot.TotalObjects ?? _totalObjects,
                        snapshot.HitCounts?.SliderTailHit ?? 0,
                        snapshot.HitCounts?.SmallTickHit ?? 0,
                        snapshot.HitCounts?.LargeTickHit ?? 0,
                        clockRate,
                        _currentModSettings.AR ?? -1.0,
                        _currentModSettings.CS ?? -1.0,
                        _currentModSettings.OD ?? -1.0,
                        _currentModSettings.HP ?? -1.0,
                        isLazer: true
                    );
                    WriteLog($"Calculated PP: {snapshot.PP} (Combo={snapshot.MaxCombo}, Miss={snapshot.HitCounts?.Misses}, Acc={snapshot.Accuracy})");

                    var ppResult = _rosuService.CalculatePpIfFc(_currentOsuFilePath, modsList, 100.0, _currentModSettings.AR ?? -1.0, _currentModSettings.CS ?? -1.0, _currentModSettings.OD ?? -1.0, _currentModSettings.HP ?? -1.0, clockRate, isLazer: true);

                    snapshot.PPIfFC = ppResult.PP;
                    if (snapshot.MaxCombo == null || snapshot.MaxCombo == 0) snapshot.MaxCombo = ppResult.MaxCombo;

                    if (ppResult.MapLength > 0)
                    {
                        snapshot.TotalTimeMs = (int)(ppResult.MapLength / clockRate);
                    }

                    // UpdatePlusAttributes(snapshot); // PP+ REMOVED
                }

                int objectCount = snapshot.TotalObjects ?? 0;
                snapshot.Score = objectCount > 0 ? (long)Math.Round(((Math.Pow(objectCount, 2) * 32.57 + 100000) * standardizedScore) / 1000000.0) : standardizedScore;
                snapshot.Grade = CalculateGrade(snapshot);
                snapshot.ModsList = modsList;
                snapshot.Mods = modsList.Count > 0 ? string.Join(",", modsList) : "NM";

                // Read HitEvents from ScoreInfo on results screen
                try {
                    IntPtr hitEventsList = _scanner.ReadIntPtr(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.HitEvents));
                    if (hitEventsList != IntPtr.Zero) {
                        ReadHitEventsFromList(hitEventsList, snapshot);
                    }
                } catch { }


            }
            catch (Exception ex)
            {
                WriteLog($"UpdateResultScreen Error: {ex.Message}");
            }
        }

        private string CalculateGrade(LiveSnapshot snapshot)
        {
            // Lazer Standard grading rules:
            // SS/X: 100% accuracy (all 300s, no misses)
            // S: >90% 300s, <1% 50s, no misses
            // A: >80% 300s and no misses, OR >90% 300s
            // B: >70% 300s and no misses, OR >80% 300s
            // C: >60% 300s
            // D: <60% 300s

            double acc = snapshot.Accuracy ?? 0;
            int misses = snapshot.HitCounts?.Misses ?? 0;
            int c300 = snapshot.HitCounts?.Count300 ?? 0;
            int c100 = snapshot.HitCounts?.Count100 ?? 0;
            int c50 = snapshot.HitCounts?.Count50 ?? 0;
            int total = c300 + c100 + c50 + misses;
            
            bool silver = snapshot.Mods != null && (snapshot.Mods.Contains("HD") || snapshot.Mods.Contains("FL"));

            // Calculate 300 ratio
            double ratio300 = total > 0 ? (double)c300 / total : 0;
            double ratio50 = total > 0 ? (double)c50 / total : 0;

            // SS requires 100% accuracy (all 300s, no misses, no 100s, no 50s)
            if (acc >= 1.0 || (misses == 0 && c100 == 0 && c50 == 0 && c300 > 0))
                return silver ? "SSH" : "SS";

            // S requires >90% 300s, <1% 50s, and no misses
            if (misses == 0 && ratio300 > 0.9 && ratio50 < 0.01)
                return silver ? "SH" : "S";

            // A requires >80% 300s with no misses, OR >90% 300s
            if ((misses == 0 && ratio300 > 0.8) || ratio300 > 0.9)
                return "A";

            // B requires >70% 300s with no misses, OR >80% 300s
            if ((misses == 0 && ratio300 > 0.7) || ratio300 > 0.8)
                return "B";

            // C requires >60% 300s
            if (ratio300 > 0.6)
                return "C";

            return "D";
        }

        private void ReadHitEvents(IntPtr scoreProcessorPtr, LiveSnapshot snapshot)
        {
            if (scoreProcessorPtr == IntPtr.Zero) return;

            try
            {
                IntPtr hitEventsList = _scanner.ReadIntPtr(IntPtr.Add(scoreProcessorPtr, Offsets.ScoreProcessor.hitEvents));
                ReadHitEventsFromList(hitEventsList, snapshot);
            }
            catch (Exception ex)
            {
                WriteLog($"ReadHitEvents Error: {ex.Message}");
            }
        }

        private void ReadHitEventsFromList(IntPtr hitEventsList, LiveSnapshot snapshot)
        {
            if (hitEventsList == IntPtr.Zero) return;

            try
            {
                // .NET List<T> layout: +0x8 items, +0x10 size
                int count = _scanner.ReadInt32(IntPtr.Add(hitEventsList, 0x10));
                if (count <= 0 || count > 30000) return;

                IntPtr itemsArray = _scanner.ReadIntPtr(IntPtr.Add(hitEventsList, 0x8));
                if (itemsArray == IntPtr.Zero) return;

                // .NET Array layout: +0x10 start of data
                // In Lazer, HitEvent is often a struct or class.
                // Tosu treats it as an inlined struct of size 0x40.

                List<double> offsets = new();
                var aimOffsets = new List<object[]>();

                for (int i = 0; i < count; i++)
                {
                    IntPtr hitEventAddr = IntPtr.Add(itemsArray, 0x10 + (i * 0x40));

                    // TimeOffset at +0x10 (double)
                    double timeOffset = _scanner.ReadDouble(IntPtr.Add(hitEventAddr, 0x10));

                    // HitResult at +0x18
                    int hitResultRaw = _scanner.ReadInt32(IntPtr.Add(hitEventAddr, 0x18));
                    LazerHitResults result = (LazerHitResults)hitResultRaw;

                    // UR samples (lazer): include only actual timing judgments. Exclude slider ticks and slider tail.
                    bool isUrSample = result == LazerHitResults.Great ||
                                      result == LazerHitResults.Perfect ||
                                      result == LazerHitResults.Good ||
                                      result == LazerHitResults.Ok ||
                                      result == LazerHitResults.Meh;

                    if (isUrSample)
                    {
                        offsets.Add(timeOffset);
                    }

                    // Aim scatter (include misses too). We don't yet have reliable cursor/object positions,
                    // so this is a placeholder record that still preserves time and result.
                    // Schema: [timeOffsetMs, hitResultRaw, dx, dy]
                    if (result == LazerHitResults.Great || result == LazerHitResults.Perfect || result == LazerHitResults.Good ||
                        result == LazerHitResults.Ok || result == LazerHitResults.Meh || result == LazerHitResults.Miss)
                    {
                        aimOffsets.Add(new object[] { timeOffset, hitResultRaw, 0.0, 0.0 });
                    }
                }

                snapshot.LiveHitOffsets = offsets;
                snapshot.LiveUR = CalculateLiveUR(offsets, RosuService.GetClockRateFromMods(GetModsBits(snapshot.ModsList)));

                // Generate histogram (5ms bins)
                var histogram = new Dictionary<int, int>();
                foreach (var o in offsets)
                {
                    int bin = (int)Math.Round(o / 5.0) * 5;
                    if (!histogram.ContainsKey(bin)) histogram[bin] = 0;
                    histogram[bin]++;
                }
                snapshot.LiveHitOffsetHistogram = histogram;

                // Store aim offsets json in snapshot for persistence.
                snapshot.AimOffsetsJson = JsonSerializer.Serialize(aimOffsets);
            }
            catch (Exception ex)
            {
                WriteLog($"ReadHitEventsFromList Error: {ex.Message}");
            }
        }


        private double CalculateLiveUR(List<double> offsets, double clockRate = 1.0)
        {
            if (offsets == null || offsets.Count == 0) return 0;
            
            // Normalize offsets by clock rate (per osu!lazer HitEventExtensions.cs line 45)
            // This accounts for TimeOffset scaling with gameplay rate
            if (clockRate <= 0) clockRate = 1.0;
            
            double sum = 0;
            foreach (var o in offsets) sum += o / clockRate;
            double mean = sum / offsets.Count;
            
            double sumSquares = 0;
            foreach (var o in offsets) sumSquares += Math.Pow((o / clockRate) - mean, 2);
            
            double variance = sumSquares / offsets.Count;
            double stdDev = Math.Sqrt(variance);
            
            return stdDev * 10.0;
        }

        private void ReadStatisticsDict(IntPtr dictAddr, LiveSnapshot snapshot)
        {
            if (dictAddr == IntPtr.Zero) return;

            // .NET Dictionary<TKey, TValue> layout (64-bit CoreCLR)
            // _count is at 0x38 (modern .NET) or 0x18 (older)
            int count = _scanner.ReadInt32(IntPtr.Add(dictAddr, 0x38)); 
            if (count < 0 || count > 50000) count = _scanner.ReadInt32(IntPtr.Add(dictAddr, 0x18));
            if (count < 0 || count > 50000) return;

            // _entries is at 0x10 (modern) or 0x18?
            IntPtr entriesPtr = _scanner.ReadIntPtr(IntPtr.Add(dictAddr, 0x10)); 
            if (entriesPtr == IntPtr.Zero) return;

            int hit300 = 0, hit100 = 0, hit50 = 0, hitMiss = 0;
            int hitSliderTail = 0, hitSmallTick = 0, hitLargeTick = 0;

            for (int i = 0; i < count; i++)
            {
                // struct Entry { int hash; int next; int key; int value; } size=16
                IntPtr entryAddr = IntPtr.Add(entriesPtr, 0x10 + (i * 0x10));

                int key = _scanner.ReadInt32(IntPtr.Add(entryAddr, 0x8));
                int value = _scanner.ReadInt32(IntPtr.Add(entryAddr, 0xC));

                if (key <= 0 || value < 0) continue;

                LazerHitResults result = (LazerHitResults)key;
                switch (result)
                {
                    case LazerHitResults.Great: hit300 += value; break;
                    case LazerHitResults.Perfect: hit300 += value; break;
                    case LazerHitResults.Good: hit100 += value; break; 
                    case LazerHitResults.Ok: hit100 += value; break;
                    case LazerHitResults.Meh: hit50 += value; break;
                    case LazerHitResults.Miss: hitMiss += value; break;
                    case LazerHitResults.SliderTailHit: hitSliderTail += value; break;
                    case LazerHitResults.SmallTickHit: hitSmallTick += value; break;
                    case LazerHitResults.LargeTickHit: hitLargeTick += value; break;
                }
            }
            snapshot.HitCounts = new HitCounts(hit300, hit100, hit50, hitMiss, hitSliderTail, hitSmallTick, hitLargeTick);
        }

        private void UpdateStaticAttributesIfNeeded(List<string> currentMods, double clockRate)
        {
            if (string.IsNullOrEmpty(_currentOsuFilePath)) return;

            uint modsBits = GetModsBits(currentMods);

            // If clock rate is invalid, try to guess from mods
            if (clockRate < 0.1 || clockRate > 5.0)
            {
                if (_currentModSettings.SpeedChange.HasValue)
                    clockRate = _currentModSettings.SpeedChange.Value;
                else
                    clockRate = RosuService.GetClockRateFromMods(modsBits);
            }

            // Calculate stars for full map with clock rate and overrides
            WriteLogThrottled("calc-stars-input", $"Calculating stars: modsBits={modsBits}, clockRate={clockRate:F3}, mods={string.Join(",", currentMods)}, Overrides: AR={_currentModSettings.AR}, CS={_currentModSettings.CS}, OD={_currentModSettings.OD}, HP={_currentModSettings.HP}");

            _staticStars = _rosuService.GetStars(
                modsBits,
                0,
                clockRate,
                _currentModSettings.AR ?? -1.0,
                _currentModSettings.CS ?? -1.0,
                _currentModSettings.OD ?? -1.0,
                _currentModSettings.HP ?? -1.0,
                isLazer: true
            );

            // Calculate BASE (NM) attributes using rosu
            var nmAttrs = _rosuService.GetDifficultyAttributes(0, 1.0, -1, -1, -1, -1);
            _baseCS = (float)nmAttrs.CS;
            _baseAR = (float)nmAttrs.AR;
            _baseOD = (float)nmAttrs.OD;
            _baseHP = (float)nmAttrs.HP;
            _baseStars = _rosuService.GetStars(0, 0, 1.0, -1, -1, -1, -1, isLazer: true);

            WriteLogThrottled("calc-stars-result", $"Calculated stars: {_staticStars:F2}");

            double rosuBpm = _rosuService.GetBpm(modsBits);
            double effectiveBase = _baseModeBpm;

            // Fallback: If parser failed, estimate base from rosuBpm
            if (effectiveBase <= 3.0 && rosuBpm > 0)
            {
                if (currentMods.Contains("DT") || currentMods.Contains("NC"))
                    effectiveBase = rosuBpm / 1.5;
                else if (currentMods.Contains("HT"))
                    effectiveBase = rosuBpm / 0.75;
                else
                    effectiveBase = rosuBpm;
            }

            if (effectiveBase > 0)
            {
                _staticBpm = effectiveBase * clockRate;
                _baseModeBpm = effectiveBase; // Sync base BPM
                DebugLog($"UpdateStaticAttributes [V2.1.FIX]: Final calculated BPM={_staticBpm:F2} (Base={effectiveBase:F2}, Rate={clockRate:F2})");
            }
            else
            {
                _staticBpm = rosuBpm;
                _baseModeBpm = _rosuService.BaseBpm; // Sync from rosu fallback
                DebugLog($"UpdateStaticAttributes [V2.1.FIX]: Fallback to rosuBpm={rosuBpm}");
            }

            // Update base attributes from rosuNM context
            _baseCS = (float)_rosuService.CS;
            _baseAR = (float)_rosuService.AR;
            _baseOD = (float)_rosuService.OD;
            _baseHP = (float)_rosuService.HP;

            // Update min/max BPM with clock rate
            _minBpm = _baseMinBpm * clockRate;
            _maxBpm = _baseMaxBpm * clockRate;

            DebugLog($"UpdateStaticAttributes [DA-CHECK]: Mods={string.Join(",", currentMods)} | Stars={_staticStars:F2} | BPM={_staticBpm:F2} | ClockRate={clockRate:F3} | AR={_currentModSettings.AR ?? -1:F1} | CS={_currentModSettings.CS ?? -1:F1} | OD={_currentModSettings.OD ?? -1:F1} | HP={_currentModSettings.HP ?? -1:F1}");
        }

        private string GetLazerFilesPath()
        {
            // 1. User Settings override
            string? customPath = SettingsManager.Current.LazerPath;
            if (!string.IsNullOrEmpty(customPath) && Directory.Exists(Path.Combine(customPath, "files")))
            {
                return Path.Combine(customPath, "files");
            }

            // 2. Primary: LocalAppData/osu (The standard path for modern Lazer)
            string localOsu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu");
            if (Directory.Exists(Path.Combine(localOsu, "files")))
            {
                return Path.Combine(localOsu, "files");
            }

            // 3. Fallback: Check storage.ini for custom migration paths
            string roamingPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu");
            string storageIni = Path.Combine(roamingPath, "storage.ini");
            if (!File.Exists(storageIni)) storageIni = Path.Combine(localOsu, "storage.ini");

            if (File.Exists(storageIni))
            {
                try {
                    var lines = File.ReadAllLines(storageIni);
                    var fullPathLine = lines.FirstOrDefault(l => l.StartsWith("FullPath =", StringComparison.OrdinalIgnoreCase));
                    if (fullPathLine != null)
                    {
                        var path = fullPathLine.Split('=').Last().Trim();
                        if (Directory.Exists(Path.Combine(path, "files"))) return Path.Combine(path, "files");
                    }
                } catch { }
            }

            // 4. Final Fallbacks
            if (Directory.Exists(Path.Combine(roamingPath, "files"))) return Path.Combine(roamingPath, "files");
            if (Directory.Exists(@"G:\osu-lazer-data\files")) return @"G:\osu-lazer-data\files";

            return Path.Combine(localOsu, "files"); // Default to Local if all else fails
        }

        private void UpdateBeatmapFile(IntPtr beatmapInfoPtr)

        {
            if (beatmapInfoPtr == IntPtr.Zero) return;

            try
            {
                IntPtr hashPtr = _scanner.ReadIntPtr(IntPtr.Add(beatmapInfoPtr, Offsets.BeatmapInfo.Hash));
                string hash = _scanner.ReadString(hashPtr);

                if (IsValidHash(hash) && hash != _currentBeatmapHash)
                {
                    _currentBeatmapHash = hash;
                    string lazrFiles = GetLazerFilesPath();

                    if (hash.Length >= 2)

                    {
                        string p = Path.Combine(lazrFiles, hash.Substring(0, 1), hash.Substring(0, 2), hash);
                        if (File.Exists(p))
                        {
                            _currentOsuFilePath = p;
                            // Update Rosu context
                            _rosuService.UpdateContext(p);

                            // Calculate static map attributes with current mods
                            List<string> currentMods = ReadModsFromMemory();
                            uint modsBits = GetModsBits(currentMods);
                            double clockRate = GetClockRate();

                            // Parse BPM and Object Count from file as base values
                            ParseMapDataFromFile(p);

                            // Calculate static map attributes with current mods/rate
                            UpdateStaticAttributesIfNeeded(currentMods, clockRate);
                        }
                        else
                        {
                            _currentOsuFilePath = null;
                            _staticStars = 0;
                            // _maxPp removed
                            _staticBpm = 0;
                            _minBpm = 0;
                            _maxBpm = 0;
                            _totalObjects = 0;
                        }
                    }
                }
            }
            catch { }
        }

        private void ParseMapDataFromFile(string path)
        {
            DebugLog($"ParseMapDataFromFile: Analyzing {path}");
            try
            {
                bool inTimingPoints = false;
                bool inHitObjects = false;
                int objCount = 0;
                int circles = 0, sliders = 0, spinners = 0;

                List<(double time, double beatLength)> timingPoints = new();
                double firstTime = -1;
                double lastTime = 0;
                _objectStartTimes.Clear();
                foreach (var line in File.ReadLines(path))
                {
                    string l = line.Trim();
                    if (string.IsNullOrEmpty(l)) continue;

                    if (l.StartsWith("["))
                    {
                        inTimingPoints = string.Equals(l, "[TimingPoints]", StringComparison.OrdinalIgnoreCase);
                        inHitObjects = string.Equals(l, "[HitObjects]", StringComparison.OrdinalIgnoreCase);
                        if (inTimingPoints) DebugLog("ParseMapDataFromFile: Found [TimingPoints] section.");
                        continue;
                    }

                    if (inTimingPoints)
                    {
                        var parts = l.Split(',');
                        if (parts.Length >= 2)
                        {
                            if (double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double time) &&
                                double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double beatLength))
                            {
                                // 7th field is uninherited (1 = true/red line, 0 = false/green line)
                                // If missing, usually defaults to 1?
                                bool uninherited = true;
                                if (parts.Length >= 7)
                                    uninherited = parts[6].Trim() == "1";

                                if (uninherited && beatLength > 0)
                                {
                                    timingPoints.Add((time, beatLength));
                                }

                                if (firstTime < 0) firstTime = time;
                                lastTime = time; // Approximation of map end references
                            }
                        }
                    }
                    else if (inHitObjects)
                    {
                        objCount++;
                        var parts = l.Split(',');
                        if (parts.Length >= 4)
                        {
                            if (double.TryParse(parts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double t))
                            {
                                _objectStartTimes.Add(t);
                                if (t > lastTime) lastTime = t;
                            }

                            if (int.TryParse(parts[3], out int type))
                            {
                                if ((type & 1) != 0) circles++;
                                else if ((type & 2) != 0) sliders++;
                                else if ((type & 8) != 0) spinners++;
                            }
                        }
                    }
                }

                _totalObjects = objCount;
                _circles = circles;
                _sliders = sliders;
                _spinners = spinners;
                _objectStartTimes.Sort();
                WriteLog($"ParseMapDataFromFile: path={path}, objects={_totalObjects}, timingPoints={timingPoints.Count}");

                if (timingPoints.Count > 0)
                {
                    // Calculate Min/Max
                    var bpms = timingPoints.Select(x => 60000.0 / x.beatLength).ToList();
                    _minBpm = bpms.Min();
                    _maxBpm = bpms.Max();

                    // Calculate Mode (Most common BPM by duration)
                    // If only 1 point, it's that.
                    if (timingPoints.Count == 1)
                    {
                        _staticBpm = bpms[0];
                    }
                    else
                    {
                        // Sort by time
                        timingPoints.Sort((a, b) => a.time.CompareTo(b.time));

                        double maxDuration = 0;
                        double modeBpm = bpms[0];

                        Dictionary<double, double> bpmDurations = new();

                        for (int i = 0; i < timingPoints.Count; i++)
                        {
                            double startTime = timingPoints[i].time;
                            double endTime = (i == timingPoints.Count - 1) ? lastTime : timingPoints[i + 1].time;
                            if (endTime < startTime) endTime = startTime; // Should not happen if sorted

                            double dur = endTime - startTime;
                            double bpm = 60000.0 / timingPoints[i].beatLength;

                            // Round BPM for grouping (e.g. 150.001 -> 150)
                            double roundedBpm = Math.Round(bpm);

                            if (!bpmDurations.ContainsKey(roundedBpm)) bpmDurations[roundedBpm] = 0;
                            bpmDurations[roundedBpm] += dur;
                        }

                        // Find max duration
                        foreach (var kvp in bpmDurations)
                        {
                            if (kvp.Value > maxDuration)
                            {
                                maxDuration = kvp.Value;
                                modeBpm = kvp.Key;
                            }
                        }
                        _staticBpm = modeBpm;
                        _baseModeBpm = modeBpm;
                        DebugLog($"ParseMapDataFromFile result: BaseBPM={modeBpm}, Min={_minBpm}, Max={_maxBpm}");
                        _baseMinBpm = _minBpm;
                        _baseMaxBpm = _maxBpm;
                    }
                }
                else
                {
                    _staticBpm = 0;
                    _minBpm = 0;
                    _maxBpm = 0;
                    _baseMinBpm = 0;
                    _baseMaxBpm = 0;
                }

            }
            catch
            {
                _totalObjects = 0;
                _staticBpm = 0;
                _baseMinBpm = 0;
                _baseMaxBpm = 0;
            }
        }


        private List<string> ReadModsFromMemory()
        {
            try
            {
                int currentGamemode = ReadGamemode();
                if ((_modVTableMap.Count == 0 || currentGamemode != _lastGamemode) && _gameBaseAddress != IntPtr.Zero)
                {
                    BuildModVTableMap(currentGamemode);
                }

                _currentModSettings = new ModSettings(); // Reset
                IntPtr bindableMods = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameDesktop.SelectedMods));

                if (bindableMods == IntPtr.Zero)
                {
                    // Try fallback to AvailableMods if SelectedMods is zero for some reason (rare)
                    WriteLog($"ReadModsFromMemory: SelectedMods Bindable is ZERO at offset {Offsets.OsuGameDesktop.SelectedMods}");
                    return new List<string>();
                }

                // SelectedMods is a BindableList<T>
                IntPtr bindableMT = _scanner.ReadIntPtr(bindableMods);
                IntPtr listPtr = _scanner.ReadIntPtr(IntPtr.Add(bindableMods, 0x18));
                if (listPtr == IntPtr.Zero) listPtr = _scanner.ReadIntPtr(IntPtr.Add(bindableMods, 0x20));
                if (listPtr == IntPtr.Zero) listPtr = _scanner.ReadIntPtr(IntPtr.Add(bindableMods, 0x10));
                if (listPtr == IntPtr.Zero) listPtr = _scanner.ReadIntPtr(IntPtr.Add(bindableMods, 0x28));
                if (listPtr == IntPtr.Zero) listPtr = _scanner.ReadIntPtr(IntPtr.Add(bindableMods, 0x30));
                if (listPtr == IntPtr.Zero) listPtr = _scanner.ReadIntPtr(IntPtr.Add(bindableMods, 0x38));
                if (listPtr == IntPtr.Zero) listPtr = _scanner.ReadIntPtr(IntPtr.Add(bindableMods, 0x40));


                WriteLog($"ReadModsFromMemory: bindableMods={bindableMods:X}, MT={bindableMT:X}, listPtr={listPtr:X}");

                if (listPtr != IntPtr.Zero)
                {
                    // List<T> layout: 0x8 = _items (Array), 0x10 = _size
                    int size = _scanner.ReadInt32(IntPtr.Add(listPtr, 0x10));
                    IntPtr itemsPtr = _scanner.ReadIntPtr(IntPtr.Add(listPtr, 0x8));

                    WriteLog($"ReadModsFromMemory: list size={size}, itemsPtr={itemsPtr:X}");

                    if (itemsPtr != IntPtr.Zero && size >= 0 && size < 50)
                    {
                        List<string> mods = new();
                        for (int i = 0; i < size; i++)
                        {
                            IntPtr modPtr = _scanner.ReadIntPtr(IntPtr.Add(itemsPtr, 0x10 + i * 8));
                            FlattenAndAddMod(modPtr, mods);
                        }
                        var distinctMods = mods.Distinct().ToList();
                        _currentModsList = distinctMods;
                        if (distinctMods.Count > 0)
                        {
                            WriteLog($"ReadModsFromMemory: Detected mods: {string.Join(",", distinctMods)}");
                        }
                        return distinctMods;
                    }
                    else
                    {
                        _currentModsList = new List<string>();
                        if (size > 0)
                        {
                            WriteLog($"ReadModsFromMemory: size={size} but itemsPtr is NULL or size out of range.");
                        }
                        return _currentModsList;
                    }
                }
                else
                {
                    WriteLog($"ReadModsFromMemory: Failed to find listPtr at 0x18/0x20 from bindable {bindableMods:X}");
                }
            }
            catch (Exception ex)
            {
                WriteLog($"ReadModsFromMemory Error: {ex.Message}");
            }
            return new List<string>();
        }

        private void FlattenAndAddMod(IntPtr modPtr, List<string> mods)
        {
            if (modPtr == IntPtr.Zero) return;

            IntPtr vtable = _scanner.ReadIntPtr(modPtr);
            if (vtable == IntPtr.Zero) return;

            // Check for MultiMod using VTable signature (tosu logic)
            // 0x1000000 at offset 0, and 8193 at offset 3
            int sig1 = _scanner.ReadInt32(vtable);
            int sig2 = _scanner.ReadInt32(IntPtr.Add(vtable, 3));

            if (sig1 == 16777216 && sig2 == 8193)
            {
                // It is a MultiMod. The mods list (Array) is at +0x10
                IntPtr modsArrayPtr = _scanner.ReadIntPtr(IntPtr.Add(modPtr, 0x10));
                if (modsArrayPtr != IntPtr.Zero)
                {
                    // Array structure: +0x8 = length, +0x10 = start of items
                    int size = _scanner.ReadInt32(IntPtr.Add(modsArrayPtr, 0x8));
                    if (size > 0 && size < 50)
                    {
                        for (int k = 0; k < size; k++)
                        {
                            // Read item pointer (Array entries are pointers to Mod objects)
                            IntPtr nestedModPtr = _scanner.ReadIntPtr(IntPtr.Add(modsArrayPtr, 0x10 + k * 8));
                            FlattenAndAddMod(nestedModPtr, mods);
                        }
                    }
                }
            }
            else
            {
                if (_modVTableMap.TryGetValue(vtable, out string? acronym))
                {
                    mods.Add(acronym);
                    WriteLog($"ReadModsFromMemory: VTable={vtable:X} -> {acronym}");

                    // Extract settings if relevant
                    if (acronym == "DT" || acronym == "HT" || acronym == "NC")
                    {
                        IntPtr speedChangeBindable = _scanner.ReadIntPtr(IntPtr.Add(modPtr, 0x10));
                        if (speedChangeBindable != IntPtr.Zero)
                        {
                            // In .NET 8, Bindable<double>.Value is usually at 0x40. 
                            // Default (Original rate) is at 0x48.
                            double customRate = _scanner.ReadDouble(IntPtr.Add(speedChangeBindable, 0x40));
                            if (customRate > 0.05 && customRate < 5.0)
                            {
                                _currentModSettings.SpeedChange = customRate;
                                DebugLog($"SpeedChange read: {customRate:F2} from bindable {speedChangeBindable:X}");
                            }
                        }
                        else
                        {
                            DebugLog($"ReadModsFromMemory: {acronym} SpeedChange Bindable is ZERO at offset 0x10 from modPtr {modPtr:X}");
                        }
                    }
                    else if (acronym == "DA")
                    {
                        try
                        {
                            IntPtr drainRateBindable = _scanner.ReadIntPtr(IntPtr.Add(modPtr, 0x10));
                            IntPtr overallDifficultyBindable = _scanner.ReadIntPtr(IntPtr.Add(modPtr, 0x18));
                            IntPtr circleSizeBindable = _scanner.ReadIntPtr(IntPtr.Add(modPtr, 0x28));
                            IntPtr approachRateBindable = _scanner.ReadIntPtr(IntPtr.Add(modPtr, 0x30));

                            DebugLog($"DA TOGGLE: modPtr={modPtr:X}, HP_B={drainRateBindable:X}, OD_B={overallDifficultyBindable:X}, CS_B={circleSizeBindable:X}, AR_B={approachRateBindable:X}");

                            // When DA is enabled but values are NOT adjusted, the bindables contain 0.
                            // We treat 0 (or values < epsilon) as "use original" by keeping the setting as null.
                            // A null value becomes -1 when passed to rosu-pp, meaning "don't override".


                            // DA logic refined from tosu: Mod -> BindableNumber -> .Current -> .Value
                            if (approachRateBindable != IntPtr.Zero)
                            {
                                IntPtr currentBindable = _scanner.ReadIntPtr(IntPtr.Add(approachRateBindable, 0x60));
                                float val = _scanner.ReadFloat(IntPtr.Add(currentBindable != IntPtr.Zero ? currentBindable : approachRateBindable, 0x40));
                                if (val != 0) _currentModSettings.AR = (double)val;
                                else _currentModSettings.AR = null;
                            }
                            if (circleSizeBindable != IntPtr.Zero)
                            {
                                IntPtr currentBindable = _scanner.ReadIntPtr(IntPtr.Add(circleSizeBindable, 0x60));
                                float val = _scanner.ReadFloat(IntPtr.Add(currentBindable != IntPtr.Zero ? currentBindable : circleSizeBindable, 0x40));
                                if (val != 0) _currentModSettings.CS = (double)val;
                                else _currentModSettings.CS = null;
                            }
                            if (overallDifficultyBindable != IntPtr.Zero)
                            {
                                IntPtr currentBindable = _scanner.ReadIntPtr(IntPtr.Add(overallDifficultyBindable, 0x60));
                                float val = _scanner.ReadFloat(IntPtr.Add(currentBindable != IntPtr.Zero ? currentBindable : overallDifficultyBindable, 0x40));
                                if (val != 0) _currentModSettings.OD = (double)val;
                                else _currentModSettings.OD = null;
                            }
                            if (drainRateBindable != IntPtr.Zero)
                            {
                                IntPtr currentBindable = _scanner.ReadIntPtr(IntPtr.Add(drainRateBindable, 0x60));
                                float val = _scanner.ReadFloat(IntPtr.Add(currentBindable != IntPtr.Zero ? currentBindable : drainRateBindable, 0x40));
                                if (val > 0) 
                                {
                                    if (val <= 10.1f) _currentModSettings.HP = (double)val;
                                    else _currentModSettings.HP = 10.0;
                                }
                                else _currentModSettings.HP = null;
                            }

                            WriteLog($"DA Attributes Applied: AR={_currentModSettings.AR}, CS={_currentModSettings.CS}, OD={_currentModSettings.OD}, HP={_currentModSettings.HP}");
                        }
                        catch (Exception ex) { WriteLog($"DA Error: {ex.Message}"); }
                    }
                }
                else
                {
                    WriteLog($"ReadModsFromMemory: ModPtr={modPtr:X} VTable={vtable:X} NOT IN MAP (Sig1={sig1}, Sig2={sig2})");
                    // Fallback
                    try
                    {
                        IntPtr acronymPtr = _scanner.ReadIntPtr(IntPtr.Add(modPtr, 0x28));
                        string acr = _scanner.ReadString(acronymPtr);
                        if (!string.IsNullOrEmpty(acr))
                        {
                            mods.Add(acr);
                            WriteLog($"ReadModsFromMemory: Fallback acronym={acr}");
                        }
                    }
                    catch { }
                }
            }
        }

        private int GetObjectCountFromMaxStatistics(IntPtr dictAddr)
        {
            if (dictAddr == IntPtr.Zero) return 0;

            int count = _scanner.ReadInt32(IntPtr.Add(dictAddr, 0x38)); // _count
            IntPtr entriesPtr = _scanner.ReadIntPtr(IntPtr.Add(dictAddr, 0x10)); // _entries (Array)

            if (entriesPtr == IntPtr.Zero) return 0;

            int total = 0;
            for (int i = 0; i < count; i++)
            {
                IntPtr entryAddr = IntPtr.Add(entriesPtr, 0x10 + (i * 0x10));
                int key = _scanner.ReadInt32(IntPtr.Add(entryAddr, 0x8));
                int value = _scanner.ReadInt32(IntPtr.Add(entryAddr, 0xC));

                if (key == 0) continue;

                LazerHitResults result = (LazerHitResults)key;
                if (IsBasicHitResult(result))
                {
                    total += value;
                }
            }
            return total;
        }

        private bool IsBasicHitResult(LazerHitResults result)
        {
            // tosu logic: isScorable && !isTick && !isBonus
            bool isScorable = result == LazerHitResults.LegacyComboIncrease ||
                              result == LazerHitResults.ComboBreak ||
                              result == LazerHitResults.SliderTailHit ||
                              (result >= LazerHitResults.Miss && result < LazerHitResults.IgnoreMiss);

            bool isTick = result == LazerHitResults.LargeTickHit ||
                          result == LazerHitResults.LargeTickMiss ||
                          result == LazerHitResults.SmallTickHit ||
                          result == LazerHitResults.SmallTickMiss ||
                          result == LazerHitResults.SliderTailHit;

            bool isBonus = result == LazerHitResults.SmallBonus ||
                           result == LazerHitResults.LargeBonus;

            return isScorable && !isTick && !isBonus;
        }

        private List<string> ReadMods(IntPtr scoreInfoPtr)
        {
            var mods = new List<string>();
            try
            {
                IntPtr modsJsonPtr = _scanner.ReadIntPtr(IntPtr.Add(scoreInfoPtr, Offsets.ScoreInfo.ModsJson));
                string json = _scanner.ReadString(modsJsonPtr);

                if (!string.IsNullOrEmpty(json))
                {
                    // Lazer mods json: [{"acronym":"HD"},{"acronym":"HR"}] or with settings
                    var jArray = Newtonsoft.Json.Linq.JArray.Parse(json);
                    foreach (var m in jArray)
                    {
                        string? acr = m["acronym"]?.ToString();
                        if (!string.IsNullOrEmpty(acr))
                        {
                            mods.Add(acr);

                            // Extract settings for PP calculation
                            var settings = m["settings"];
                            if (settings != null)
                            {
                                if (acr == "DT" || acr == "HT" || acr == "NC")
                                {
                                    double? speed = (double?)settings["speed_change"];
                                    if (speed.HasValue) _currentModSettings.SpeedChange = speed;
                                }
                                else if (acr == "DA")
                                {
                                    _currentModSettings.AR = (double?)settings["approach_rate"];
                                    _currentModSettings.CS = (double?)settings["circle_size"];
                                    _currentModSettings.OD = (double?)settings["overall_difficulty"];
                                    _currentModSettings.HP = (double?)settings["drain_rate"];
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            if (mods.Count == 0) mods.Add("NM");
            return mods;
        }


        private uint GetModsBits(List<string>? mods)
        {
            if (mods == null) return 0;
            uint bits = 0;
            foreach (var m in mods)
            {
                switch (m.ToUpper())
                {
                    case "NF": bits |= 1; break;
                    case "EZ": bits |= 2; break;
                    case "TD": bits |= 4; break;
                    case "HD": bits |= 8; break;
                    case "HR": bits |= 16; break;
                    case "SD": bits |= 32; break;
                    case "DT": bits |= 64; break;
                    case "RX": bits |= 128; break;
                    case "HT": bits |= 256; break;
                    case "DC": bits |= 256; break;
                    case "NC": bits |= 512 | 64; break;
                    case "FL": bits |= 1024; break;
                    case "AT": break; // Ignore Autoplay for PP calculation
                    case "SO": bits |= 4096; break;
                    case "AP": bits |= 8192; break;
                    case "PF": bits |= 16384 | 32; break; // PF implies SD
                    case "CL": bits |= (1 << 24); break;
                }
            }
            return bits;
        }

        private List<string> _cachedMods = new();
        private IntPtr _lastModsValuePtr = IntPtr.Zero;
        private IntPtr _lastBeatmapInfoPtr = IntPtr.Zero;


        private RawBeatmapInfo? ReadRawBeatmapInfoCached()
        {
            try
            {
                // Read Beatmap Bindable -> Value (WorkingBeatmap)
                var beatmapBindable = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.Beatmap));
                if (beatmapBindable == IntPtr.Zero) return null;

                var workingBeatmapPtr = _scanner.ReadIntPtr(IntPtr.Add(beatmapBindable, 0x20));
                if (workingBeatmapPtr == IntPtr.Zero) { WriteLog("ReadRawBeatmapInfoCached: WorkingBeatmap is null at 0x20"); return null; }

                // Get BeatmapInfo from WorkingBeatmap
                // The original code likely reads BeatmapInfo from WorkingBeatmap.
                // Let's assume WorkingBeatmap IS what ReadRawBeatmapInfo expects?
                // Wait, Offsets.BeatmapManagerWorkingBeatmap.BeatmapInfo = 8
                // So we need to dereference WorkingBeatmap + 8 to get BeatmapInfoPtr.

                var beatmapInfoPtr = _scanner.ReadIntPtr(IntPtr.Add(workingBeatmapPtr, Offsets.BeatmapManagerWorkingBeatmap.BeatmapInfo));

                if (beatmapInfoPtr == _lastBeatmapInfoPtr && _cachedRawBeatmapInfo != null)
                {
                    return _cachedRawBeatmapInfo;
                }

                _lastBeatmapInfoPtr = beatmapInfoPtr;
                _cachedRawBeatmapInfo = ReadRawBeatmapInfo();
                return _cachedRawBeatmapInfo;

            }
            catch
            {
                return null;
            }
        }

        private double GetClockRate()
        {
            // 1. Explicit Speed Change (e.g. Custom DT) - Highest Priority
            // This is populated by ReadModsFromMemory (for Song Select/Results)
            if (_currentModSettings.SpeedChange.HasValue)
            {
                return _currentModSettings.SpeedChange.Value;
            }

            // 2. Memory Clock Rate (Accuracy during Gameplay)
            try
            {
                IntPtr beatmapClockPtr = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.beatmapClock));
                if (beatmapClockPtr != IntPtr.Zero)
                {
                    double rate = _scanner.ReadDouble(IntPtr.Add(beatmapClockPtr, Offsets.FramedBeatmapClock.rate));
                    // If rate is active (not 1.0), trust it. Result screen usually resets to 1.0.
                    if (rate > 0.05 && rate < 5.0 && Math.Abs(rate - 1.0) > 0.001)
                    {
                        return rate;
                    }
                }
            }
            catch { }

            // 3. Fallback: Mods Acronyms (e.g. Standard DT/NC/HT on Result Screen where clock is 1.0)
            if (_currentModsList != null)
            {
                if (_currentModsList.Contains("DT") || _currentModsList.Contains("NC")) return 1.5;
                if (_currentModsList.Contains("HT") || _currentModsList.Contains("DC")) return 0.75;
            }

            return 1.0;
        }

        private double GetCurrentTime()
        {
            try
            {
                IntPtr beatmapClockPtr = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.beatmapClock));
                if (beatmapClockPtr != IntPtr.Zero)
                {
                    IntPtr finalClockSource = _scanner.ReadIntPtr(IntPtr.Add(beatmapClockPtr, Offsets.FramedBeatmapClock.finalClockSource));
                    if (finalClockSource != IntPtr.Zero)
                    {
                        return _scanner.ReadDouble(IntPtr.Add(finalClockSource, Offsets.FramedClock.CurrentTime));
                    }
                }
            }
            catch { }
            return 0;
        }

        // Enum from tosu
        public enum LazerHitResults
        {
            None = 0,
            Miss = 1,
            Meh = 2,
            Ok = 3,
            Good = 4,
            Great = 5,
            Perfect = 6,
            SmallTickMiss = 7,
            SmallTickHit = 8,
            LargeTickMiss = 9,
            LargeTickHit = 10,
            SmallBonus = 11,
            LargeBonus = 12,
            IgnoreMiss = 13,
            IgnoreHit = 14,
            ComboBreak = 15,
            SliderTailHit = 16,
            LegacyComboIncrease = 17,
            LegacyHitAdornment = 18
        }

        private class RawBeatmapInfo
        {
            public float CS;
            public float AR;
            public float OD;
            public float HP;
            public double StarRating;
            public double BPM;
            public double Length;
            public string MD5Hash = "";
            public string FileHash = ""; // SHA256 hash of the .osu file
            public IntPtr BeatmapSetInfoPtr;
        }

        private RawBeatmapInfo? ReadRawBeatmapInfo()
        {
            try
            {
                IntPtr beatmapBindable = _scanner.ReadIntPtr(IntPtr.Add(_gameBaseAddress, Offsets.OsuGameBase.Beatmap));
                if (beatmapBindable == IntPtr.Zero) { WriteLog("ReadRawBeatmapInfo: BeatmapBindable is null"); return null; }

                IntPtr workingBeatmap = _scanner.ReadIntPtr(IntPtr.Add(beatmapBindable, 0x20));
                // Lazer Value for WorkingBeatmap bindable is at 0x20, not 0x10.
                if (workingBeatmap == IntPtr.Zero) { WriteLog("ReadRawBeatmapInfo: WorkingBeatmap is null at 0x20"); return null; }

                IntPtr beatmapInfo = _scanner.ReadIntPtr(IntPtr.Add(workingBeatmap, Offsets.BeatmapManagerWorkingBeatmap.BeatmapInfo));
                if (beatmapInfo == IntPtr.Zero) { WriteLog($"ReadRawBeatmapInfo: BeatmapInfo is null. WorkingBeatmap={workingBeatmap:X}"); return null; }

                WriteLog($"ReadRawBeatmapInfo: Pointers - WorkingBeatmap={workingBeatmap:X}, BeatmapInfo={beatmapInfo:X}");

                var info = new RawBeatmapInfo();
                info.BeatmapSetInfoPtr = _scanner.ReadIntPtr(IntPtr.Add(workingBeatmap, Offsets.BeatmapManagerWorkingBeatmap.BeatmapSetInfo));

                info.MD5Hash = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(beatmapInfo, Offsets.BeatmapInfo.MD5Hash)));
                info.FileHash = _scanner.ReadString(_scanner.ReadIntPtr(IntPtr.Add(beatmapInfo, Offsets.BeatmapInfo.Hash)));
                WriteLog($"ReadRawBeatmapInfo: Hashes - MD5='{info.MD5Hash}', FileHash='{info.FileHash}', SetInfoPtr={info.BeatmapSetInfoPtr:X}");

                info.Length = _scanner.ReadDouble(IntPtr.Add(beatmapInfo, Offsets.BeatmapInfo.Length));
                info.BPM = 0;

                // Difficulty
                IntPtr difficulty = _scanner.ReadIntPtr(IntPtr.Add(beatmapInfo, Offsets.BeatmapInfo.Difficulty));
                WriteLog($"ReadRawBeatmapInfo: Difficulty Pointer={difficulty:X} (offset {Offsets.BeatmapInfo.Difficulty})");
                
                if (difficulty != IntPtr.Zero)
                {
                    info.CS = _scanner.ReadFloat(IntPtr.Add(difficulty, Offsets.BeatmapDifficulty.CircleSize));
                    info.AR = _scanner.ReadFloat(IntPtr.Add(difficulty, Offsets.BeatmapDifficulty.ApproachRate));
                    info.OD = _scanner.ReadFloat(IntPtr.Add(difficulty, Offsets.BeatmapDifficulty.OverallDifficulty));
                    info.HP = _scanner.ReadFloat(IntPtr.Add(difficulty, Offsets.BeatmapDifficulty.DrainRate));
                    WriteLog($"ReadRawBeatmapInfo: Raw Stats - CS:{info.CS} AR:{info.AR} OD:{info.OD} HP:{info.HP}");
                }

                // StarRating
                info.StarRating = _scanner.ReadDouble(IntPtr.Add(beatmapInfo, Offsets.BeatmapInfo.StatusInt + 8));

                return info;
            }
            catch (Exception ex)
            {
                WriteLog($"ReadRawBeatmapInfo Error: {ex.Message}");
                return null;
            }
        }

        // PP+ FEATURE REMOVED - UpdatePlusAttributes method disabled
        // private void UpdatePlusAttributes(LiveSnapshot snap) { ... }

        private string? ResolveOsuFileByHash(IntPtr beatmapSetInfoPtr, string targetFileHash)
        {
            if (beatmapSetInfoPtr == IntPtr.Zero || string.IsNullOrEmpty(targetFileHash)) return null;

            try
            {
                IntPtr filesList = _scanner.ReadIntPtr(IntPtr.Add(beatmapSetInfoPtr, Offsets.BeatmapSetInfo.Files));
                if (filesList == IntPtr.Zero) { WriteLog("ResolveOsuFileByHash: FilesList is null"); return null; }

                IntPtr itemsArray = _scanner.ReadIntPtr(IntPtr.Add(filesList, 0x8));
                int count = _scanner.ReadInt32(IntPtr.Add(filesList, 0x10));

                if (itemsArray == IntPtr.Zero || count <= 0 || count > 500) { WriteLog($"ResolveOsuFileByHash: Invalid list items={itemsArray:X} count={count}"); return null; }

                // Find the specific .osu file by matching the SHA256 hash
                for (int i = 0; i < count; i++)
                {
                    IntPtr itemPtr = _scanner.ReadIntPtr(IntPtr.Add(itemsArray, 0x10 + (i * 0x8)));
                    if (itemPtr == IntPtr.Zero) continue;

                    // Get file hash
                    IntPtr realmFilePtr = _scanner.ReadIntPtr(IntPtr.Add(itemPtr, Offsets.RealmNamedFileUsage.File));
                    if (realmFilePtr == IntPtr.Zero) continue;

                    IntPtr hashPtr = _scanner.ReadIntPtr(IntPtr.Add(realmFilePtr, Offsets.RealmFile.Hash));
                    string fileHash = _scanner.ReadString(hashPtr);

                    if (string.Equals(fileHash, targetFileHash, StringComparison.OrdinalIgnoreCase))
                    {
                        // Found the exact file! Build the path.
                        string filesPath = GetLazerFilesPath();
                        string path = Path.Combine(filesPath, fileHash.Substring(0, 1), fileHash.Substring(0, 2), fileHash);
                        WriteLog($"ResolveOsuFileByHash: Matched file hash -> {path}");
                        return path;
                    }
                }
                WriteLog($"ResolveOsuFileByHash: Target hash {targetFileHash} not found in {count} files");
            }
            catch (Exception ex)
            {
                WriteLog($"ResolveOsuFileByHash Error: {ex.Message}");
            }
            return null;
        }

        public string? TryGetBeatmapPath(string md5)
        {
            // 1. Check if it matches currently monitored beatmap
            if (!string.IsNullOrEmpty(md5) && string.Equals(md5, _currentBeatmapHash, StringComparison.OrdinalIgnoreCase))
            {
                return _currentOsuFilePath;
            }

            // 2. Try to find in Lazer 'files' directory
            if (string.IsNullOrEmpty(md5)) return null;

            try 
            {
                string localFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu", "files");
                string roamingFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu", "files");
                
                var candidates = new[]
                {
                    localFiles,
                    roamingFiles,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!", "files"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu!", "files"),
                    @"G:\osu-lazer-data\files"
                };

                foreach(var root in candidates)
                {
                    if (Directory.Exists(root))
                    {
                        var p = Path.Combine(root, md5.Substring(0, 1), md5.Substring(0, 2), md5);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { /* Ignore IO errors */ }

            return null;
        }

    }
}
