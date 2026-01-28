    update(data) {
        try {
            if (!data || data.type === 'refresh') return;
            const el = this.elements;

            // 1. Background
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

            // 2. Map Metadata
            if (el.mapTitle) {
                let title = data.mapName || 'No Map Selected';
                if (title.includes(' - ')) title = title.split(' - ').slice(1).join(' - ');
                el.mapTitle.textContent = title;
                
                // If map name changed, reset max combo cache
                if (this.currentMapName !== data.mapName) {
                    this.currentMapName = data.mapName;
                    this.cachedMapMaxCombo = 0;
                }
            }
            if (el.mapArtist) el.mapArtist.textContent = data.artist || '—';
            if (el.playState) el.playState.textContent = data.playState || 'Idle';
            if (el.achievementStatus) el.achievementStatus.textContent = data.achievement || '';

            // 3. Mods
            if (el.modsContainer && data.mods) this.renderMods(data.mods);

            const state = data.playState || 'Idle';
            const isPlaying = state === 'Playing' || state === 'Results' || state === 'Replay' || state === 'Paused';
            const isRealPlaying = state === 'Playing';

            // 4. Hit Counts extraction
            const hits = data.hitCounts || {};
            const h300 = hits.count300 ?? 0;
            const h100 = hits.count100 ?? 0;
            const h50 = hits.count50 ?? 0;
            const hMiss = hits.misses ?? 0;
            const totalHits = h300 + h100 + h50 + hMiss;

            // Replay Suppression: Forced reset until first object passes
            const suppressStats = (state === 'Replay' && totalHits === 0);

            // 5. Proactive Reset when starting a new play
            if (isPlaying && this.lastState !== state && (state === 'Playing' || state === 'Replay')) {
                if (this.slots.accuracy) this.slots.accuracy.setValue(1.0);
                if (this.slots.combo) this.slots.combo.setValue(0);
                if (this.slots.score) this.slots.score.setValue(0);
                if (this.slots.pp) this.slots.pp.setValue(0);
                if (this.slots.count300) this.slots.count300.setValue(0);
                if (this.slots.count100) this.slots.count100.setValue(0);
                if (this.slots.count50) this.slots.count50.setValue(0);
                if (this.slots.countMiss) this.slots.countMiss.setValue(0);
            }
            this.lastState = state;

            // 6. Accuracy
            const acc = (data.accuracy != null && !suppressStats) ? data.accuracy : 1.0;
            if (this.slots.accuracy) this.slots.accuracy.setValue(acc);
            
            // 7. Combo
            if (this.slots.combo) {
                let comboVal = 0;
                if (state === 'Results') {
                    comboVal = data.maxCombo || 0; 
                } else if (isPlaying && !suppressStats) {
                    comboVal = data.combo || 0;
                } else if (!isPlaying) {
                    comboVal = data.mapMaxCombo || 0;
                }
                this.slots.combo.setValue(comboVal);
            }

            // 8. Cache Map Max Combo from Song Select or map change (CRITICAL for fire effect)
            if (data.mapMaxCombo > 0) {
                if (this.cachedMapMaxCombo <= 0 || (this.cachedMapMaxCombo !== data.mapMaxCombo && state !== 'Playing')) {
                    console.log(`[Live] 🎯 Map Max Combo Cached: ${data.mapMaxCombo}`);
                    this.cachedMapMaxCombo = data.mapMaxCombo;
                }
            }

            // 9. Score
            if (this.slots.score) {
                const scoreVal = (isPlaying && !suppressStats) ? (data.score || 0) : (isPlaying ? 0 : (data.score || 0));
                this.slots.score.setValue(scoreVal);
            }

            // 10. PP
            if (this.slots.pp) {
                let ppVal = 0;
                if (state === 'Results') {
                    ppVal = data.pp || 0;
                } else if (isPlaying && !suppressStats) {
                    ppVal = data.pp || 0;
                } else if (isPlaying && suppressStats) {
                    ppVal = 0;
                } else {
                    ppVal = data.ppIfFc || 0;
                }
                this.slots.pp.setValue(ppVal);
                
                // PP Tier Detection (Stable even in results)
                this.updatePPTier(ppVal, isPlaying || state === 'Results');
            }
            
            // 11. Fire Effect Detection (after combo and max combo cache)
            if (isRealPlaying && !suppressStats) {
                this.updateFireEffect(data);
            } else {
                this.clearFireEffect();
            }
            
            // 12. Star Rating
            if (this.slots.stars) {
                const sr = data.stars || 0;
                this.slots.stars.setValue(sr);
                
                // Dynamic Star Badge Styling
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

            // 13. BPM
            if (this.slots.bpm) this.slots.bpm.setValue(data.bpm || 0);

            // 14. Grade
            if (el.grade) {
                const grade = (isPlaying && !suppressStats) ? (data.grade || '—') : 'SS';
                if (el.grade.textContent !== grade) {
                    el.grade.textContent = grade;
                    el.grade.className = `grade ${grade}`;
                }
            }

            // 15. Dashboard Attributes (CS, AR, OD, HP)
            if (el.cs) el.cs.textContent = (data.cs || 0).toFixed(1);
            if (el.ar) el.ar.textContent = (data.ar || 0).toFixed(1);
            if (el.od) el.od.textContent = (data.od || 0).toFixed(1);
            if (el.hp) el.hp.textContent = (data.hp || 0).toFixed(1);

            // 16. Hit Counts
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

            // 17. Map Time
            if (el.mapTime) {
                let time = isPlaying ? (data.currentTime || 0) : (data.totalTime || 0);
                const mins = Math.floor(time / 60);
                const secs = Math.floor(time % 60);
                el.mapTime.textContent = `${mins}:${secs.toString().padStart(2, '0')}`;
            }

            if (window.app) window.app.setPlaying(isPlaying);
        } catch (err) {
            console.error('[Live] Error in update loop:', err, data);
        }
    }