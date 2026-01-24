/**
 * OSU!GRIND Main Application - v1.3.0
 * Handles tab navigation, window controls, and app initialization
 */

class OsuGrindApp {
    constructor() {
        this.currentTab = 'live';
        this.sessionStart = Date.now();
        this.playingTime = 0;
        this.isPlaying = false;
    }

    init() {
        this.setupTabs();
        this.setupWindowControls();
        this.setupProfileButton();
        this.startTimers();
        this.initDebugConsole();

        // Connect to live WebSocket
        window.api.connectLive();

        window.api.onLog((msg, level) => {
            this.logToDebug(msg, level);
            const prefix = `[C#][${level}]`;
            if (level === 'ERROR' || level === 'EXCEPTION') console.error(prefix, msg);
            else if (level === 'WARN') console.warn(prefix, msg);
            else console.log(prefix, msg);
        });

        // Initialize each module
        if (window.liveModule) window.liveModule.init();
        if (window.analyticsModule) window.analyticsModule.init();
        if (window.historyModule) window.historyModule.init();
        if (window.settingsModule) window.settingsModule.init();
        if (window.profileModule) window.profileModule.init();

        // Listen for HUD updates from Rewind iframe
        window.addEventListener('message', (event) => {
            if (event.data.type === 'hudUpdate') {
                try {
                    const data = event.data.data;
                    if (!data) return;

                    // Diagnostic globals
                    window._lastRewindData = data;
                    
                    const ctx = window.historyModule ? window.historyModule.currentReplayContext : null;
                    const isAnalysis = ctx && ctx.scoreId > 0;
                    
                    if (window.historyModule && window.historyModule.heatmapRenderer && data.spatialHits) {
                        window.historyModule.heatmapRenderer.addHits(data.spatialHits, data.cs);
                    }

                    // 1. Data Extraction (Multi-key support)
                    const getNum = (keys, def = 0) => {
                        for (const k of keys) {
                            if (data[k] !== undefined && data[k] !== null) {
                                const val = parseFloat(data[k]);
                                if (!isNaN(val)) return val;
                            }
                        }
                        return def;
                    };

                    const h300 = getNum(['300', 'count300', 'n300', 'great']);
                    const h100 = getNum(['100', 'count100', 'n100', 'ok']);
                    const h50 = getNum(['50', 'count50', 'n50', 'meh']);
                    const miss = getNum(['miss', 'countMiss', 'nMiss', 'misses']);
                    const rewindCombo = getNum(['combo', 'currentCombo']);
                    const rewindAcc = getNum(['acc', 'accuracy']) / (getNum(['acc', 'accuracy']) > 1 ? 100 : 1);
                    const ur = getNum(['ur', 'unstableRate']);
                    
                    const playbackTime = getNum(['time', 'currentTime', 'playbackTime']);
                    const targetHits = h300 + h100 + h50 + miss;

                    let lookupSucceeded = false;
                    
                    if (isAnalysis && ctx.statsTimeline && ctx.statsTimeline.length > 0) {
                        const timeline = ctx.statsTimeline;
                        
                        // 2. STATE-MATCH SYNC (Object-to-Object)
                        // Search backwards for the latest entry that doesn't exceed Rewind's hits AND combo
                        let bestIdx = 0;
                        for (let i = timeline.length - 1; i >= 0; i--) {
                            const entry = timeline[i];
                            const entryHits = entry[3] + entry[4] + entry[5] + entry[6];
                            const entryCombo = entry[8] || 0;

                            // Sync Logic:
                            // We want the entry that is at or before Rewind's state in both Hits and Combo.
                            // This prevents jumping to a slider end/tick if the playback hasn't reached it.
                            if (entryHits <= targetHits && entryCombo <= rewindCombo) {
                                bestIdx = i;
                                break;
                            }
                        }

                        // Intro Support: If no hits yet, find position by time to show metadata
                        if (targetHits === 0 && playbackTime >= 0 && bestIdx === 0) {
                            const lastEntry = timeline[timeline.length - 1];
                            const totalDurationMs = lastEntry[7] || 0;
                            let targetMs = playbackTime;
                            
                            // Heuristic to detect if time is in seconds or ms
                            if (targetMs > 0 && targetMs < totalDurationMs / 10) targetMs *= 1000;

                            let low = 0, high = timeline.length - 1;
                            while (low <= high) {
                                let mid = Math.floor((low + high) / 2);
                                if (timeline[mid][7] <= targetMs) {
                                    bestIdx = mid;
                                    low = mid + 1;
                                } else {
                                    high = mid - 1;
                                }
                            }
                        }

                        const entry = timeline[bestIdx];
                        if (entry && window.analysisSlots) {
                            lookupSucceeded = true;
                            const hasCurrentCombo = entry.length >= 9;
                            
                            // PP
                            if (window.analysisSlots.pp) window.analysisSlots.pp.setValue(entry[0]);
                            
                            // COMBO: Truth from live recording
                            if (window.analysisSlots.combo) {
                                window.analysisSlots.combo.setValue(hasCurrentCombo ? entry[8] : entry[1]);
                            }

                            // Accuracy & Hits Override
                            if (window.analysisSlots.acc) window.analysisSlots.acc.setValue(entry[2]);
                            if (window.analysisSlots.h300) window.analysisSlots.h300.setValue(entry[3]);
                            if (window.analysisSlots.h100) window.analysisSlots.h100.setValue(entry[4]);
                            if (window.analysisSlots.h50) window.analysisSlots.h50.setValue(entry[5]);
                            if (window.analysisSlots.miss) window.analysisSlots.miss.setValue(entry[6]);
                            if (window.analysisSlots.ur) window.analysisSlots.ur.setValue(ur);

                            if (window.liveModule) {
                                window.liveModule.updateUI({
                                    playState: 'Replay',
                                    accuracy: entry[2],
                                    combo: hasCurrentCombo ? entry[8] : entry[1],
                                    hitCounts: { great: entry[3], ok: entry[4], meh: entry[5], miss: entry[6] }
                                });
                            }
                        }
                    }

                    // 3. Fallback: Use Rewind data directly (Imported replays)
                    if (!lookupSucceeded && window.analysisSlots) {
                        if (window.analysisSlots.acc) window.analysisSlots.acc.setValue(rewindAcc);
                        if (window.analysisSlots.combo) window.analysisSlots.combo.setValue(rewindCombo);
                        if (window.analysisSlots.ur) window.analysisSlots.ur.setValue(ur);
                        if (window.analysisSlots.h300) window.analysisSlots.h300.setValue(h300);
                        if (window.analysisSlots.h100) window.analysisSlots.h100.setValue(h100);
                        if (window.analysisSlots.h50) window.analysisSlots.h50.setValue(h50);
                        if (window.analysisSlots.miss) window.analysisSlots.miss.setValue(miss);
                        
                        if (isAnalysis && (!window._lastPpUpdate || Date.now() - window._lastPpUpdate > 200)) {
                            window._lastPpUpdate = Date.now();
                            window.api.calculateRewindPp({
                                scoreId: ctx.scoreId, beatmapHash: ctx.beatmapHash, mods: ctx.mods,
                                combo: rewindCombo, count300: h300, count100: h100, count50: h50, misses: miss, passedObjects: targetHits
                            }).then(res => {
                                if (res && res.pp !== undefined && window.analysisSlots.pp) window.analysisSlots.pp.setValue(res.pp);
                            }).catch(() => {});
                        }
                    }
                } catch (e) {
                    console.error('[App] Sync Error:', e);
                }
            }
        });

        console.log('[App] OsuGrind v1.2.0 initialized');
    }

    setupTabs() {
        document.querySelectorAll('.tab').forEach(tab => {
            tab.addEventListener('click', () => this.switchTab(tab.dataset.tab));
        });
    }

    switchTab(tabName) {
        document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.dataset.tab === tabName));
        document.querySelectorAll('.tab-content').forEach(content => content.classList.toggle('active', content.id === `tab-${tabName}`));
        this.currentTab = tabName;
        if (tabName === 'analytics' && window.analyticsModule) window.analyticsModule.refresh();
        if (tabName === 'history' && window.historyModule) window.historyModule.refresh();
    }

    setupWindowControls() {
        document.getElementById('minimizeBtn')?.addEventListener('click', () => window.chrome?.webview?.postMessage({ action: 'minimize' }));
        document.getElementById('maximizeBtn')?.addEventListener('click', () => window.chrome?.webview?.postMessage({ action: 'maximize' }));
        document.getElementById('closeBtn')?.addEventListener('click', () => window.chrome?.webview?.postMessage({ action: 'close' }));
        document.getElementById('settingsBtn')?.addEventListener('click', () => this.switchTab('settings'));
        
        const titlebar = document.getElementById('titlebar');
        if (titlebar) {
            titlebar.addEventListener('mousedown', (e) => {
                if (e.target.closest('button')) return;
                window.chrome?.webview?.postMessage({ action: 'startDrag' });
            });
        }
    }

    setupProfileButton() {
        document.getElementById('profileBtn')?.addEventListener('click', () => window.profileModule?.show());
    }

    startTimers() {
        setInterval(() => this.updateTimers(), 1000);
    }

    updateTimers() {
        const sessionElapsed = Math.floor((Date.now() - this.sessionStart) / 1000);
        document.getElementById('sessionTimer').textContent = this.formatTime(sessionElapsed);
        if (this.isPlaying) this.playingTime++;
        document.getElementById('playingTimer').textContent = this.formatTime(this.playingTime);
    }

    formatTime(seconds) {
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        const s = seconds % 60;
        return `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
    }

    setPlaying(isPlaying) { this.isPlaying = isPlaying; }
    updatePlays24h(count) {
        const el = document.getElementById('plays24h');
        if (el) el.textContent = `${count} play${count !== 1 ? 's' : ''}`;
    }
    updateStreak(days) {
        const pill = document.getElementById('streakPill'), count = document.getElementById('streakCount');
        if (days > 0) { pill.style.display = 'flex'; count.textContent = days; } else { pill.style.display = 'none'; }
    }

    initDebugConsole() {
        this.debugConsole = document.getElementById('debugConsole');
        this.debugOutput = document.getElementById('debugOutput');
        if (document.getElementById('clearDebug')) document.getElementById('clearDebug').onclick = () => { this.debugOutput.innerHTML = ''; };
        if (document.getElementById('closeDebug')) document.getElementById('closeDebug').onclick = () => { this.debugConsole.style.display = 'none'; };
        window.addEventListener('keydown', (e) => {
            if (e.key.toLowerCase() === 'd' && (e.ctrlKey || e.shiftKey)) {
                const isHidden = this.debugConsole.style.display === 'none';
                this.debugConsole.style.display = isHidden ? 'flex' : 'none';
            }
        });
    }

    logToDebug(msg, type = 'info') {
        if (!this.debugOutput) return;
        const line = document.createElement('div');
        line.className = `debug-line ${type}`;
        line.textContent = `[${new Date().toLocaleTimeString()}] ${msg}`;
        this.debugOutput.appendChild(line);
        this.debugOutput.scrollTop = this.debugOutput.scrollHeight;
    }
}

document.addEventListener('DOMContentLoaded', () => {
    window.app = new OsuGrindApp();
    window.app.init();
});
