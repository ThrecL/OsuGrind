/**
 * OSU!GRIND Analytics Tab Module
 * Handles the unified chart and metrics display
 */

class AnalyticsModule {
    constructor() {
        this.mainChart = null;
        this.loaded = false;
        this.currentPeriod = 30;
        this.currentMetric = 'pp';
        this.rawData = null;
    }

    init() {
        this.setupEventListeners();
        console.log('[Analytics] Module initialized');
    }

    setupEventListeners() {
        // Period buttons
        document.querySelectorAll('.period-selector .p-btn').forEach(btn => {
            btn.addEventListener('click', (e) => {
                this.currentPeriod = parseInt(e.target.dataset.days);
                this.updateActiveButton('.period-selector', e.target);
                this.refresh(true);
            });
        });

        // Metric buttons
        document.querySelectorAll('.metric-selector .p-btn').forEach(btn => {
            btn.addEventListener('click', (e) => {
                this.currentMetric = e.target.dataset.metric;
                this.updateActiveButton('.metric-selector', e.target);
                if (this.rawData) this.renderMainChart();
            });
        });
    }

    updateActiveButton(selector, activeBtn) {
        // Handle cases where child element (text) might be clicked
        const btn = activeBtn.closest('.p-btn');
        if (!btn) return;
        
        document.querySelectorAll(`${selector} .p-btn`).forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
    }

    async refresh(force = false) {
        if (this.loaded && !force) return;

        try {
            const data = await window.api.fetch(`/api/analytics?days=${this.currentPeriod}`);
            this.rawData = data;
            this.updateStats(data);
            this.renderMainChart();
            this.loaded = true;
        } catch (error) {
            console.error('[Analytics] Failed to load:', error);
        }
    }

    updateStats(data) {
        // Mini cards
        document.getElementById('totalPlays').textContent = this.formatNumber(data.totalPlays || 0);
        document.getElementById('totalTime').textContent = this.formatDuration(data.totalMinutes || 0);
        document.getElementById('avgAcc').textContent = `${((data.avgAccuracy || 0) * 100).toFixed(2)}%`;
        document.getElementById('avgPP').textContent = Math.round(data.avgPP || 0);

        // Performance & Form
        const perfMatchEl = document.getElementById('perfMatch');
        if (perfMatchEl) perfMatchEl.textContent = `${(data.perfMatch || 0).toFixed(1)}%`;

        const formEl = document.getElementById('currentForm');
        if (formEl) {
            formEl.textContent = data.currentForm || 'Stable';
            formEl.className = 'form-value ' + (data.currentForm || 'stable').toLowerCase();
        }

        const mentalityValEl = document.getElementById('mentalityValue');
        const mentalityFillEl = document.getElementById('mentalityFill');
        if (mentalityValEl && mentalityFillEl) {
            const m = Math.round(data.mentality ?? 0);
            mentalityValEl.textContent = `${m}%`;
            mentalityFillEl.style.width = `${m}%`;
        }
    }

    renderMainChart() {
        const ctx = document.getElementById('mainAnalyticsChart')?.getContext('2d');
        if (!ctx || !this.rawData) return;

        if (this.mainChart) {
            this.mainChart.destroy();
        }

        const dailyData = this.rawData.dailyActivity || [];
        const labels = dailyData.map(d => d.date);
        
        let datasets = [];

        switch (this.currentMetric) {
            case 'pp':
                datasets.push({
                    label: 'Avg PP',
                    data: dailyData.map(d => d.avgPP),
                    borderColor: '#BB88FF',
                    backgroundColor: 'rgba(187, 136, 255, 0.1)',
                    fill: true,
                    tension: 0.4
                });
                break;
            case 'performance':
                datasets.push({
                    label: 'Performance Match (%)',
                    data: this.rawData.dailyPerformance.map(d => d.match),
                    borderColor: '#FF66AB',
                    backgroundColor: 'rgba(255, 102, 171, 0.1)',
                    fill: true,
                    tension: 0.4
                });
                break;
            case 'activity':
                datasets.push({
                    label: 'Plays',
                    data: dailyData.map(d => d.plays),
                    borderColor: '#00F2FE',
                    backgroundColor: 'rgba(0, 242, 254, 0.1)',
                    fill: true,
                    tension: 0.4,
                    yAxisID: 'y'
                });
                datasets.push({
                    label: 'Minutes',
                    data: dailyData.map(d => d.minutes),
                    borderColor: '#f2fe00',
                    backgroundColor: 'transparent',
                    borderDash: [5, 5],
                    tension: 0.4,
                    yAxisID: 'y1'
                });
                break;
            case 'accuracy':
                datasets.push({
                    label: 'Accuracy (%)',
                    data: dailyData.map(d => d.avgAcc), 
                    borderColor: '#00FF88',
                    backgroundColor: 'rgba(0, 255, 136, 0.1)',
                    fill: true,
                    tension: 0.4
                });
                break;
        }

        this.mainChart = new Chart(ctx, {
            type: 'line',
            data: {
                labels: labels,
                datasets: datasets
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { 
                        display: this.currentMetric === 'activity',
                        position: 'top',
                        align: 'end',
                        labels: { color: '#888', boxWidth: 12, font: { size: 10 } }
                    }
                },
                scales: {
                    x: {
                        grid: { color: 'rgba(255, 255, 255, 0.05)' },
                        ticks: { color: '#666', maxTicksLimit: 10 }
                    },
                    y: {
                        grid: { color: 'rgba(255, 255, 255, 0.05)' },
                        ticks: { color: '#888' }
                    },
                    y1: {
                        display: this.currentMetric === 'activity',
                        position: 'right',
                        grid: { drawOnChartArea: false },
                        ticks: { color: '#888' }
                    }
                }
            }
        });
    }

    formatNumber(num) {
        if (num >= 1000000) return (num / 1000000).toFixed(1) + 'M';
        if (num >= 1000) return (num / 1000).toFixed(1) + 'K';
        return num.toString();
    }

    formatDuration(minutes) {
        if (minutes >= 60) {
            const hours = Math.floor(minutes / 60);
            return `${hours}h`;
        }
        return `${Math.round(minutes)}m`;
    }
}

window.analyticsModule = new AnalyticsModule();
