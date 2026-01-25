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
        // Toggle switches
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

    async importLazer() {
        const btn = document.getElementById('importLazerBtn');
        const originalText = btn.textContent;
        btn.textContent = 'Importing...';
        btn.disabled = true;

        try {
            const result = await window.api.importLazer();
            if (result.success) {
                alert(`Import successful! Added: ${result.count}, Skipped: ${result.skipped}`);
                if (window.analyticsModule) window.analyticsModule.refresh(true);
                if (window.historyModule) window.historyModule.refresh();
            } else {
                alert('Import failed: ' + result.message);
            }
        } catch (error) {
            console.error(error);
            alert('Import failed: ' + error.message);
        } finally {
            btn.textContent = originalText;
            btn.disabled = false;
        }
    }

    async importStable() {
        const aliases = prompt("Enter additional guest aliases to import (comma separated):", "");
        const btn = document.getElementById('importStableBtn');
        const originalText = btn.textContent;
        btn.textContent = 'Importing...';
        btn.disabled = true;

        try {
            const result = await window.api.fetch(`/api/import/stable?aliases=${encodeURIComponent(aliases || '')}`, { method: 'POST' });
            if (result.success) {
                alert(`Imported ${result.count} scores from Stable!`);
                if (window.analyticsModule) window.analyticsModule.refresh(true);
                if (window.historyModule) window.historyModule.refresh();
            } else {
                alert('Import failed: ' + result.message);
            }
        } catch (error) {
            console.error(error);
            alert('Import failed: ' + error.message);
        } finally {
            btn.textContent = originalText;
            btn.disabled = false;
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
            if (window.historyModule) window.historyModule.refresh();
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
