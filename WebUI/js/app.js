/**
 * OSU!GRIND Main Application - v1.3.0
 * Handles tab navigation, window controls, and app initialization
 */

class OsuGrindApp {
    constructor() {
        this.currentTab = 'live';
        this.previousTab = 'live';
        this.lastNonSettingsTab = 'live';
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
            if (msg === undefined && level === undefined) return; // refresh signal check
            this.logToDebug(msg, level);
            const prefix = `[C#][${level}]`;
            if (level === 'ERROR' || level === 'EXCEPTION') console.error(prefix, msg);
            else if (level === 'WARN') console.warn(prefix, msg);
            else console.log(prefix, msg);
        });

        window.api.onLiveData((data) => {
            if (data.type === 'refresh') {
                console.log('[App] Refresh signal received');
                if (window.historyModule) window.historyModule.resetToCalendar();
                if (window.analyticsModule) window.analyticsModule.refresh(true);
                if (window.goalsModule) window.goalsModule.refresh();
                return;
            }
            window.liveModule?.updateUI(data);
        });

        // Initialize each module
        if (window.liveModule) window.liveModule.init();
        if (window.analyticsModule) window.analyticsModule.init();
        if (window.historyModule) window.historyModule.init();
        if (window.settingsModule) window.settingsModule.init();
        if (window.profileModule) window.profileModule.init();
        if (window.goalsModule) window.goalsModule.init();

        // Initialize context menu
        this.ctxMenu = document.getElementById('contextMenu');
        this.currentCtxId = null;
        document.addEventListener('click', () => this.hideCtxMenu());
        document.getElementById('ctxDeletePlay')?.addEventListener('click', () => this.handleCtxDelete());

        // Listen for folder selection from native side
        window.chrome?.webview?.addEventListener('message', (event) => {
            if (event.data.type === 'folderSelected') {
                this.handleFolderSelected(event.data.path, event.data.context);
            }
        });

        // Initial fetch for top-bar stats
        if (window.analyticsModule) window.analyticsModule.refresh(true);

        window.addEventListener('message', (event) => {
            if (event.data.type === 'hudUpdate') {
                try {
                    const data = event.data.data;
                    if (!data) return;

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
                        
                        let bestIdx = 0;
                        for (let i = timeline.length - 1; i >= 0; i--) {
                            const entry = timeline[i];
                            const entryHits = entry[3] + entry[4] + entry[5] + entry[6];
                            const entryCombo = entry[8] || 0;

                            if (entryHits <= targetHits && entryCombo <= rewindCombo) {
                                bestIdx = i;
                                break;
                            }
                        }

                        if (targetHits === 0 && playbackTime >= 0 && bestIdx === 0) {
                            const lastEntry = timeline[timeline.length - 1];
                            const totalDurationMs = lastEntry[7] || 0;
                            let targetMs = playbackTime;
                            
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
                            
                            if (window.analysisSlots.pp) window.analysisSlots.pp.setValue(entry[0]);
                            
                            if (window.analysisSlots.combo) {
                                window.analysisSlots.combo.setValue(hasCurrentCombo ? entry[8] : entry[1]);
                            }

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
                                beatmapPath: ctx.beatmapPath, beatmapHash: ctx.beatmapHash, mods: ctx.mods,
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

        this.checkForUpdates();

        console.log('[App] OsuGrind v1.0.0 initialized');
    }

    async checkForUpdates() {
        try {
            const res = await window.api.checkForUpdates();
            if (res && res.available) {
                this.showUpdateNotification(res.latestVersion, res.zipUrl);
            }
        } catch (e) {
            console.error('[App] Update check failed:', e);
        }
    }

    showUpdateNotification(version, zipUrl) {
        const div = document.createElement('div');
        div.className = 'update-notification';
        div.innerHTML = `
            <div class="update-content">
                <span class="update-icon">🚀</span>
                <div class="update-text">
                    <div class="update-title">Update Available</div>
                    <div class="update-desc">OsuGrind ${version} is ready!</div>
                </div>
            </div>
            <div class="update-actions">
                <button class="update-btn" id="installUpdateBtn">Install Now</button>
                <button class="update-close" onclick="this.parentElement.parentElement.remove()">✕</button>
            </div>
        `;
        document.body.appendChild(div);

        document.getElementById('installUpdateBtn')?.addEventListener('click', async () => {
            const btn = document.getElementById('installUpdateBtn');
            btn.textContent = 'Downloading...';
            btn.disabled = true;
            try {
                await window.api.installUpdate(zipUrl);
            } catch (e) {
                alert('Installation failed. Try manual download.');
                console.error(e);
                btn.textContent = 'Install Now';
                btn.disabled = false;
            }
        });
    }

    setupTabs() {
        document.querySelectorAll('.tab').forEach(tab => {
            tab.addEventListener('click', () => this.switchTab(tab.dataset.tab));
        });
    }

    switchTab(tabName) {
        if (this.currentTab === tabName) return;
        console.log('[App] Switching to tab:', tabName);

        const tabs = ['live', 'analytics', 'history', 'settings'];
        const currentIndex = tabs.indexOf(this.currentTab);
        const newIndex = tabs.indexOf(tabName);
        const direction = newIndex > currentIndex ? 'right' : 'left';

        document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.dataset.tab === tabName));
        
        const settingsBtn = document.getElementById('settingsBtn');
        if (settingsBtn) settingsBtn.classList.toggle('active', tabName === 'settings');

        document.querySelectorAll('.tab-content').forEach(c => {
            c.classList.remove('active', 'from-left');
        });

        const newContent = document.getElementById(`tab-${tabName}`);
        if (newContent) {
            newContent.style.animation = 'none';
            newContent.offsetHeight; 
            newContent.style.animation = null;

            if (direction === 'left') newContent.classList.add('from-left');
            newContent.classList.add('active');
        }
        
        this.previousTab = this.currentTab;
        if (tabName !== 'settings') this.lastNonSettingsTab = tabName;
        this.currentTab = tabName;
        
        if (tabName === 'analytics' && window.analyticsModule) window.analyticsModule.refresh();
        if (tabName === 'history' && window.historyModule) window.historyModule.refresh();
        if (tabName === 'settings' && window.settingsModule) window.settingsModule.loadSettings();
    }

    setupWindowControls() {
        document.getElementById('settingsBtn')?.addEventListener('click', () => {
            if (this.currentTab === 'settings') {
                // If already in settings, go back to previous non-settings tab
                this.switchTab(this.lastNonSettingsTab || 'live');
            } else {
                // Save current as last non-settings before switching
                this.lastNonSettingsTab = this.currentTab;
                this.switchTab('settings');
            }
        });
        document.getElementById('minBtn')?.addEventListener('click', () => {
            if (window.chrome?.webview) window.chrome.webview.postMessage({ action: 'minimize' });
        });
        document.getElementById('minBtn')?.addEventListener('contextmenu', (e) => {
            e.preventDefault();
            if (window.chrome?.webview) window.chrome.webview.postMessage({ action: 'hideToTray' });
        });
        document.getElementById('maxBtn')?.addEventListener('click', () => {
            if (window.chrome?.webview) window.chrome.webview.postMessage({ action: 'maximize' });
        });
        document.getElementById('closeBtn')?.addEventListener('click', () => {
            if (window.chrome?.webview) window.chrome.webview.postMessage({ action: 'close' });
        });
        
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
        const sessionEl = document.getElementById('sessionTimer');
        if (sessionEl) sessionEl.textContent = this.formatTime(sessionElapsed);

        if (this.isPlaying) {
            this.playingTime++;
        }
        
        const playingEl = document.getElementById('playingTimer');
        if (playingEl) playingEl.textContent = this.formatTime(this.playingTime);
    }

    formatTime(seconds) {
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        const s = seconds % 60;
        return `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
    }

    setPlaying(isPlaying) { this.isPlaying = isPlaying; }
    
    updatePlaysToday(count) {
        const el = document.getElementById('playsToday');
        if (el) el.textContent = `${count} play${count !== 1 ? 's' : ''}`;
    }
    
    updateStreak(days) {
        const pill = document.getElementById('streakPill'), count = document.getElementById('streakCount');
        if (pill) pill.style.display = 'flex';
        if (count) count.textContent = days || 0;
    }

    showCtxMenu(x, y, id) {
        if (!this.ctxMenu) return;
        this.currentCtxId = id;
        this.ctxMenu.style.display = 'block';
        this.ctxMenu.style.left = x + 'px';
        this.ctxMenu.style.top = y + 'px';
    }

    hideCtxMenu() {
        if (this.ctxMenu) this.ctxMenu.style.display = 'none';
    }

    async handleFolderSelected(path, context) {
        if (!path) return;
        console.log(`[App] Folder selected for ${context}: ${path}`);
        
        try {
            const settings = await window.api.getSettings();
            if (context === 'osu!lazer') {
                settings.lazerPath = path;
                await window.api.saveSettings(settings);
                if (window.settingsModule) await window.settingsModule.importLazer();
            } else if (context === 'osu!stable') {
                settings.stablePath = path;
                await window.api.saveSettings(settings);
                if (window.settingsModule) await window.settingsModule.importStable();
            }
        } catch (e) {
            console.error('[App] Failed to handle folder selection:', e);
        }
    }

    async handleCtxDelete() {
        if (!this.currentCtxId) return;
        if (confirm('Delete this score?')) {
            try {
                await window.api.deletePlay(this.currentCtxId);
                console.log('[App] Deleted play:', this.currentCtxId);
                // Re-refresh history if on that tab
                if (this.currentTab === 'history' && window.historyModule) {
                    if (window.historyModule.selectedDate) {
                        await window.historyModule.showDayDetail(window.historyModule.selectedDate);
                    } else {
                        await window.historyModule.refresh();
                    }
                }
                // Also refresh recent in live tab
                if (window.liveModule) window.liveModule.fetchRecent();
                // Refresh analytics
                if (window.analyticsModule) window.analyticsModule.refresh(true);
            } catch (e) {
                console.error('[App] Delete failed:', e);
            }
        }
    }

    initDebugConsole() {
        this.debugConsole = document.getElementById('debugConsole');
        this.debugOutput = document.getElementById('debugOutput');
        if (document.getElementById('clearDebug')) document.getElementById('clearDebug').onclick = () => { this.debugOutput.innerHTML = ''; };
        if (document.getElementById('closeDebug')) document.getElementById('closeDebug').onclick = () => { this.debugConsole.style.display = 'none'; };
        window.addEventListener('keydown', (e) => {
            if (e.key.toLowerCase() === 'd' && (e.ctrlKey || e.shiftKey)) {
                if (this.debugConsole) {
                    const isHidden = this.debugConsole.style.display === 'none';
                    this.debugConsole.style.display = isHidden ? 'flex' : 'none';
                }
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
