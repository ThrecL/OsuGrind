# OsuGrind Analytics Formulas

This document outlines the mathematical models used by OsuGrind to track your performance, consistency, and mental state.

## 1. Mentality Score (0–100)
The Mentality score is a reactive indicator of your focus and session quality. It is calculated strictly using data from the **last 3 days**.

### Base Score
The base score is weighted as follows:
- **Pass Rate (50%)**: `(PassCount / TotalPlays) * 50`
- **Accuracy (40%)**: `AverageAccuracy * 40`
- **Density (10%)**: `Math.Min(1.0, TotalPlays / (HoursPlayed * 10 + 1))`

### Penalties & Multipliers
The base score is then subjected to dynamic multipliers:

#### Inactivity Decay
Mentality begins to decline if you don't play.
- **Starts after**: 12 hours of inactivity.
- **Rate**: -10% per day.
- **Formula**: `multiplier = 0.9 ^ (InactivityDays - 0.5)`

#### Performance Penalty
If your recent performance is significantly lower than your historical peak, Mentality takes a hit.
- **85% of Peak**: 0.8x Multiplier
- **70% of Peak**: 0.5x Multiplier (Massive Hit)
- **50% of Peak**: 0.2x Multiplier (Critical Hit)

#### Goal Penalty
If daily goals are enabled and you are behind schedule (after 12:00 PM local time).
- **Formula**: `0.6x Multiplier` if current progress is less than 70% of the expected progress for the current hour.

---

## 2. Peak Performance Match
Measures how your current session (or selected period) compares to your all-time potential.

- **Formula**: `(Average_PP_of_Period / Reference_PP) * 100`
- **Reference_PP**: The highest daily average PP recorded in your OsuGrind history.

---

## 3. Current Form
A trend indicator comparing short-term performance against long-term consistency.

- **Short-term Window**: 14 Days
- **Long-term Baseline**: 90 Days
- **Formula**: `(14d_Avg_PP / 90d_Avg_PP)`

| Ratio | Form State |
|-------|------------|
| > 1.10 | **Peak** |
| > 1.03 | **Improving** |
| 0.96 – 1.03 | **Stable** |
| < 0.96 | **Slumping** |
| < 0.90 | **Burnout** |

---

## 4. Unstable Rate (UR)
Calculated based on hit offsets from memory (Live) or replay (Analysis).

- **Formula**: `StandardDeviation(HitOffsets) * 10`
- **Note**: For speed-modifying mods (DT/HT), the offsets are normalized by the clock rate to ensure accurate comparisons.
