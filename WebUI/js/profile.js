/**
 * OSU!GRIND Profile Overlay Module
 * Handles profile display and OAuth
 */

class ProfileModule {
    constructor() {
        this.isLoggedIn = false;
        this.profile = null;
    }

    init() {
        this.setupEventListeners();
        this.loadProfile();
        console.log('[Profile] Module initialized');
    }

    setupEventListeners() {
        document.getElementById('closeProfile')?.addEventListener('click', () => this.hide());
        document.getElementById('loginBtn')?.addEventListener('click', () => this.login());
        document.getElementById('logoutBtn')?.addEventListener('click', () => this.logout());

        // Close on backdrop click
        document.querySelector('#profileOverlay .overlay-backdrop')?.addEventListener('click', () => this.hide());

        // Refresh on window focus (when coming back from browser)
        window.addEventListener('focus', () => {
            console.log('[Profile] Window focused, refreshing profile...');
            this.loadProfile();
        });

        // Poll every 5 seconds just in case
        setInterval(() => this.loadProfile(), 5000);
    }

    async loadProfile() {
        try {
            const data = await window.api.getProfile();
            this.profile = data;
            this.isLoggedIn = data.isLoggedIn || false;
            this.updateHeaderUI();

            // Update overlay if it's open
            const overlay = document.getElementById('profileOverlay');
            if (overlay && overlay.style.display !== 'none') {
                this.updateOverlayUI();
            }
        } catch (error) {
            console.error('[Profile] Failed to load:', error);
        }
    }

    updateHeaderUI() {
        const avatarPlaceholder = document.getElementById('avatarPlaceholder');
        const avatarImg = document.getElementById('avatarImg');
        const profileName = document.getElementById('profileName');
        const placeholderUrl = 'https://osu.ppy.sh/images/layout/avatar-guest.png';

        if (this.isLoggedIn && this.profile) {
            avatarPlaceholder.style.display = 'none';
            avatarImg.src = this.profile.avatarUrl || placeholderUrl;
            avatarImg.style.display = 'block';
            profileName.textContent = this.profile.username || 'User';
        } else {
            avatarPlaceholder.style.display = 'flex';
            avatarImg.style.display = 'none';
            profileName.textContent = 'Login with osu!';
        }
    }

    show() {
        const overlay = document.getElementById('profileOverlay');
        if (!overlay) return;

        overlay.style.display = 'flex';
        this.updateOverlayUI();
    }

    hide() {
        const overlay = document.getElementById('profileOverlay');
        if (overlay) overlay.style.display = 'none';
    }

    updateOverlayUI() {
        const p = this.profile || {};
        const placeholderUrl = 'https://osu.ppy.sh/images/layout/avatar-guest.png';

        // Background
        const bg = document.getElementById('profileBg');
        if (bg && p.coverUrl) {
            bg.style.backgroundImage = `url('${p.coverUrl}')`;
        }

        // Avatar
        const avatar = document.getElementById('profileAvatar');
        if (avatar) avatar.src = p.avatarUrl || placeholderUrl;

        // Username & Rank
        document.getElementById('profileUsername').textContent = p.username || 'Not logged in';
        document.getElementById('profileRank').innerHTML = p.globalRank
            ? `Global <strong>#${p.globalRank.toLocaleString()}</strong> | Country <strong>#${(p.countryRank || 0).toLocaleString()}</strong>`
            : '';

        // Skill Badges
        this.renderSkillBadges(p.skills || []);

        // Ranking Cards
        this.renderRankingCards(p);

        // Insights
        this.renderInsights(p.insights || []);

        // Login/Logout buttons
        document.getElementById('loginBtn').style.display = this.isLoggedIn ? 'none' : 'block';
        document.getElementById('logoutBtn').style.display = this.isLoggedIn ? 'block' : 'none';
    }

    renderSkillBadges(skills) {
        const container = document.getElementById('skillBadges');
        if (!container) return;

        container.innerHTML = skills.map(skill => `
            <div class="skill-badge">
                <span class="badge-icon">${skill.icon || '⭐'}</span>
                <span class="badge-label">${skill.label}</span>
                <span class="badge-value">${skill.value}</span>
            </div>
        `).join('');
    }

    renderRankingCards(profile) {
        const container = document.getElementById('rankingCards');
        if (!container) return;

        const cards = [
            { label: 'PP', value: profile.pp || 0, color: 'neon-pink' },
            { label: 'Accuracy', value: `${(profile.accuracy || 0).toFixed(2)}%`, color: 'neon-green' },
            { label: 'Play Count', value: profile.playCount || 0, color: 'neon-cyan' },
            { label: 'Max Combo', value: `${(profile.maxCombo || 0).toLocaleString()}x`, color: 'neon-purple' }
        ];

        container.innerHTML = cards.map(card => `
            <div class="ranking-card">
                <div class="ranking-label">${card.label}</div>
                <div class="ranking-value ${card.color}">${typeof card.value === 'number' ? card.value.toLocaleString() : card.value}</div>
            </div>
        `).join('');
    }

    renderInsights(insights) {
        const container = document.getElementById('profileInsights');
        if (!container) return;

        container.innerHTML = insights.map(insight => `
            <div class="insight">
                <span class="insight-icon">${insight.icon || '💡'}</span>
                <span>${insight.text}</span>
            </div>
        `).join('');
    }

    async login() {
        try {
            console.log('[Profile] Login clicked, fetching auth URL...');
            // Get auth URL from backend
            const response = await fetch('/api/auth/login');
            const data = await response.json();
            console.log('[Profile] Auth response:', data);

            if (data.authUrl) {
                console.log('[Profile] Sending openAuth to C#:', data.authUrl);
                // Send to C# without JSON.stringify (matches other handlers in app.js)
                if (window.chrome?.webview) {
                    window.chrome.webview.postMessage({ action: 'openAuth', url: data.authUrl });
                } else {
                    // Fallback for non-WebView2 environments
                    window.open(data.authUrl, '_blank');
                }
            } else {
                console.error('[Profile] No authUrl in response');
            }
        } catch (error) {
            console.error('[Profile] Login failed:', error);
            alert('Login failed');
        }
    }

    async logout() {
        if (!confirm('Are you sure you want to logout?')) return;

        try {
            await window.api.logout();
            this.isLoggedIn = false;
            this.profile = null;
            this.updateHeaderUI();
            this.updateOverlayUI();
        } catch (error) {
            console.error('[Profile] Logout failed:', error);
        }
    }
}

window.profileModule = new ProfileModule();

