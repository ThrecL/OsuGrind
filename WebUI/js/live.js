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
        const oldChars = this.lastValueText.split('');
        
        // Ensure element has correct number of digit containers
        if (this.el.children.length !== chars.length) {
            this.el.innerHTML = chars.map(c => this.createDigitContainer(c)).join('');
        }

        const containers = this.el.querySelectorAll('.digit-container');
        chars.forEach((char, i) => {
            if (char === oldChars[i]) return;
            
            const strip = containers[i].querySelector('.digit-strip');
            if (!strip) return;

            if (isNaN(char) || char === ' ') {
                // For non-numeric characters (%, ., ,), just update text
                strip.querySelector('span').textContent = char;
                strip.style.transform = 'translateY(0)';
                return;
            }

            // Numeric slide
            const targetDigit = parseInt(char);
            strip.style.transform = `translateY(-${targetDigit * 10}%)`;
        });

        // Trigger container pop animation ONLY FOR COMBO
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
        // Create a strip from 0 to 9
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
        this.elements = {};
        this.slots = {};
        this.lastModsJson = "";
    }

    init() {
        this.cacheElements();
        this.initSlots();
        this.subscribeToLiveData();
        console.log('[Live] Professional HUD initialized');
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
            mapTime: document.getElementById('mapTime')
        };
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
        const el = this.elements;

        if (data.backgroundPath && el.background) {
            let bgUrl;
            if (data.backgroundPath.includes('STABLE:')) {
                const parts = data.backgroundPath.split('STABLE:');
                bgUrl = `/api/background/stable?path=${encodeURIComponent(parts[1])}`;
            } else {
                bgUrl = data.backgroundPath.replace(/\\/g, '/');
            }
            el.background.style.backgroundImage = `url('${bgUrl}')`;
        }

        if (el.mapTitle) {
            let title = data.mapName || 'No Map Selected';
            if (title.includes(' - ')) title = title.split(' - ').slice(1).join(' - ');
            el.mapTitle.textContent = title;
        }
        if (el.mapArtist) el.mapArtist.textContent = data.artist || '—';
        if (el.playState) el.playState.textContent = data.playState || 'Idle';
        if (el.achievementStatus) el.achievementStatus.textContent = data.achievement || '';

        if (el.modsContainer && data.mods) this.renderMods(data.mods);

        const state = data.playState || 'Idle';
        const isPlaying = state === 'Playing' || state === 'Results' || state === 'Replay' || state === 'Paused';

        // Accuracy normalize check
        const acc = data.accuracy != null ? data.accuracy : 1.0;
        if (this.slots.accuracy) this.slots.accuracy.setValue(acc);
        if (this.slots.combo) this.slots.combo.setValue(isPlaying ? (data.combo || 0) : (data.mapMaxCombo || 0));
        if (this.slots.score) this.slots.score.setValue(data.score || 0);
        if (this.slots.pp) {
            const ppVal = isPlaying ? (data.pp || 0) : (data.ppIfFc || 0);
            this.slots.pp.setValue(ppVal);
        }
        if (this.slots.stars) {
            const sr = data.stars || 0;
            this.slots.stars.setValue(sr);
            
            // Dynamic Star Badge Styling
            const container = document.getElementById('sr-badge-container');
            if (container) {
                // Remove existing sr-* and sr-text-* classes
                container.className = container.className.replace(/\bsr-\d+\b/g, '').replace(/\bsr-text-\d+\b/g, '');
                const level = Math.floor(sr);
                const safeLevel = Math.min(15, Math.max(0, level));
                container.classList.add(`sr-${safeLevel}`);
                
                // Text color logic: 0.00-6.49 (#000000BF), 6.50+ (#FFD966)
                // Use strict check to avoid floating point issues (6.50 should be gold)
                const textColorClass = (sr < 6.499) ? 'sr-text-0' : 'sr-text-1';
                container.classList.add(textColorClass);
            }
        }
        if (this.slots.bpm) this.slots.bpm.setValue(data.bpm || 0);

        // Grade
        if (el.grade) {
            const grade = isPlaying ? (data.grade || '—') : 'SS';
            if (el.grade.textContent !== grade) {
                el.grade.textContent = grade;
                el.grade.className = `grade ${grade}`;
            }
        }

        // Static attributes
        if (el.cs) el.cs.textContent = (data.cs || 0).toFixed(1);
        if (el.ar) el.ar.textContent = (data.ar || 0).toFixed(1);
        if (el.od) el.od.textContent = (data.od || 0).toFixed(1);
        if (el.hp) el.hp.textContent = (data.hp || 0).toFixed(1);

        // Update HP Bar if playing
        if (isPlaying && el.hp) {
             const health = data.liveHP != null ? data.liveHP : (data.hp || 0);
             // In Lazer HP is often 0-1, in Stable it might be 0-1 or 0-100?
             // Most memory readers normalize to 0-1.
             const hpPercent = Math.min(100, Math.max(0, health * 100));
             // el.hpBar.style.width = `${hpPercent}%`; // Assuming there is an HP bar
        }

        // Hit Counts with slots
        const hits = data.hitCounts || {};
        if (isPlaying) {
            if (this.slots.count300) this.slots.count300.setValue(hits.great || 0);
            if (this.slots.count100) this.slots.count100.setValue(hits.ok || 0);
            if (this.slots.count50) this.slots.count50.setValue(hits.meh || 0);
            if (this.slots.countMiss) this.slots.countMiss.setValue(hits.miss || 0);
        } else {
            // Song select: everything should be "the max"
            // Show total objects as "Great" hits
            if (this.slots.count300) this.slots.count300.setValue(data.totalObjects || 0);
            if (this.slots.count100) this.slots.count100.setValue(0);
            if (this.slots.count50) this.slots.count50.setValue(0);
            if (this.slots.countMiss) this.slots.countMiss.setValue(0);
        }

        // Time
        if (el.mapTime) {
            let time = isPlaying ? (data.currentTime || 0) : (data.totalTime || 0);
            const mins = Math.floor(time / 60);
            const secs = Math.floor(time % 60);
            el.mapTime.textContent = `${mins}:${secs.toString().padStart(2, '0')}`;
        }

        if (window.app) window.app.setPlaying(isPlaying);
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
