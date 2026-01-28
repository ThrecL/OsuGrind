/**
 * OSU!GRIND Live Tab Module
 * Professional Slot Machine (Odometer style) animations
 */

const MOD_FILES = {
    'EZ': 'mod-easy.png', 'NF': 'mod-no-fail.png', 'HT': 'mod-half-time.png',
    'DC': 'mod-daycore.png', 'HR': 'mod-hard-rock.png', 'SD': 'mod-sudden-death.png',
    'PF': 'mod-perfect.png', 'DT': 'mod-double-time.png', 'NC': 'mod-nightcore.png',
    'HD': 'mod-hidden.png', 'FL': 'mod-flashlight.png', 'RL': 'mod-relax.png',
    'AP': 'mod-autopilot.png', 'SO': 'mod-spun-out.png', 'AT': 'mod-autoplay.png',
    'AU': 'mod-autoplay.png', 'CN': 'mod-cinema.png', 'SV2': 'mod-score-v2.png',
    'TD': 'mod-touch-device.png', 'CL': 'mod-classic.png', 'MR': 'mod-mirror.png',
    'RD': 'mod-random.png', 'DA': 'mod-difficulty-adjust.png', 'BL': 'mod-blinds.png',
    'ST': 'mod-strict-tracking.png', 'AC': 'mod-accuracy-challenge.png',
    'TP': 'mod-target-practice.png', 'SG': 'mod-single-tap.png', 'AL': 'mod-alternate.png',
    'WU': 'mod-wind-up.png', 'WD': 'mod-wind-down.png', 'TC': 'mod-traceable.png',
    'BR': 'mod-barrel-roll.png', 'AD': 'mod-approach-different.png', 'MU': 'mod-muted.png',
    'NS': 'mod-no-scope.png', 'MG': 'mod-magnetised.png', 'RP': 'mod-repel.png',
    'AS': 'mod-adaptive-speed.png', 'FR': 'mod-freeze-frame.png', 'BU': 'mod-bubbles.png',
    'DP': 'mod-depth.png', 'BM': 'mod-bloom.png', 'IN': 'mod-invert.png',
    'CS': 'mod-constant-speed.png', 'HO': 'mod-hold-off.png', 'NR': 'mod-no-release.png',
    'CO': 'mod-cover.png', 'FI': 'mod-fade-in.png', 'SI': 'mod-spin-in.png',
    'GR': 'mod-grow.png', 'DF': 'mod-deflate.png', 'WG': 'mod-wiggle.png',
    'TR': 'mod-transform.png', 'FF': 'mod-floating-fruits.png', 'DS': 'mod-dual-stages.png',
    'SW': 'mod-swap.png', 'SR': 'mod-simplified-rhythm.png', 'SY': 'mod-synesthesia.png',
    '1K': 'mod-one-key.png', '2K': 'mod-two-keys.png', '3K': 'mod-three-keys.png',
    '4K': 'mod-four-keys.png', '5K': 'mod-five-keys.png', '6K': 'mod-six-keys.png',
    '7K': 'mod-seven-keys.png', '8K': 'mod-eight-keys.png', '9K': 'mod-nine-keys.png',
    '10K': 'mod-ten-keys.png'
};

class SlotNumber {
    constructor(element, options = {}) {
        this.el = element;
        this.isAcc = options.isAcc || false;
        this.suffix = options.suffix || '';
        this.precision = options.precision || 0;
        this.lastValueText = "";
    }

    setValue(value) {
        if (value === undefined || value === null) return;
        let text;
        if (this.isAcc) {
            text = (value * 100).toFixed(2) + '%';
        } else if (this.precision > 0) {
            text = value.toFixed(this.precision);
        } else {
            text = Math.round(value).toLocaleString();
        }
        text += this.suffix;

        if (text === this.lastValueText) return;
        this.render(text);
        this.lastValueText = text;
    }

    render(text) {
        const chars = text.split('');
        const oldChars = (this.lastValueText || "").split('');
        
        if (this.el.children.length !== chars.length) {
            this.el.innerHTML = chars.map(c => this.createDigitContainer(c)).join('');
        }

        const containers = this.el.querySelectorAll('.digit-container');
        chars.forEach((char, i) => {
            if (char === oldChars[i]) return;
            const strip = containers[i].querySelector('.digit-strip');
            if (!strip) return;

            if (isNaN(char) || char === ' ') {
                const span = strip.querySelector('span');
                if (span && span.textContent !== char) span.textContent = char;
                strip.style.transform = 'translateY(0)';
                return;
            }

            const targetDigit = parseInt(char);
            const targetY = -(targetDigit * 10);
            strip.style.transform = `translateY(${targetY}%)`;
        });

        if (this.el.id === 'combo' || this.el.id === 'analysis-combo') {
            const target = this.el.classList.contains('side-value') ? this.el.parentElement : this.el;
            target.classList.remove('changed');
            void target.offsetWidth;
            target.classList.add('changed');
        }
    }

    createDigitContainer(char) {
        if (isNaN(char) || char === ' ') {
            return `<div class="digit-container"><div class="digit-strip"><span>${char}</span></div></div>`;
        }
        let stripHtml = '';
        for (let i = 0; i <= 9; i++) {
            stripHtml += `<span>${i}</span>`;
        }
        const initialOffset = parseInt(char) * 10;
        return `
            <div class="digit-container">
                <div class="digit-strip" style="transform: translateY(-${initialOffset}%)">
                    ${stripHtml}
                </div>
            </div>`;
    }
}

class LiveModule {
    constructor() {
        this.cacheElements();
        this.slots = {};
        this.lastState = 'Idle';
        this.lastModsJson = '';
        this.currentMapName = '';
        
        this.topPlays = [];
        this.currentPPTier = null;
        this._lastStarsBadgeVal = -1;

        this.initSlots();
        this.subscribeToLiveData();
    }

    init() {
        console.log('[Live] HUD initialized');
        this.fetchTopPlays();
        setInterval(() => this.fetchTopPlays(), 5 * 60 * 1000);
    }

    async fetchTopPlays() {
        try {
            console.log('[Live] Refreshing top 100 plays cache...');
            const plays = await window.api.getTopPlays();
            if (Array.isArray(plays)) {
                this.topPlays = plays.map(p => {
                    const pp = p.pp ?? p.PP ?? 0;
                    return typeof pp === 'number' ? pp : parseFloat(pp) || 0;
                }).filter(pp => pp > 0).sort((a, b) => b - a);
                console.log(`[Live] Cached ${this.topPlays.length} top plays.`);
            }
        } catch (e) {
            console.error('[Live] Failed to fetch top plays:', e);
        }
    }

    cacheElements() {
        this.elements = {
            background: document.getElementById('liveBackground'),
            mapTitle: document.getElementById('mapTitle'),
            mapArtist: document.getElementById('mapArtist'),
            playState: document.getElementById('playState'),
            achievementStatus: document.getElementById('achievementStatus'),
            modsContainer: document.getElementById('modsContainer'),
            accuracy: document.getElementById('accuracy'),
            grade: document.getElementById('grade'),
            combo: document.getElementById('combo'),
            score: document.getElementById('score'),
            pp: document.getElementById('pp'),
            cs: document.getElementById('cs'),
            ar: document.getElementById('ar'),
            od: document.getElementById('od'),
            hp: document.getElementById('hp'),
            stars: document.getElementById('stars'),
            bpm: document.getElementById('bpm'),
            count300: document.getElementById('count300'),
            count100: document.getElementById('count100'),
            count50: document.getElementById('count50'),
            countMiss: document.getElementById('countMiss'),
            mapTime: document.getElementById('mapTime'),
            locateBtn: document.getElementById('locateOsuBtn')
        };
        
        if (this.elements.locateBtn) {
            this.elements.locateBtn.onclick = () => {
                const game = this._lastGameName || 'osu';
                window.chrome.webview.postMessage({ action: 'browseFolder', url: game.toLowerCase().includes('lazer') ? 'osu!lazer' : 'osu!stable' });
            };
        }
    }

    initSlots() {
        const el = this.elements;
        if (el.accuracy) this.slots.accuracy = new SlotNumber(el.accuracy, { isAcc: true });
        if (el.combo) this.slots.combo = new SlotNumber(el.combo, { suffix: 'x' });
        if (el.score) this.slots.score = new SlotNumber(el.score);
        if (el.pp) this.slots.pp = new SlotNumber(el.pp);
        if (el.stars) this.slots.stars = new SlotNumber(el.stars, { precision: 2 });
        if (el.bpm) this.slots.bpm = new SlotNumber(el.bpm);
        if (el.count300) this.slots.count300 = new SlotNumber(el.count300);
        if (el.count100) this.slots.count100 = new SlotNumber(el.count100);
        if (el.count50) this.slots.count50 = new SlotNumber(el.count50);
        if (el.countMiss) this.slots.countMiss = new SlotNumber(el.countMiss);
    }

    subscribeToLiveData() {
        window.api.onLiveData(data => this.updateUI(data));
    }

    updateUI(data) {
        if (!data || data.type === 'refresh') return;
        const el = this.elements;

        if (data.backgroundPath && el.background) {
            let bgUrl;
            if (data.backgroundPath.includes('STABLE:')) {
                const parts = data.backgroundPath.split('STABLE:');
                bgUrl = `/api/background/stable?path=${encodeURIComponent(parts[1])}`;
            } else {
                bgUrl = data.backgroundPath.replace(/\\/g, '/');
            }
            const newBg = `url("${bgUrl}")`;
            if (el.background.style.backgroundImage !== newBg) {
                el.background.style.backgroundImage = newBg;
            }
        }

        if (el.mapTitle) {
            let title = data.mapName || 'No Map Selected';
            if (title.includes(' - ')) title = title.split(' - ').slice(1).join(' - ');
            if (el.mapTitle.textContent !== title) el.mapTitle.textContent = title;
        }
        if (el.mapArtist && el.mapArtist.textContent !== (data.artist || '—')) {
            el.mapArtist.textContent = data.artist || '—';
        }

        this._lastGameName = data.gameName;
        if (el.locateBtn) {
            // Show button if game is connected but map file wasn't found
            const status = data.connectionStatus;
            const mapFound = data.mapFileFound;
            el.locateBtn.style.display = (status === 'connected' && !mapFound && data.mapName !== 'Searching...') ? 'block' : 'none';
        }

        if (el.playState && el.playState.textContent !== (data.playState || 'Idle')) {
            el.playState.textContent = data.playState || 'Idle';
        }
        if (el.achievementStatus) el.achievementStatus.textContent = data.achievement || '';

        if (el.modsContainer && data.mods) this.renderMods(data.mods);

        const state = data.playState || 'Idle';
        const isPlaying = state === 'Playing' || state === 'Results' || state === 'Replay' || state === 'Paused';

        const hits = data.hitCounts || {};
        const h300 = hits.great ?? 0;
        const h100 = hits.ok ?? 0;
        const h50 = hits.meh ?? 0;
        const hMiss = hits.miss ?? 0;
        const totalHits = h300 + h100 + h50 + hMiss;
        const suppressStats = (state === 'Replay' && totalHits === 0);

        if (isPlaying && this.lastState !== state && (state === 'Playing' || state === 'Replay')) {
            if (this.slots.accuracy) this.slots.accuracy.setValue(1.0);
            if (this.slots.combo) this.slots.combo.setValue(0);
            if (this.slots.score) this.slots.score.setValue(0);
            if (this.slots.pp) this.slots.pp.setValue(0);
            if (this.slots.count300) this.slots.count300.setValue(0);
            if (this.slots.count100) this.slots.count100.setValue(0);
            if (this.slots.count50) this.slots.count50.setValue(0);
            if (this.slots.countMiss) this.slots.countMiss.setValue(0);
            this.clearPPTier();
        }
        this.lastState = state;

        const acc = (data.accuracy != null && !suppressStats) ? data.accuracy : 1.0;
        if (this.slots.accuracy) this.slots.accuracy.setValue(acc);
        
        if (this.slots.combo) {
            let comboVal = 0;
            if (state === 'Results') comboVal = data.maxCombo || 0;
            else if (isPlaying && !suppressStats) comboVal = data.combo || 0;
            else if (!isPlaying) comboVal = data.mapMaxCombo || 0;
            this.slots.combo.setValue(comboVal);
        }

        if (this.slots.score) {
            const scoreVal = (isPlaying && !suppressStats) ? (data.score || 0) : (isPlaying ? 0 : (data.score || 0));
            this.slots.score.setValue(scoreVal);
        }

        let ppVal = 0;
        if (this.slots.pp) {
            if (state === 'Results') ppVal = data.pp || 0;
            else if (isPlaying && !suppressStats) ppVal = data.pp || 0;
            else if (isPlaying && suppressStats) ppVal = 0;
            else ppVal = data.ppIfFc || 0;
            const numericPP = parseFloat(ppVal) || 0;
            this.slots.pp.setValue(numericPP);
            this.updatePPTier(numericPP, isPlaying || state === 'Results');
        }
        
        if (this.slots.stars) {
            const sr = data.stars || 0;
            const baseSr = data.baseStars || sr;
            this.slots.stars.setValue(sr);
            
            const badge = document.getElementById('sr-badge-container');
            if (badge) {
                const diff = sr - baseSr;
                badge.classList.remove('modified-harder', 'modified-easier');
                
                if (diff > 0.01) {
                    badge.classList.add('modified-harder');
                } else if (diff < -0.01) {
                    badge.classList.add('modified-easier');
                }
            }

            if (this._lastStarsBadgeVal !== sr) {
                this._lastStarsBadgeVal = sr;
                const container = document.getElementById('sr-badge-container');
                if (container) {
                    container.className = container.className.replace(/\bsr-\d+\b/g, '').replace(/\bsr-text-\d+\b/g, '');
                    const level = Math.floor(sr);
                    const safeLevel = Math.min(15, Math.max(0, level));
                    container.classList.add(`sr-${safeLevel}`);
                    const textColorClass = (sr < 6.499) ? 'sr-text-0' : 'sr-text-1';
                    container.classList.add(textColorClass);
                }
            }
        }

        // Attributes (CS, AR, OD, HP, BPM) - Color red if harder, green if easier
        const updateAttr = (el, val, base) => {
            if (!el) return;
            el.textContent = (val || 0).toFixed(1);
            const diff = (val || 0) - (base || val || 0);
            el.classList.remove('modified-harder', 'modified-easier');
            
            if (diff > 0.05) {
                el.classList.add('modified-harder');
            } else if (diff < -0.05) {
                el.classList.add('modified-easier');
            }
        };

        updateAttr(el.cs, data.cs, data.baseCS);
        updateAttr(el.ar, data.ar, data.baseAR);
        updateAttr(el.od, data.od, data.baseOD);
        updateAttr(el.hp, data.hp, data.baseHP);

        if (el.bpm) {
            const bpm = data.bpm || 0;
            const baseBpm = data.baseBPM || bpm;
            this.slots.bpm.setValue(bpm);
            const bpmValueEl = document.getElementById('bpm');
            if (bpmValueEl) {
                const diff = bpm - baseBpm;
                bpmValueEl.classList.remove('modified-harder', 'modified-easier');
                if (diff > 1.0) {
                    bpmValueEl.classList.add('modified-harder');
                } else if (diff < -1.0) {
                    bpmValueEl.classList.add('modified-easier');
                }
            }
        }

        if (el.grade) {
            const grade = (isPlaying && !suppressStats) ? (data.grade || '—') : 'SS';
            if (el.grade.textContent !== grade) {
                el.grade.textContent = grade;
                el.grade.className = `grade ${grade}`;
            }
        }

        if (isPlaying) {
            if (this.slots.count300) this.slots.count300.setValue(suppressStats ? 0 : h300);
            if (this.slots.count100) this.slots.count100.setValue(suppressStats ? 0 : h100);
            if (this.slots.count50) this.slots.count50.setValue(suppressStats ? 0 : h50);
            if (this.slots.countMiss) this.slots.countMiss.setValue(suppressStats ? 0 : hMiss);
        } else {
            if (this.slots.count300) this.slots.count300.setValue(data.totalObjects || 0);
            if (this.slots.count100) this.slots.count100.setValue(0);
            if (this.slots.count50) this.slots.count50.setValue(0);
            if (this.slots.countMiss) this.slots.countMiss.setValue(0);
        }

        if (el.mapTime) {
            let time = isPlaying ? (data.currentTime || 0) : (data.totalTime || 0);
            const mins = Math.floor(time / 60);
            const secs = Math.floor(time % 60);
            el.mapTime.textContent = `${mins}:${secs.toString().padStart(2, '0')}`;
        }
        
        const timerActive = state === 'Playing';
        if (window.app) window.app.setPlaying(timerActive);
    }

    updatePPTier(ppVal, isPlayingOrResults) {
        if (!isPlayingOrResults || !this.topPlays.length || ppVal < 1) {
            this.clearPPTier();
            return;
        }
        let tier = null;
        const count = this.topPlays.length;
        if (ppVal >= this.topPlays[0]) tier = 'rank-top1';
        else if (count >= 2 && ppVal >= this.topPlays[1]) tier = 'rank-top2';
        else if (count >= 3 && ppVal >= this.topPlays[2]) tier = 'rank-top3';
        else if (count >= 5 && ppVal >= this.topPlays[4]) tier = 'rank-top5';
        else if (count >= 10 && ppVal >= this.topPlays[9]) tier = 'rank-top10';
        else if (count >= 15 && ppVal >= this.topPlays[14]) tier = 'rank-top15';
        else if (count >= 25 && ppVal >= this.topPlays[24]) tier = 'rank-top25';
        else if (count >= 50 && ppVal >= this.topPlays[49]) tier = 'rank-top50';
        else if (count >= 100 && ppVal >= this.topPlays[99]) tier = 'rank-top100';

        if (this.currentPPTier !== tier) {
            this.clearPPTier();
            if (tier) {
                this.elements.pp?.classList.add(tier);
                if (tier === 'rank-top1' && this.currentPPTier !== 'rank-top1') this.triggerTop1Effect();
            }
            this.currentPPTier = tier;
        }
    }

    clearPPTier() {
        if (this.currentPPTier) {
            this.elements.pp?.classList.remove(this.currentPPTier);
            this.currentPPTier = null;
        }
    }

    triggerTop1Effect() {
        document.body.classList.add('screen-shake');
        setTimeout(() => document.body.classList.remove('screen-shake'), 500);
        this.spawnConfetti();
    }

    spawnConfetti() {
        for (let i = 0; i < 50; i++) {
            const confetti = document.createElement('div');
            confetti.className = 'confetti-particle';
            confetti.style.left = Math.random() * 100 + 'vw';
            confetti.style.backgroundColor = `hsl(${Math.random() * 360}, 100%, 50%)`;
            confetti.style.transform = `scale(${Math.random()})`;
            confetti.style.animationDuration = (Math.random() * 2 + 2) + 's';
            confetti.style.animationDelay = (Math.random() * 0.5) + 's';
            document.body.appendChild(confetti);
            setTimeout(() => confetti.remove(), 4000);
        }
    }

    renderMods(mods = []) {
        const container = this.elements.modsContainer;
        if (!container) return;
        const modsToRender = mods || [];
        const modsJson = JSON.stringify(modsToRender);
        if (modsJson === this.lastModsJson) return;
        this.lastModsJson = modsJson;
        if (modsToRender.length === 0 || (modsToRender.length === 1 && modsToRender[0] === 'NM')) {
            container.innerHTML = '';
            return;
        }
        container.innerHTML = modsToRender.map(mod => {
            const file = MOD_FILES[mod.toUpperCase()];
            return file ? `<img class="mod-icon" src="/Assets/Mods/${file}" alt="${mod}">` : `<span class="mod-badge">${mod}</span>`;
        }).join('');
    }
}

window.SlotNumber = SlotNumber;
window.liveModule = new LiveModule();
