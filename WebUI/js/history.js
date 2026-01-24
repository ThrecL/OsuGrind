/**
 * OSU!GRIND History Tab Module
 * Handles calendar view and play list with Map → Difficulty → Scores hierarchy
 */

class HistoryModule {
    constructor() {
        this.currentYear = new Date().getFullYear();
        this.currentMonth = new Date().getMonth();
        this.monthPlayCounts = {};
        this.selectedDate = null;
    }

    init() {
        this.setupNavigation();
        window.api.onLog((msg, level) => this.handleLog(msg, level));
        console.log('[History] Module initialized');
    }

    handleLog(msg, level) {
        if (!msg.includes('[RealmExport]')) return;
        const rewindContainer = document.getElementById('rewindPlayer');
        if (!rewindContainer) return;
        if (level === 'error') {
            rewindContainer.innerHTML = '<div class="heatmap-placeholder">Failed to export replay data</div>';
        }
    }

    setupNavigation() {
        document.getElementById('prevMonth')?.addEventListener('click', () => this.changeMonth(-1));
        document.getElementById('nextMonth')?.addEventListener('click', () => this.changeMonth(1));
        document.getElementById('backToCalendar')?.addEventListener('click', () => this.showCalendar());
    }

    async refresh() {
        await this.loadMonthData();
        this.renderCalendar();
    }

    changeMonth(delta) {
        this.currentMonth += delta;
        if (this.currentMonth < 0) {
            this.currentMonth = 11;
            this.currentYear--;
        } else if (this.currentMonth > 11) {
            this.currentMonth = 0;
            this.currentYear++;
        }
        this.refresh();
    }

    async loadMonthData() {
        try {
            const data = await window.api.getMonthPlays(this.currentYear, this.currentMonth + 1);
            this.monthPlayCounts = data.playCounts || {};
        } catch (error) {
            console.error('[History] Failed to load month data:', error);
            this.monthPlayCounts = {};
        }
    }

    renderCalendar() {
        const monthNames = ['January', 'February', 'March', 'April', 'May', 'June',
            'July', 'August', 'September', 'October', 'November', 'December'];

        const labelEl = document.getElementById('monthLabel');
        if (labelEl) {
            labelEl.textContent = `${monthNames[this.currentMonth]} ${this.currentYear}`;
        }

        const grid = document.getElementById('calendarGrid');
        if (!grid) return;

        const firstDay = new Date(this.currentYear, this.currentMonth, 1).getDay();
        const daysInMonth = new Date(this.currentYear, this.currentMonth + 1, 0).getDate();
        const today = new Date();

        let html = '';

        // Empty cells for days before first of month
        for (let i = 0; i < firstDay; i++) {
            html += '<div class="calendar-day empty"></div>';
        }

        // Days of month
        for (let day = 1; day <= daysInMonth; day++) {
            const dateStr = `${this.currentYear}-${String(this.currentMonth + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
            const playCount = this.monthPlayCounts[dateStr] || 0;
            const isToday = today.getFullYear() === this.currentYear &&
                today.getMonth() === this.currentMonth &&
                today.getDate() === day;

            const classes = ['calendar-day'];
            if (playCount > 0) classes.push('has-plays');
            if (isToday) classes.push('today');

            html += `
                <div class="${classes.join(' ')}" data-date="${dateStr}">
                    <span class="day-number">${day}</span>
                    ${playCount > 0 ? `<span class="day-plays">${playCount}</span>` : ''}
                </div>
            `;
        }

        grid.innerHTML = html;

        // Add click handlers
        grid.querySelectorAll('.calendar-day:not(.empty)').forEach(el => {
            el.addEventListener('click', () => {
                const date = el.dataset.date;
                if (this.monthPlayCounts[date] > 0) {
                    this.showDayDetail(date);
                }
            });
        });
    }

    async showDayDetail(dateStr) {
        this.selectedDate = dateStr;

        document.getElementById('calendarView').style.display = 'none';
        document.getElementById('dayView').style.display = 'block';

        const date = new Date(dateStr);
        document.getElementById('dayTitle').textContent = date.toLocaleDateString('en-US', {
            weekday: 'long',
            year: 'numeric',
            month: 'long',
            day: 'numeric'
        });

        try {
            const data = await window.api.getHistoryForDate(dateStr);
            this.renderDayStats(data.stats);
            this.renderPlaysList(data.plays);
        } catch (error) {
            console.error('[History] Failed to load day data:', error);
        }
    }

    showCalendar() {
        document.getElementById('calendarView').style.display = 'block';
        document.getElementById('dayView').style.display = 'none';
        this.selectedDate = null;
    }

    renderDayStats(stats) {
        const container = document.getElementById('dayStats');
        if (!container || !stats) return;

        container.innerHTML = `
            <div class="day-stat"><strong>${stats.plays || 0}</strong> plays</div>
            <div class="day-stat"><strong>${(stats.avgAccuracy || 0).toFixed(1)}%</strong> avg acc</div>
            <div class="day-stat"><strong>${Math.round(stats.avgPP || 0)}</strong> avg pp</div>
            <div class="day-stat"><strong>${stats.duration || '0m'}</strong> played</div>
        `;
    }

    renderPlaysList(plays) {
        const container = document.getElementById('playsList');
        if (!container) return;

        if (!plays || plays.length === 0) {
            container.innerHTML = '<p style="color: var(--text-muted); text-align: center; padding: 40px;">No plays on this day</p>';
            return;
        }

        // Group by Song (title + artist), then by Difficulty
        const songGroups = {};
        plays.forEach(play => {
            const songKey = `${play.artist || 'Unknown'}_${play.title || 'Unknown'}`;
            if (!songGroups[songKey]) {
                songGroups[songKey] = {
                    title: play.title || 'Unknown Title',
                    artist: play.artist || 'Unknown Artist',
                    backgroundPath: play.backgroundPath,
                    cs: play.cs,
                    ar: play.ar,
                    od: play.od,
                    hp: play.hp,
                    bpm: play.bpm,
                    difficulties: {}
                };
            }

            const diffKey = play.difficulty || 'Normal';
            if (!songGroups[songKey].difficulties[diffKey]) {
                songGroups[songKey].difficulties[diffKey] = {
                    name: diffKey,
                    stars: play.stars,
                    scores: []
                };
            }
            songGroups[songKey].difficulties[diffKey].scores.push(play);
        });

        const sortedSongs = Object.values(songGroups).sort((a, b) => {
            const latestA = Math.max(...Object.values(a.difficulties).flatMap(d => d.scores.map(s => new Date(s.createdAtUtc || s.timestamp).getTime())));
            const latestB = Math.max(...Object.values(b.difficulties).flatMap(d => d.scores.map(s => new Date(s.createdAtUtc || s.timestamp).getTime())));
            return latestB - latestA;
        });

        container.innerHTML = sortedSongs.map(song => this.renderMapCard(song)).join('');
        this.setupInteractions(container);
    }

    renderMapCard(song) {
        const difficulties = Object.values(song.difficulties);
        difficulties.sort((a, b) => (b.stars || 0) - (a.stars || 0));
        const bgUrl = song.backgroundPath ? `/api/background/${song.backgroundPath.split(/[/\\]/).pop()}` : '';

        return `
            <div class="map-card">
                <div class="map-header">
                    <div class="map-header-bg" style="background-image: url('${bgUrl}')"></div>
                    <div class="map-header-overlay"></div>
                    <div class="map-info">
                        <div class="map-title">${song.title}</div>
                        <div class="map-artist">${song.artist}</div>
                    </div>
                    ${this.getStarBadgeHTML(difficulties[0]?.stars)}
                    <div class="map-attrs">

                        ${song.cs != null ? `<div class="map-attr"><span class="label">CS</span><span class="value">${song.cs?.toFixed(1) || '-'}</span></div>` : ''}
                        ${song.ar != null ? `<div class="map-attr"><span class="label">AR</span><span class="value">${song.ar?.toFixed(1) || '-'}</span></div>` : ''}
                        ${song.od != null ? `<div class="map-attr"><span class="label">OD</span><span class="value">${song.od?.toFixed(1) || '-'}</span></div>` : ''}
                        ${song.hp != null ? `<div class="map-attr"><span class="label">HP</span><span class="value">${song.hp?.toFixed(1) || '-'}</span></div>` : ''}
                        ${song.bpm ? `<div class="map-attr"><span class="label">BPM</span><span class="value">${Math.round(song.bpm)}</span></div>` : ''}
                    </div>
                </div>
                ${difficulties.map(diff => this.renderDiffSection(diff)).join('')}
            </div>
        `;
    }

    renderDiffSection(diff) {
        diff.scores.sort((a, b) => new Date(b.createdAtUtc || b.timestamp) - new Date(a.createdAtUtc || a.timestamp));
        const bestScore = diff.scores.reduce((best, s) => (s.pp > (best?.pp || 0) ? s : best), diff.scores[0]);
        let bestGrade = bestScore?.rank || '?';
        if (bestGrade === 'X') bestGrade = 'SS';
        if (bestGrade === 'XH') bestGrade = 'SSH';

        const playCount = diff.scores.length;
        const avgAcc = diff.scores.reduce((sum, s) => sum + (s.accuracy || 0), 0) / playCount * 100;
        const diffColorClass = this.getDiffColorClass(diff.stars);

        return `
            <div class="diff-section">
                <div class="diff-header">
                    <div class="diff-color ${diffColorClass}"></div>
                    <div class="diff-name">${diff.name}</div>
                    <div class="diff-stats">
                        ${this.getStarBadgeHTML(diff.stars)}
                        <div class="diff-stat"><strong>${playCount}</strong> plays</div>

                        <div class="diff-stat"><strong>${avgAcc.toFixed(1)}%</strong> avg</div>
                    </div>
                    <div class="diff-best-grade ${this.getGradeClass(bestGrade)}">${bestGrade}</div>
                    <button class="diff-toggle">▼</button>
                </div>
                <div class="diff-scores">
                    ${diff.scores.map(s => this.renderScoreRow(s)).join('')}
                </div>
            </div>
        `;
    }

    renderScoreRow(score) {
        const time = new Date(score.createdAtUtc || score.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        const acc = ((score.accuracy || 0) * 100).toFixed(2);
        const pp = Math.round(score.pp || 0);
        const combo = score.combo || 0;
        const scoreVal = (score.score || 0).toLocaleString();
        let grade = (score.rank || '?').toUpperCase();
        if (grade === 'X') grade = 'SS';
        if (grade === 'XH') grade = 'SSH';

        const scoreJson = JSON.stringify(score).replace(/'/g, '&#39;').replace(/"/g, '&quot;');

        // Mods logic
        const mods = score.mods || 'NM';
        const isNoMod = (mods === 'NM' || mods === 'None' || mods === 'NoMod' || mods === 'No Mod');
        const modList = isNoMod ? [] : (mods.replace('+', '').match(/.{1,2}/g) || []);
        
        let modsHtml = "";
        if (modList.length > 0) {
            modsHtml = modList.map(m => {
                const iconName = this.getModIconName(m);
                return `<img class="row-mod-icon" src="/Assets/Mods/${iconName}.png" alt="${m}" title="${m}" onerror="this.src='/Assets/Mods/mod-no-mod.png'">`;
            }).join('');
        } else {
            modsHtml = `<img class="row-mod-icon" src="/Assets/Mods/mod-no-mod.png" alt="NM" title="No Mod">`;
        }

        return `
            <div class="score-row" data-id="${score.id}" data-score="${scoreJson}">
                <div class="score-time">${time}</div>
                <div class="score-grade ${this.getGradeClass(grade)}">${grade}</div>
                <div class="score-value">${scoreVal}</div>
                <div class="score-combo">${combo}x</div>
                <div class="score-acc">${acc}%</div>
                <div class="score-pp">${pp}pp</div>
                <div class="score-mods">${modsHtml}</div>
                <div class="score-hits">
                    <span class="hit great">${score.count300 || 0}</span>
                    <span class="hit ok">${score.count100 || 0}</span>
                    <span class="hit meh">${score.count50 || 0}</span>
                    <span class="hit miss">${score.misses || 0}</span>
                </div>
            </div>
        `;
    }

    setupInteractions(container) {
        container.querySelectorAll('.diff-header').forEach(header => {
            header.addEventListener('click', (e) => {
                if (e.target.closest('.score-action')) return;
                header.closest('.diff-section').classList.toggle('expanded');
            });
        });

        container.querySelectorAll('.score-row').forEach(row => {
            row.addEventListener('click', (e) => {
                if (e.target.closest('.score-action')) return;
                const scoreData = JSON.parse(row.dataset.score || '{}');
                this.openScoreAnalysis(scoreData);
            });
        });

        container.querySelectorAll('.score-action').forEach(btn => {
            btn.addEventListener('click', async (e) => {
                e.stopPropagation();
                const action = btn.dataset.action;
                const id = btn.closest('.score-row').dataset.id;
                await this.handleScoreAction(action, id);
            });
        });
    }

    openScoreAnalysis(score) {
        const existingModal = document.querySelector('.score-analysis-modal');
        if (existingModal) existingModal.remove();

        const acc = ((score.accuracy || 0) * 100).toFixed(2);
        const pp = Math.round(score.pp || 0);
        const combo = score.combo || 0;
        let grade = score.rank || '?';
        if (grade === 'X') grade = 'SS';
        if (grade === 'XH') grade = 'SSH';

        const mods = score.mods || 'NM';
        const beatmap = score.beatmap || 'Unknown Map';

        const modList = mods.replace('+', '').match(/.{1,2}/g) || [];
        const modsHtml = modList.length ? modList.map(m => {
            const iconName = this.getModIconName(m);
            // High quality HD mod from rewind folder
            const isHD = m.toUpperCase() === 'HD';
            const src = isHD ? 'rewind/mod-hidden.png' : `Assets/mods/${iconName}.png`;
            const style = isHD ? 'filter: grayscale(0%); width: 32px;' : '';
            return `<img class="mod-icon" src="${src}" alt="${m}" title="${m}" style="${style}" onerror="this.src='Assets/mods/mod-no-mod.png'">`;
        }).join('') : '';

        const modal = document.createElement('div');
        modal.className = 'score-analysis-modal';
        
        // Store current replay context for PP calc
        this.currentReplayContext = {
            scoreId: score.id,
            beatmapHash: score.beatmapHash || '',
            mods: modList
        };
        console.log('[History] Set replay context:', this.currentReplayContext);

        modal.innerHTML = `
            <div class="modal-backdrop"></div>
            <div class="modal-content users-design full-player">
                <button class="modal-close">✕</button>
                <div class="analysis-header-new">
                    <!-- Map name removed, only used for dragging now -->
                </div>
                <div class="analysis-grid-new">
                    <div class="rewind-player" id="rewindPlayer">
                        <div class="rewind-status">
                            <div class="heatmap-placeholder">Preparing replay...</div>
                        </div>
                        <iframe class="rewind-frame" src="about:blank" frameborder="0"></iframe>
                        
                        <!-- Overlay HUD elements -->
                        <div class="analysis-overlay-hud">
                            <div class="hud-top-left">
                                <div class="analysis-stat ur-stat" style="margin-bottom: 10px;">
                                    <span class="label">UR</span>
                                    <div class="value side-value" id="analysis-ur">0.00</div>
                                </div>
                                <div class="analysis-stat pp-stat">
                                    <span class="label">PP</span>
                                    <div class="value side-value" id="analysis-pp">0.00</div>
                                </div>
                            </div>
                            <div class="hud-top-right">
                                <div class="analysis-stat acc-stat">
                                    <span class="label">ACC</span>
                                    <div class="value side-value" id="analysis-accuracy">100.00%</div>
                                </div>
                                <div class="meta-mods" style="margin-top: 10px; justify-content: flex-end;">${modsHtml}</div>
                                <div class="heatmap-circle-container"></div>
                            </div>
                            <div class="hud-bottom-left">
                                <div class="analysis-stat combo-stat">
                                    <div class="value side-value" id="analysis-combo">0x</div>
                                </div>
                            </div>
                            <div class="hud-bottom-right">
                                <div class="analysis-hits" id="analysis-hits">
                                    <div class="hit hit-300" id="analysis-300">0</div>
                                    <div class="hit hit-100" id="analysis-100">0</div>
                                    <div class="hit hit-50" id="analysis-50">0</div>
                                    <div class="hit hit-miss" id="analysis-miss">0</div>
                                </div>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        `;

        document.body.appendChild(modal);
        
        // Add manual dragging for full player
        const analysisHeader = modal.querySelector('.analysis-header-new');
        if (analysisHeader) {
            analysisHeader.addEventListener('mousedown', (e) => {
                if (e.target.closest('button')) return;
                if (window.chrome?.webview) {
                    window.chrome.webview.postMessage({ action: 'startDrag' });
                }
            });
        }

        // Initialize odometers for all analysis HUD stats
        if (window.SlotNumber) {
            window.analysisSlots = {
                acc: new window.SlotNumber(document.getElementById('analysis-accuracy'), { isAcc: true }),
                combo: new window.SlotNumber(document.getElementById('analysis-combo'), { suffix: 'x' }),
                pp: new window.SlotNumber(document.getElementById('analysis-pp'), { precision: 0 }),
                ur: new window.SlotNumber(document.getElementById('analysis-ur'), { precision: 2 }),
                h300: new window.SlotNumber(document.getElementById('analysis-300')),
                h100: new window.SlotNumber(document.getElementById('analysis-100')),
                h50: new window.SlotNumber(document.getElementById('analysis-50')),
                miss: new window.SlotNumber(document.getElementById('analysis-miss'))
            };
            
            // Explicit initial reset to avoid carrying over old values
            window.analysisSlots.acc.setValue(1);
            window.analysisSlots.combo.setValue(0);
            window.analysisSlots.pp.setValue(0);
            window.analysisSlots.ur.setValue(0);
            window.analysisSlots.h300.setValue(0);
            window.analysisSlots.h100.setValue(0);
            window.analysisSlots.h50.setValue(0);
            window.analysisSlots.miss.setValue(0);
        }


        this.loadRewindPlayer(score.id, modal);

        modal.querySelector('.modal-backdrop').addEventListener('click', () => {

            if (this.heatmapRenderer) {
                this.saveCursorOffsets(score.id, this.heatmapRenderer.getSnapshot());
                this.heatmapRenderer.stop();
                this.heatmapRenderer = null;
            }
            const rewindFrame = modal.querySelector('.rewind-frame');
            if (rewindFrame) rewindFrame.src = 'about:blank';
            modal.remove();
        });
        modal.querySelector('.modal-close').addEventListener('click', () => {
            if (this.heatmapRenderer) {
                this.saveCursorOffsets(score.id, this.heatmapRenderer.getSnapshot());
                this.heatmapRenderer.stop();
                this.heatmapRenderer = null;
            }
            const rewindFrame = modal.querySelector('.rewind-frame');
            if (rewindFrame) rewindFrame.src = 'about:blank';
            modal.remove();
        });
    }

    async saveCursorOffsets(scoreId, offsets) {
        if (!offsets || offsets.length === 0) return;
        try {
            await window.api.saveCursorOffsets(scoreId, offsets);
            console.log('[History] Saved', offsets.length, 'cursor offsets to DB');
        } catch (e) {
            console.error('[History] Failed to save offsets:', e);
        }
    }


    async loadRewindPlayer(id, modal) {
        try {
            const rewindContainer = document.getElementById('rewindPlayer');
            if (!rewindContainer) return;


            const iframe = rewindContainer.querySelector('.rewind-frame');
            const statusOverlay = rewindContainer.querySelector('.rewind-status');
            if (iframe) {
                iframe.src = 'about:blank';
            }
            if (statusOverlay) {
                statusOverlay.innerHTML = `
                    <div class="rewind-progress">
                        <div class="rewind-progress-bar" style="width: 25%"></div>
                    </div>
                    <div class="heatmap-placeholder">Preparing replay...</div>
                `;
            }


            const updateProgress = (percent, label) => {
                const bar = rewindContainer.querySelector('.rewind-progress-bar');
                const labelEl = rewindContainer.querySelector('.heatmap-placeholder');
                if (bar) bar.style.width = `${percent}%`;
                if (labelEl && label) labelEl.textContent = label;
            };

            updateProgress(30, 'Requesting replay data...');
            const info = await window.api.getRewindInfo(id);

            if (!info || !info.replayPath) {
                const statusLabel = rewindContainer.querySelector('.heatmap-placeholder');
                if (statusLabel) statusLabel.textContent = 'Replay not available';
                return;
            }

            console.debug('[Rewind] info', info);
            if (!info.osuFileName) {
                console.warn('[Rewind] Missing osuFileName for beatmap folder', info.beatmapFolder);
            }
            updateProgress(60, 'Loading beatmap data...');

            if (info.beatmapFolder) {
                const beatmapPath = info.osuFileName
                    ? `${info.beatmapFolder}/${info.osuFileName}`
                    : info.beatmapFolder;
                const resolvedBeatmapPath = beatmapPath.replace(/\\/g, '/');
                const fallbackBeatmapPath = info.beatmapFolder
                    ? `${info.beatmapFolder}/${info.osuFileName || ''}`
                    : resolvedBeatmapPath;
                const checkUrl = `/rewind/file?path=${encodeURIComponent(resolvedBeatmapPath)}`;
                const checkResponse = await fetch(checkUrl);
                console.debug('[Rewind] beatmap file check', { beatmapPath: resolvedBeatmapPath, status: checkResponse.status });
                if (!checkResponse.ok) {
                    const fallbackUrl = `/rewind/file?path=${encodeURIComponent(fallbackBeatmapPath)}`;
                    const fallbackResponse = await fetch(fallbackUrl);
                    console.debug('[Rewind] beatmap fallback check', { beatmapPath: fallbackBeatmapPath, status: fallbackResponse.status });
                    if (!fallbackResponse.ok) {
                        console.warn('[Rewind] beatmap validation failed, continuing anyway');
                    }
                }

                updateProgress(85, 'Beatmap verified...');

                // Store exact beatmap path for PP calculation
                if (this.currentReplayContext) {
                    this.currentReplayContext.beatmapPath = info.osuFileName
                        ? `${info.beatmapFolder}/${info.osuFileName}`
                        : info.beatmapFolder;
                    this.currentReplayContext.statsTimeline = info.statsTimeline; // Include live recording truth
                }
            }

            updateProgress(80, 'Launching analyzer...');
            const baseUrl = '/rewind/index.html';
            const params = new URLSearchParams({
                replayPath: info.replayPath,
                songsRoot: info.songsRoot || '',
                skinsRoot: info.skinsRoot || '',
                beatmapFolder: info.beatmapFolder || '',
                osuFileName: info.osuFileName || '',
                mods: info.mods || '',
                beatmapHash: info.beatmapHash || '',
                replaysRoot: info.replaysRoot || '',
                manualLoad: '0',
                cacheBust: Date.now().toString()
            });

            const rewindFrame = rewindContainer.querySelector('.rewind-frame');
            if (rewindFrame) {
                rewindFrame.src = `${baseUrl}?${params.toString()}`;
                
                // Inject style to make everything transparent in the player
                rewindFrame.onload = () => {
                    try {
                        const style = rewindFrame.contentDocument.createElement('style');
                        style.innerHTML = `
                            body, html, #root { background: transparent !important; background-color: transparent !important; }
                            .MuiPaper-root { background-color: transparent !important; background-image: none !important; box-shadow: none !important; }
                            .MuiAppBar-root { background-color: transparent !important; background-image: none !important; box-shadow: none !important; }
                            canvas:not(:first-of-type) { display: none !important; }
                        `;
                        rewindFrame.contentDocument.head.appendChild(style);

                        // Initialize heatmap overlay
                        if (this.heatmapRenderer) this.heatmapRenderer.stop();
                        const heatmapContainer = modal.querySelector('.heatmap-circle-container');
                        this.heatmapRenderer = new HeatmapRenderer(heatmapContainer);
                        this.heatmapRenderer.start();
                        
                        console.log('[Rewind] Transparency styles and heatmap initialized');
                    } catch (e) {
                        console.warn('[Rewind] Could not inject styles:', e);
                    }
                };
            }
            if (statusOverlay) {
                statusOverlay.style.opacity = '0';
                setTimeout(() => {
                    if (statusOverlay) statusOverlay.remove();
                }, 300);
            }
        } catch (e) {
            console.error('Failed to load rewind player:', e);
            if (loadButton) loadButton.disabled = false;
        }
    }


    getDiffColorClass(stars) {
        if (!stars) return 'diff-normal';
        if (stars < 2.0) return 'diff-easy';
        if (stars < 2.7) return 'diff-normal';
        if (stars < 4.0) return 'diff-hard';
        if (stars < 5.3) return 'diff-insane';
        if (stars < 6.5) return 'diff-expert';
        return 'diff-expertplus';
    }

    getGradeClass(grade) {
        const g = (grade || '').toUpperCase();
        if (g === 'SS' || g === 'X') return 'grade-ss';
        if (g === 'SSH' || g === 'XH') return 'grade-ssh';
        if (g === 'S') return 'grade-s';
        if (g === 'SH') return 'grade-sh';
        if (g === 'A') return 'grade-a';
        if (g === 'B') return 'grade-b';
        if (g === 'C') return 'grade-c';
        if (g === 'D') return 'grade-d';
        if (g === 'F') return 'grade-f';
        return '';
    }

    getModIconName(mod) {
        const iconMap = {
            'NM': 'mod-no-mod',
            'NF': 'mod-no-fail', 'EZ': 'mod-easy', 'TD': 'mod-touch-device',
            'HD': 'mod-hidden', 'HR': 'mod-hard-rock', 'SD': 'mod-sudden-death',
            'DT': 'mod-double-time', 'RX': 'mod-relax', 'HT': 'mod-half-time',
            'NC': 'mod-nightcore', 'FL': 'mod-flashlight', 'AT': 'mod-autoplay',
            'SO': 'mod-spun-out', 'AP': 'mod-autopilot', 'PF': 'mod-perfect',
            'CL': 'mod-classic', 'MR': 'mod-mirror', 'DC': 'mod-daycore',
            'TP': 'mod-target-practice', 'SV2': 'mod-score-v2', 'DA': 'mod-difficulty-adjust',
            'BL': 'mod-blinds', 'ST': 'mod-strict-tracking', 'AC': 'mod-accuracy-challenge',
            'AL': 'mod-alternate', 'SG': 'mod-single-tap', 'WU': 'mod-wind-up',
            'WD': 'mod-wind-down', 'TC': 'mod-traceable', 'BR': 'mod-barrel-roll',
            'AD': 'mod-approach-different', 'MU': 'mod-muted', 'NS': 'mod-no-scope',
            'MG': 'mod-magnetised', 'RP': 'mod-repel', 'AS': 'mod-adaptive-speed',
            'FR': 'mod-freeze-frame', 'BU': 'mod-bubbles', 'SY': 'mod-synesthesia',
            'DP': 'mod-depth', 'BM': 'mod-bloom', 'IN': 'mod-invert',
            'CS': 'mod-constant-speed', 'HO': 'mod-hold-off', 'NR': 'mod-no-release',
            'CO': 'mod-cover', 'FI': 'mod-fade-in', 'SI': 'mod-spin-in',
            'GR': 'mod-grow', 'DF': 'mod-deflate', 'WG': 'mod-wiggle',
            'TR': 'mod-transform', 'FF': 'mod-floating-fruits', 'DS': 'mod-dual-stages',
            'SW': 'mod-swap', 'SR': 'mod-simplified-rhythm'
        };
        return iconMap[mod.toUpperCase()] || `mod-${mod.toLowerCase()}`;
    }


    getStarBadgeHTML(sr) {
        if (sr == null) return '';
        const level = Math.floor(sr);
        const safeLevel = Math.min(15, Math.max(0, level));
        
        // Strict threshold check: 0.00-6.49 (sr-text-0), 6.50+ (sr-text-1)
        const textColorClass = (sr < 6.499) ? 'sr-text-0' : 'sr-text-1';
        
        return `
            <div class="sr-badge sr-${safeLevel} ${textColorClass}">
                <span class="sr-icon">★</span>
                <span>${sr.toFixed(2)}</span>
            </div>
        `;
    }

    getStarColor(stars) {
        if (stars < 1.0) return '#4CB6FE';
        if (stars < 2.0) return '#4FFFD5';
        if (stars < 3.0) return '#88B300';
        if (stars < 4.0) return '#D3F557';
        if (stars < 5.0) return '#FDA265';
        if (stars < 6.0) return '#F94D79';
        if (stars < 7.0) return '#B64CC1';
        if (stars < 8.0) return '#5654CA';
        if (stars < 9.0) return '#14117D';
        if (stars < 10.0) return '#FFCC22';
        if (stars < 11.0) return '#EE5555';
        if (stars < 12.0) return '#E74A95';
        if (stars < 13.0) return '#9A57CE';
        if (stars < 14.0) return '#5654CA';
        return '#111111';
    }

    async handleScoreAction(action, id) {
        try {
            switch (action) {
                case 'delete':
                    if (confirm('Delete this score?')) {
                        await window.api.deletePlay(id);
                        document.querySelector(`.score-row[data-id="${id}"]`)?.remove();
                        if (this.selectedDate) this.showDayDetail(this.selectedDate);
                    }
                    break;
                case 'record':
                    const res = await window.api.triggerRecord(id);
                    if (!res.success) alert('Error: ' + res.error);
                    break;
                case 'analyze':
                    const row = document.querySelector(`.score-row[data-id="${id}"]`);
                    const scoreData = JSON.parse(row.dataset.score || '{}');
                    this.openScoreAnalysis(scoreData);
                    break;
            }
        } catch (error) {
            console.error(`[History] Action ${action} failed:`, error);
        }
    }
}

/**
 * Renders hit accuracy points on a canvas with a 5-second fade effect.
 */
class HeatmapRenderer {
    constructor(container) {
        this.container = container;
        this.canvas = document.createElement('canvas');
        this.canvas.className = 'accuracy-heatmap';
        this.ctx = this.canvas.getContext('2d');
        this.hits = []; // {time, dx, dy, result, timestamp}
        this.allHits = []; // For persistence
        this.running = false;
        this.radiusScale = 32; // Default, will be updated by CS
    }

    start() {
        this.container.appendChild(this.canvas);
        this.resize();
        window.addEventListener('resize', () => this.resize());
        this.running = true;
        this.render();
    }

    stop() {
        this.running = false;
        this.canvas.remove();
    }

    resize() {
        const rect = this.container.getBoundingClientRect();
        this.canvas.width = rect.width;
        this.canvas.height = rect.height;
    }

    addHits(spatialHits, cs) {
        if (!Array.isArray(spatialHits) || spatialHits.length === 0) return;
        if (cs != null) {
            // Multiplier set to 1.00 for raw osu! radius scale
            this.radiusScale = (54.4 - 4.48 * cs) * 1.00;
        }
        const now = Date.now();
        spatialHits.forEach(h => {
            const hit = { ...h, timestamp: now };
            this.hits.push(hit);
            this.allHits.push(h);
        });
    }

    getSnapshot() {
        return this.allHits;
    }

    render() {
        if (!this.running) return;

        const now = Date.now();
        this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);

        const centerX = this.canvas.width / 2;
        const centerY = this.canvas.height / 2;

        // Draw reference crosshair
        this.ctx.beginPath();
        this.ctx.strokeStyle = 'rgba(255, 255, 255, 0.05)';
        this.ctx.lineWidth = 1;
        this.ctx.moveTo(centerX, 0);
        this.ctx.lineTo(centerX, this.canvas.height);
        this.ctx.moveTo(0, centerY);
        this.ctx.lineTo(this.canvas.width, centerY);
        this.ctx.stroke();

        // Draw tiny + in the middle
        const plusSize = 3;
        this.ctx.beginPath();
        this.ctx.strokeStyle = 'rgba(255, 255, 255, 0.5)';
        this.ctx.lineWidth = 1;
        this.ctx.moveTo(centerX - plusSize, centerY);
        this.ctx.lineTo(centerX + plusSize, centerY);
        this.ctx.moveTo(centerX, centerY - plusSize);
        this.ctx.lineTo(centerX, centerY + plusSize);
        this.ctx.stroke();

        // Remove hits older than 5 seconds
        this.hits = this.hits.filter(h => now - h.timestamp < 5000);

        this.hits.forEach(hit => {
            const age = now - hit.timestamp;
            const opacity = Math.max(0, 1 - age / 5000);
            
            // Map result to color
            let color = '#00FFFF'; // GREAT (300) - Cyan
            if (hit.result === 1) color = '#00FF88'; // OK (100) - Green
            if (hit.result === 2) color = '#FFCC00'; // MEH (50) - Yellow
            if (hit.result === 3) color = '#FF3C78'; // MISS - Red (extra)

            const x = hit.dx * (this.canvas.width / 2 / this.radiusScale);
            const y = hit.dy * (this.canvas.height / 2 / this.radiusScale);

            this.ctx.globalAlpha = opacity;
            const size = hit.result === 0 ? 3.0 : hit.result === 1 ? 2.8 : 2.5;

            this.ctx.beginPath();
            this.ctx.fillStyle = color;
            this.ctx.arc(centerX + x, centerY + y, size, 0, Math.PI * 2);
            this.ctx.fill();
            
            this.ctx.shadowBlur = 1;
            this.ctx.shadowColor = color;
            this.ctx.shadowBlur = 0;
        });

        this.ctx.globalAlpha = 1.0;
        requestAnimationFrame(() => this.render());
    }
}

window.historyModule = new HistoryModule();

