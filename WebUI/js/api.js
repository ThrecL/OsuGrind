/**
 * OSU!GRIND API Client
 * Handles all communication with the C# backend
 */

const API_BASE = '';  // Same origin
const WS_URL = `ws://${window.location.host}/ws/live`;

class OsuGrindAPI {
    constructor() {
        this.ws = null;
        this.wsReconnectTimer = null;
        this.liveCallbacks = [];
        this.logCallbacks = [];
    }

    // ═══════════════════════════════════════════════════════════════════════
    // WebSocket for Live Data
    // ═══════════════════════════════════════════════════════════════════════

    connectLive() {
        if (this.ws && this.ws.readyState === WebSocket.OPEN) return;

        this.ws = new WebSocket(WS_URL);

        this.ws.onopen = () => {
            console.log('[WS] Connected');
            this.updateConnectionStatus('connecting'); // Show yellow light initially
            if (this.wsReconnectTimer) {
                clearTimeout(this.wsReconnectTimer);
                this.wsReconnectTimer = null;
            }
        };


        this.ws.onmessage = (event) => {
            try {
                const payload = JSON.parse(event.data);
                if (payload.type === 'live') {
                    const data = payload.data;
                    if (data.connectionStatus) {
                        this.updateConnectionStatus(data.connectionStatus, data.gameName);
                    }
                    this.liveCallbacks.forEach(cb => cb(data));
                } else if (payload.type === 'log') {
                    this.liveCallbacks.forEach(cb => cb({ type: 'log', message: payload.message, level: payload.level }));
                } else if (payload.type === 'refresh') {
                    this.liveCallbacks.forEach(cb => cb({ type: 'refresh' }));
                } else {
                    // Backwards compatibility for old raw data if any
                    this.liveCallbacks.forEach(cb => cb(payload));
                }
            } catch (e) {
                console.error('[WS] Parse error:', e);
            }
        };

        this.ws.onclose = () => {
            console.log('[WS] Disconnected');
            this.updateConnectionStatus('disconnected');
            this.scheduleReconnect();
        };

        this.ws.onerror = (error) => {
            console.error('[WS] Error:', error);
            this.updateConnectionStatus('error');
        };
    }

    scheduleReconnect() {
        if (this.wsReconnectTimer) return;
        this.wsReconnectTimer = setTimeout(() => {
            this.wsReconnectTimer = null;
            this.updateConnectionStatus('connecting');
            this.connectLive();
        }, 3000);
    }

    onLiveData(callback) {
        this.liveCallbacks.push(callback);
    }

    onLog(callback) {
        this.logCallbacks.push(callback);
    }

    updateConnectionStatus(status, gameName = '') {
        const dot = document.querySelector('.status-dot');
        const text = document.querySelector('.status-text');

        dot?.classList.remove('connected', 'connecting');

        // Logic: if backend is connected but no client detected, keep light yellow (connecting)
        if (status === 'connected' && (gameName === 'Searching' || !gameName)) {
            dot?.classList.add('connecting');
            if (text) text.textContent = 'Connecting…';
            return;
        }

        switch (status) {
            case 'connected':
                dot?.classList.add('connected');
                if (text) text.textContent = gameName || 'Connected';
                break;
            case 'connecting':
                dot?.classList.add('connecting');
                if (text) text.textContent = 'Connecting…';
                break;
            case 'disconnected':
            default:
                if (text) text.textContent = 'Disconnected';
                break;
        }
    }


    // ═══════════════════════════════════════════════════════════════════════
    // REST API Methods
    // ═══════════════════════════════════════════════════════════════════════

    async fetch(endpoint, options = {}) {
        try {
            const response = await fetch(`${API_BASE}${endpoint}`, {
                ...options,
                headers: {
                    'Content-Type': 'application/json',
                    ...options.headers
                }
            });
            const data = await response.json();
            if (!response.ok) {
                const errorMsg = data.message || data.error || `HTTP ${response.status}`;
                throw new Error(errorMsg);
            }
            return data;
        } catch (error) {
            if (error.message.startsWith('HTTP')) {
                 console.error(`[API] ${endpoint} failed:`, error);
            }
            throw error;
        }
    }


    // History
    async getHistoryForDate(date) {
        return this.fetch(`/api/history?date=${date}`);
    }

    async getRecentHistory(limit = 50) {
        return this.fetch(`/api/history/recent?limit=${limit}`);
    }

    async getMonthPlays(year, month) {
        return this.fetch(`/api/history/month?year=${year}&month=${month}`);
    }

    // Analytics
    async getAnalytics() {
        return this.fetch('/api/analytics');
    }

    // Profile
    async getProfile() {
        return this.fetch('/api/profile');
    }

    async getTopPlays() {
        return this.fetch('/api/profile/top');
    }

    // Settings
    async getSettings() {
        return this.fetch('/api/settings');
    }

    async saveSettings(settings) {
        return this.fetch('/api/settings', {
            method: 'POST',
            body: JSON.stringify(settings)
        });
    }

    // Play Actions
    async deletePlay(id) {
        return this.fetch(`/api/play/${id}`, { method: 'DELETE' });
    }

    async updatePlayNotes(id, notes) {
        return this.fetch(`/api/play/${id}/notes`, {
            method: 'POST',
            body: JSON.stringify({ notes })
        });
    }

    async getRewindInfo(id) {
        return this.fetch(`/api/play/${id}/rewind`);
    }

    async decodeOsr(path) {
        return this.fetch(`/api/rewind/osr?path=${encodeURIComponent(path)}`);
    }

    async calculateRewindPp(data) {
        return this.fetch('/api/rewind/pp', {
            method: 'POST',
            body: JSON.stringify(data)
        });
    }

    async saveCursorOffsets(scoreId, offsets) {
        return this.fetch('/api/rewind/cursor-offsets', {
            method: 'POST',
            body: JSON.stringify({ scoreId, offsets })
        });
    }

    // Data Import
    async importLazer() {
        return this.fetch('/api/import/lazer', { method: 'POST' });
    }

    async importStable() {
        return this.fetch('/api/import/stable', { method: 'POST' });
    }

    async exportCsv() {
        window.location.href = '/api/export/csv';
    }

    async deleteZeroScores() {
        return this.fetch('/api/data/delete-zero', { method: 'POST' });
    }

    async deleteAllScores() {
        return this.fetch('/api/settings/delete-scores', { method: 'POST' });
    }

    async deleteAllBeatmaps() {
        return this.fetch('/api/settings/delete-beatmaps', { method: 'POST' });
    }

    // File Dialog (routed through backend)
    async browseFolder(type) {
        return this.fetch(`/api/browse/${type}`);
    }

    // OAuth
    async startLogin() {
        return this.fetch('/api/auth/login');
    }

    async logout() {
        return this.fetch('/api/auth/logout', { method: 'POST' });
    }
}

// Global instance
window.api = new OsuGrindAPI();
