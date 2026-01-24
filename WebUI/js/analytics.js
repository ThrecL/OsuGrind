/**
 * OSU!GRIND Analytics Tab Module
 * Handles charts and analytics display
 */

class AnalyticsModule {
    constructor() {
        this.activityChart = null;
        this.performanceChart = null;
        this.loaded = false;
    }

    init() {
        console.log('[Analytics] Module initialized');
    }

    async refresh() {
        if (this.loaded) return;

        try {
            const data = await window.api.getAnalytics();
            this.updateStats(data);
            this.renderCharts(data);
            this.loaded = true;
        } catch (error) {
            console.error('[Analytics] Failed to load:', error);
        }
    }

    updateStats(data) {
        document.getElementById('perfMatch').textContent = `${(data.performanceMatch || 0).toFixed(1)}%`;
        document.getElementById('currentForm').textContent = data.currentForm || '—';

        // Update Mentality Bar if exists
        const mentalityFill = document.getElementById('mentalityFill');
        const mentalityValue = document.getElementById('mentalityValue');
        if (mentalityFill) mentalityFill.style.width = `${data.mentality || 0}%`;
        if (mentalityValue) mentalityValue.textContent = `${(data.mentality || 0).toFixed(1)}%`;

        // Skill insights
        const insightsContainer = document.getElementById('skillInsights');
        if (insightsContainer && data.insights) {
            insightsContainer.innerHTML = data.insights.map(insight =>
                `<div class="skill-insight">${insight}</div>`
            ).join('');
        }

        // Mini cards
        document.getElementById('totalPlays').textContent = this.formatNumber(data.totalPlays || 0);
        document.getElementById('totalTime').textContent = this.formatDuration(data.totalMinutes || 0);
        document.getElementById('avgAcc').textContent = `${(data.avgAccuracy * 100 || 0).toFixed(2)}%`;
        document.getElementById('avgPP').textContent = Math.round(data.avgPP || 0);
    }

    renderCharts(data) {
        this.renderActivityChart(data.dailyActivity || []);
        this.renderPerformanceChart(data.dailyPerformance || []);
    }

    renderActivityChart(dailyData) {
        const ctx = document.getElementById('activityChart')?.getContext('2d');
        if (!ctx) return;

        if (this.activityChart) {
            this.activityChart.destroy();
        }

        const labels = dailyData.map(d => d.date);
        const plays = dailyData.map(d => d.plays);
        const time = dailyData.map(d => d.minutes);

        this.activityChart = new Chart(ctx, {
            type: 'bar',
            data: {
                labels,
                datasets: [
                    {
                        label: 'Plays',
                        data: plays,
                        backgroundColor: 'rgba(187, 136, 255, 0.6)',
                        borderColor: '#BB88FF',
                        borderWidth: 1,
                        yAxisID: 'y'
                    },
                    {
                        label: 'Minutes',
                        data: time,
                        type: 'line',
                        borderColor: '#00FF88',
                        backgroundColor: 'transparent',
                        tension: 0.3,
                        yAxisID: 'y1'
                    }
                ]
            },
            options: this.getChartOptions('Plays', 'Minutes')
        });
    }

    renderPerformanceChart(dailyData) {
        const ctx = document.getElementById('performanceChart')?.getContext('2d');
        if (!ctx) return;

        if (this.performanceChart) {
            this.performanceChart.destroy();
        }

        const labels = dailyData.map(d => d.date);
        const values = dailyData.map(d => d.match);

        this.performanceChart = new Chart(ctx, {
            type: 'line',
            data: {
                labels,
                datasets: [{
                    label: 'Performance Match',
                    data: values,
                    borderColor: '#FF66AB',
                    backgroundColor: 'rgba(255, 102, 171, 0.1)',
                    fill: true,
                    tension: 0.3
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: false }
                },
                scales: {
                    x: {
                        grid: { color: '#222' },
                        ticks: { color: '#666' }
                    },
                    y: {
                        grid: { color: '#222' },
                        ticks: { color: '#666' },
                        min: 0,
                        max: 100
                    }
                }
            }
        });
    }

    getChartOptions(leftLabel, rightLabel) {
        return {
            responsive: true,
            maintainAspectRatio: false,
            interaction: {
                mode: 'index',
                intersect: false
            },
            plugins: {
                legend: {
                    display: true,
                    labels: { color: '#888' }
                }
            },
            scales: {
                x: {
                    grid: { color: '#222' },
                    ticks: { color: '#666' }
                },
                y: {
                    type: 'linear',
                    position: 'left',
                    grid: { color: '#222' },
                    ticks: { color: '#BB88FF' },
                    title: {
                        display: true,
                        text: leftLabel,
                        color: '#BB88FF'
                    }
                },
                y1: {
                    type: 'linear',
                    position: 'right',
                    grid: { drawOnChartArea: false },
                    ticks: { color: '#00FF88' },
                    title: {
                        display: true,
                        text: rightLabel,
                        color: '#00FF88'
                    }
                }
            }
        };
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
        return `${minutes}m`;
    }
}

window.analyticsModule = new AnalyticsModule();
