/**
 * OSU!GRIND Settings Tab Module
 * Handles settings display and persistence
 */

class SettingsModule {
    constructor() {
        this.settings = {};
    }

    async init() {
        this.setupEventListeners();
        await this.loadSettings();
        console.log('[Settings] Module initialized');
    }

    setupEventListeners() {
        // Path browse buttons
        document.querySelectorAll('.browse-btn').forEach(btn => {
            btn.addEventListener('click', async () => {
                const pathType = btn.dataset.path;
                await this.browsePath(pathType);
            });
        });

        // Toggle switches
        document.getElementById('comboSounds')?.addEventListener('change', (e) => {
            this.settings.comboSoundsEnabled = e.target.checked;
            this.saveSettings();
        });

        document.getElementById('achievementSounds')?.addEventListener('change', (e) => {
            this.settings.achievementSoundsEnabled = e.target.checked;
            this.saveSettings();
        });

        document.getElementById('passSound')?.addEventListener('change', (e) => {
            this.settings.passSoundEnabled = e.target.checked;
            this.saveSettings();
        });

        document.getElementById('failSound')?.addEventListener('change', (e) => {
            this.settings.failSoundEnabled = e.target.checked;
            this.saveSettings();
        });

        document.getElementById('debugLogging')?.addEventListener('change', (e) => {
            this.settings.debugLoggingEnabled = e.target.checked;
            this.saveSettings();
        });

        // Action buttons
        document.getElementById('importLazerBtn')?.addEventListener('click', () => this.importLazer());
        document.getElementById('importStableBtn')?.addEventListener('click', () => this.importStable());
        document.getElementById('exportCsvBtn')?.addEventListener('click', () => this.exportCsv());
        
        // Delete buttons
        document.getElementById('deleteScoresBtn')?.addEventListener('click', () => this.deleteAllScores());
        document.getElementById('deleteBeatmapsBtn')?.addEventListener('click', () => this.deleteAllBeatmaps());
    }

    async loadSettings() {
        try {
            this.settings = await window.api.getSettings();
            this.updateUI();
        } catch (error) {
            console.error('[Settings] Failed to load:', error);
        }
    }

    updateUI() {
        const s = this.settings;

        document.getElementById('lazerPath').value = s.lazerPath || '';
        document.getElementById('stablePath').value = s.stablePath || '';


        document.getElementById('comboSounds').checked = s.comboSoundsEnabled || false;
        document.getElementById('achievementSounds').checked = s.achievementSoundsEnabled || false;
        document.getElementById('passSound').checked = s.passSoundEnabled || false;
        document.getElementById('failSound').checked = s.failSoundEnabled || false;
        document.getElementById('debugLogging').checked = s.debugLoggingEnabled || false;
    }

    async saveSettings() {
        try {
            await window.api.saveSettings(this.settings);
        } catch (error) {
            console.error('[Settings] Failed to save:', error);
        }
    }

    async browsePath(type) {
        try {
            const result = await window.api.browseFolder(type);
            if (result.path) {
                switch (type) {
                    case 'lazer':
                        this.settings.lazerPath = result.path;
                        document.getElementById('lazerPath').value = result.path;
                        break;
                    case 'stable':
                        this.settings.stablePath = result.path;
                        document.getElementById('stablePath').value = result.path;
                        break;

                }
                await this.saveSettings();
            }
        } catch (error) {
            console.error('[Settings] Browse failed:', error);
        }
    }

    async importLazer() {
        const btn = document.getElementById('importLazerBtn');
        btn.disabled = true;
        btn.textContent = 'Importing...';

        try {
            const result = await window.api.importLazer();
            alert(`Imported ${result.count} scores from Lazer!`);
        } catch (error) {
            alert('Import failed: ' + error.message);
        } finally {
            btn.disabled = false;
            btn.textContent = 'Import Lazer Scores';
        }
    }

    async importStable() {
        const btn = document.getElementById('importStableBtn');
        btn.disabled = true;
        btn.textContent = 'Importing...';

        try {
            const result = await window.api.importStable();
            alert(`Imported ${result.count} scores from Stable!`);
        } catch (error) {
            alert('Import failed: ' + error.message);
        } finally {
            btn.disabled = false;
            btn.textContent = 'Import Stable Scores';
        }
    }

    async exportCsv() {
        window.api.exportCsv();
    }

    async deleteAllScores() {
        if (!confirm('Are you SURE you want to delete ALL score history? This cannot be undone.')) return;

        try {
            await window.api.deleteAllScores();
            alert('All scores deleted successfully.');
        } catch (error) {
            alert('Failed to delete scores: ' + error.message);
        }
    }

    async deleteAllBeatmaps() {
        if (!confirm('Are you SURE you want to delete ALL beatmap cache? This cannot be undone.')) return;

        try {
            await window.api.deleteAllBeatmaps();
            alert('All beatmaps deleted successfully.');
        } catch (error) {
            alert('Failed to delete beatmaps: ' + error.message);
        }
    }
}

window.settingsModule = new SettingsModule();
