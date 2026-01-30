# OsuGrind Analytics Formulas (v1.0.2)

This document outlines the mathematical models used by OsuGrind to track your performance, consistency, and mental state.

## 1. Mentality Score (0–100) - Hard Mode
The Mentality score is a reactive indicator of your focus and session quality. It is calculated strictly using data from the **last 3 days**. The v1.0.2 "Hard Mode" update prioritizes deep focus and consistency over raw play count.

### Base Score
The base score is weighted as follows:
- **Resilience (20%)**: `(PassCount / TotalPlays) * 100`. Measures your ability to finish what you start.
- **Focus (40%)**: `Math.Clamp((AvgDurationMs / 180000.0) * 100, 0, 100)`. The "Gold Standard" for focus is now **3 minutes** per play. Short retries heavily penalize this score.
- **Skill Alignment (40%)**: `(CurrentAvgPP / ReferencePeakPP) * 100`. Measures how consistently you are playing near your top-tier skill level.

### Penalties & Multipliers
The base score is then subjected to dynamic environmental factors:

#### UR Penalty (Consistency)
Poor timing consistency suggests mental fatigue or tilt.
- **UR > 120**: 0.8x Multiplier
- **UR > 90**: 0.9x Multiplier

#### Inactivity Decay
Mentality declines if you stop training.
- **Starts after**: 12 hours of inactivity.
- **Rate**: -8% per day.
- **Formula**: `multiplier = 0.92 ^ (InactivityDays - 0.5)`

#### Fatigue Penalty
Overtraining leads to diminishing returns and tilt.
- **> 3 Hours Played (Recent)**: 0.85x Multiplier
- **> 6 Hours Played (Recent)**: 0.60x Multiplier

#### Goal Progress Bonus
- **Ahead of Schedule**: 1.05x Bonus if you are beating your hourly target.
- **Behind Schedule**: 0.70x Penalty if you are significantly behind after 2:00 PM.

---

## 2. Peak Performance Match (Composite Rating)
Unlike a standard PP tracker, this metric measures your **overall skill execution** relative to your peak.

- **Formula**: `(PP_Factor * 0.6) + (Acc_Factor * 0.3) + (Consistency_Factor * 0.1)`
- **PP Factor**: Current Avg PP / Average of your **Top 5 Best Days**.
- **Acc Factor**: Current Avg Accuracy / Average Accuracy of your Top 5 Days.
- **Consistency Factor**: Target UR (Best 5 days) / Current UR (capped at 1.2x).

This ensures that a "high PP" day with terrible accuracy or shaky UR results in a lower Performance Match % than a clean, consistent session.

---

## 3. Current Form
A trend indicator comparing your performance over the last **14 days** against your **90-day baseline**.

| Ratio | Form State | Description |
|-------|------------|-------------|
| > 1.05 | **PEAK** | Playing at your absolute highest potential. |
| > 0.96 | **GREAT** | Elite consistency and near-peak performance. |
| 0.88 – 0.96 | **STABLE** | Your reliable baseline skill level. |
| 0.75 – 0.88 | **SLUMPING** | Noticeable drop in performance; potential tilt. |
| < 0.75 | **BURNOUT** | Significant loss of skill/focus; rest is recommended. |

---

## 4. Unstable Rate (UR)
Calculated based on hit offsets from memory (Live) or replay (Analysis).

- **Formula**: `StandardDeviation(HitOffsets) * 10`
- **Normalization**: For speed-modifying mods (DT/HT), offsets are normalized by the clock rate to ensure accurate skill comparisons across different speeds.
