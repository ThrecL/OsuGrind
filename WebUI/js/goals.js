/**
 * OSU!GRIND Daily Goals Module
 * Handles setting and tracking daily grind goals with enhanced UX
 */

class GoalsModule {
    constructor() {
        this.settings = {
            plays: 20,
            hits: 5000,
            stars: 5.0,
            pp: 100
        };
        this.progress = {
            plays: 0,
            hits: 0,
            stars: 0,
            pp: 0
        };
        this.previousProgress = { ...this.progress };
        this.saveTimeout = null;
        this.completedGoals = new Set();
    }

    init() {
        this.setupEventListeners();
        this.refresh();
        console.log('[Goals] Module initialized');
    }

    setupEventListeners() {
        document.getElementById('streakPill')?.addEventListener('click', () => this.show());
        document.getElementById('closeGoals')?.addEventListener('click', () => this.hide());
        
        // Input listeners for autosave
        this.setupInputListener('goalPlaysInput', 'plays');
        this.setupInputListener('goalHitsInput', 'hits');
        this.setupInputListener('goalStarsInput', 'stars', true);
        this.setupInputListener('goalPPInput', 'pp');

        // Close on backdrop click
        document.querySelector('#goalsOverlay .overlay-backdrop')?.addEventListener('click', () => this.hide());
    }

    setupInputListener(id, key, isFloat = false) {
        const el = document.getElementById(id);
        if (!el) return;

        el.addEventListener('input', (e) => {
            const val = isFloat ? parseFloat(e.target.value) : parseInt(e.target.value);
            
            // Validation
            if (isNaN(val) || val < 0) {
                el.classList.add('error');
                setTimeout(() => el.classList.remove('error'), 300);
                return;
            }
            
            // Max validation
            const maxValues = { plays: 1000, hits: 100000, stars: 15, pp: 10000 };
            if (val > maxValues[key]) {
                el.classList.add('error');
                setTimeout(() => el.classList.remove('error'), 300);
                return;
            }

            el.classList.remove('error');
            this.settings[key] = val;
            this.updateUI(true); // Optimistic update (skip re-rendering inputs)
            this.debounceSave();
        });

        // Add validation feedback on blur
        el.addEventListener('blur', (e) => {
            if (!e.target.value) {
                const defaults = { plays: 20, hits: 5000, stars: 5.0, pp: 100 };
                e.target.value = defaults[key];
                this.settings[key] = defaults[key];
            }
        });
    }

    debounceSave() {
        const statusEl = document.getElementById('goalsStatus');
        if (statusEl) {
            statusEl.textContent = "Saving...";
            statusEl.classList.add('visible');
        }

        if (this.saveTimeout) clearTimeout(this.saveTimeout);
        this.saveTimeout = setTimeout(() => this.saveGoals(), 1000);
    }

    async refresh() {
        try {
            const data = await window.api.fetch('/api/goals');
            if (data.settings) this.settings = data.settings;
            if (data.progress) {
                this.previousProgress = { ...this.progress };
                this.progress = data.progress;
            }
            
            this.updateUI();
            this.checkCompletions();
        } catch (error) {
            console.error('[Goals] Failed to refresh:', error);
        }
    }

    show() {
        const overlay = document.getElementById('goalsOverlay');
        if (!overlay) return;

        // Fill inputs with current settings
        this.updateInputValues();

        overlay.style.display = 'flex';
        this.refresh();
    }

    hide() {
        const overlay = document.getElementById('goalsOverlay');
        if (overlay) overlay.style.display = 'none';
    }

    updateInputValues() {
        const setVal = (id, val) => {
            const el = document.getElementById(id);
            if(el) el.value = val;
        };
        setVal('goalPlaysInput', this.settings.plays);
        setVal('goalHitsInput', this.settings.hits);
        setVal('goalStarsInput', this.settings.stars.toFixed(1));
        setVal('goalPPInput', this.settings.pp);
    }

    updateUI(skipInputs = false) {
        // Update Targets
        document.getElementById('goalPlaysTarget').textContent = `/ ${this.settings.plays}`;
        document.getElementById('goalHitsTarget').textContent = `/ ${this.settings.hits.toLocaleString()}`;
        document.getElementById('goalStarsTarget').textContent = `≥ ${this.settings.stars.toFixed(1)}★`;
        document.getElementById('goalPPTarget').textContent = `/ ${this.settings.pp}`;

        // Update Current Values with animation trigger
        const valueChanged = (id, value) => {
            const el = document.getElementById(id);
            if (el) {
                const oldValue = el.textContent;
                el.textContent = value;
                if (oldValue !== value.toString()) {
                    el.parentElement.parentElement.classList.add('updated');
                    setTimeout(() => el.parentElement.parentElement.classList.remove('updated'), 300);
                }
            }
        };

        valueChanged('goalPlaysCurrent', this.progress.plays);
        valueChanged('goalHitsCurrent', this.progress.hits.toLocaleString());
        valueChanged('goalStarsCurrent', this.progress.stars);
        valueChanged('goalPPCurrent', Math.round(this.progress.pp));

        // Update Fills with percentages
        this.updateFill('goalPlaysFill', 'goalPlaysPercent', this.progress.plays, this.settings.plays, 'plays');
        this.updateFill('goalHitsFill', 'goalHitsPercent', this.progress.hits, this.settings.hits, 'hits');
        // For stars, use 10 as "challenge" count base
        this.updateFill('goalStarsFill', 'goalStarsPercent', this.progress.stars, 10, 'stars'); 
        this.updateFill('goalPPFill', 'goalPPPercent', this.progress.pp, this.settings.pp, 'pp');

        if (!skipInputs) this.updateInputValues();
    }

    updateFill(fillId, percentId, current, target, goalKey) {
        const fillEl = document.getElementById(fillId);
        const percentEl = document.getElementById(percentId);
        if (!fillEl) return;
        
        const percent = target > 0 ? Math.min(100, (current / target) * 100) : 0;
        fillEl.style.width = `${percent}%`;
        
        // Update percentage label
        if (percentEl) {
            percentEl.textContent = `${Math.round(percent)}%`;
        }
        
        // Get goal card parent
        const card = fillEl.closest('.goal-card');
        if (!card) return;
        
        // Mark as completed
        if (percent >= 100) {
            fillEl.classList.add('completed');
            card.classList.add('completed');
            
            // Confetti effect on first completion
            if (!this.completedGoals.has(goalKey)) {
                this.completedGoals.add(goalKey);
                this.triggerConfetti(card);
            }
        } else {
            fillEl.classList.remove('completed');
            card.classList.remove('completed');
            this.completedGoals.delete(goalKey);
        }
    }

    triggerConfetti(cardElement) {
        // Simple confetti-like particle effect
        const rect = cardElement.getBoundingClientRect();
        const centerX = rect.left + rect.width / 2;
        const centerY = rect.top + rect.height / 2;
        
        for (let i = 0; i < 15; i++) {
            const particle = document.createElement('div');
            particle.style.position = 'fixed';
            particle.style.left = `${centerX}px`;
            particle.style.top = `${centerY}px`;
            particle.style.width = '6px';
            particle.style.height = '6px';
            particle.style.borderRadius = '50%';
            particle.style.pointerEvents = 'none';
            particle.style.zIndex = '10000';
            
            const colors = ['var(--accent-pink)', 'var(--accent-cyan)', 'var(--accent-yellow)', 'var(--accent-purple)', 'var(--accent-green)'];
            particle.style.background = colors[Math.floor(Math.random() * colors.length)];
            
            document.body.appendChild(particle);
            
            const angle = (Math.PI * 2 * i) / 15;
            const velocity = 100 + Math.random() * 100;
            const vx = Math.cos(angle) * velocity;
            const vy = Math.sin(angle) * velocity - 100;
            
            let posX = centerX;
            let posY = centerY;
            let velX = vx;
            let velY = vy;
            let opacity = 1;
            
            const animate = () => {
                velY += 5; // gravity
                posX += velX * 0.016;
                posY += velY * 0.016;
                opacity -= 0.02;
                
                particle.style.left = `${posX}px`;
                particle.style.top = `${posY}px`;
                particle.style.opacity = opacity;
                
                if (opacity > 0) {
                    requestAnimationFrame(animate);
                } else {
                    particle.remove();
                }
            };
            
            requestAnimationFrame(animate);
        }
    }

    checkCompletions() {
        // Check if any goals were just completed
        const goals = ['plays', 'hits', 'pp'];
        goals.forEach(key => {
            const current = this.progress[key];
            const target = this.settings[key];
            const previous = this.previousProgress[key];
            
            if (current >= target && previous < target) {
                // Just completed this goal!
                console.log(`[Goals] 🎉 ${key} goal completed!`);
            }
        });
    }

    async saveGoals() {
        const payload = this.settings;

        try {
            const res = await window.api.fetch('/api/goals/save', {
                method: 'POST',
                body: JSON.stringify(payload)
            });

            if (res.success) {
                const statusEl = document.getElementById('goalsStatus');
                if (statusEl) {
                    statusEl.textContent = "✓ All changes saved";
                    statusEl.classList.add('visible', 'success');
                    statusEl.classList.remove('error');
                    setTimeout(() => {
                        statusEl.classList.remove('visible');
                    }, 2500);
                }
            }
        } catch (error) {
            console.error('[Goals] Save failed:', error);
            const statusEl = document.getElementById('goalsStatus');
            if (statusEl) {
                statusEl.textContent = "✗ Error saving changes";
                statusEl.classList.add('visible', 'error');
                statusEl.classList.remove('success');
                setTimeout(() => {
                    statusEl.classList.remove('visible');
                }, 3000);
            }
        }
    }
}

window.goalsModule = new GoalsModule();
