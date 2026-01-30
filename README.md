# OsuGrind
[![Release](https://img.shields.io/github/v/release/ThrecL/OsuGrind)](https://github.com/ThrecL/OsuGrind/releases)
[![Platform](https://img.shields.io/badge/platform-windows-blue.svg)](https://dotnet.microsoft.com/download/dotnet/9.0)

OsuGrind is a high-performance analytics companion and habit-building ecosystem for [osu!](https://osu.ppy.sh/). It combines real-time memory reading, advanced skill modeling, and global community integration to help players master their consistency.

Visit the official website and community leaderboard at **[osugrind.app](https://osugrind.app)**.

## 🛠 Key Features

### 📡 Unified Real-Time Tracking
Monitor your performance with ultra-low overhead.
- **Dual Support**: Seamlessly tracks both **osu!stable** and **osu!lazer** with zero configuration.
- **Live Stats**: Real-time PP (Current & FC), Unstable Rate (UR), Star Rating, and Hit Error Histograms.
- **Tiered Animations**: Visual flair that evolves as you climb the ranks (Rank #1-100).

### 📈 Advanced Skill Modeling (v1.0.2)
- Professional insights powered by sophisticated mathematical models.
- **Mentality Score**: A "Hard Mode" focus tracker that rewards long sessions and consistent skill over "retry-spamming."
- **Composite Performance Match**: A unified rating of your PP, Accuracy, and UR compared to your **Top 5 Best Days**.
- **Tapping Distribution**: Deep analysis of finger dominance and key press intervals.

### 🌐 Global Profiles & Community
Sync your progress to the cloud and compare with the world.
- **Online Profiles**: High-fidelity personal profile pages with interactive historical charts.
- **Community Leaderboard**: Track global rankings based on play count, average PP, and consistency streaks.
- **Automatic Sync**: Intelligent background synchronization to Cloudflare D1 ensures your profile is always up-to-date.

### 🎥 Integrated Replay & Hit Analysis
Deep-dive into your misses without leaving the application.
- **Rewind Integration**: Bundled WebGL replay viewer for frame-by-frame analysis.
- **Spatial Mapping**: Visualize exact aim and timing offsets for every hit object.
- **Fail Detection**: Instant notification for failed maps with no available replay data.

## 🚀 Technical Architecture
OsuGrind is built for performance and reliability:
- **Core**: .NET 9.0 Windows (WPF + WinForms)
- **UI**: Hybrid architecture using Microsoft.Web.WebView2 (Chromium-based) for a modern, hardware-accelerated frontend.
- **Native**: `rosu-pp` calculation wrapped in a native Rust DLL for millisecond-accurate PP data.
- **Backend**: Cloudflare Workers + D1 (SQL) + KV for global stats and profile hosting.

## 📥 Installation

1. Download the latest release (v1.0.2) from the [Releases](https://github.com/ThrecL/OsuGrind/releases) page.
2. Extract the archive to any folder.
3. Run `OsuGrind.exe`.
4. **Settings**: Connect your osu! account via OAuth to enable global profiles and banner synchronization.

## 🧪 Analytics Formulas
For a detailed mathematical breakdown of our scoring systems, visit the [Formulas Documentation](FORMULAS.md).

## 💖 Support
OsuGrind is developed with passion by **3cL**. If you'd like to support the project:
[**Support on Ko-fi**](https://ko-fi.com/nagakiba)

## 📜 Credits
OsuGrind stands on the shoulders of these incredible projects:
- [**Rewind**](https://github.com/abstrakt8) - Core WebGL replay engine.
- [**osuMissAnalyzer**](https://github.com/ThereGoesMySanity) - Replay hit analysis logic.
- [**tosu**](https://github.com/tosuapp) - Foundational memory research.
- [**rosu-pp**](https://github.com/MaxOhn) - High-performance PP calculation.

---
*Created by [3cL](https://osu.ppy.sh/users/19941927/osu)*
