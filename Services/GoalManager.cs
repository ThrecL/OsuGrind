using System;
using System.Threading.Tasks;

namespace OsuGrind.Services;

public static class GoalManager
{
    private static DateTime _lastGoalSoundDate = DateTime.MinValue;

    public static bool ShouldPlayGoalSound()
    {
        if (DateTime.Today == _lastGoalSoundDate.Date) return false;
        return true;
    }

    public static void RecordGoalSoundPlayed()
    {
        _lastGoalSoundDate = DateTime.Today;
    }

    public static bool IsGoalMet(TrackerDb.GoalProgress p)
    {
        int targetPlays = SettingsManager.Current.GoalPlays;
        int targetHits = SettingsManager.Current.GoalHits;
        double targetStars = SettingsManager.Current.GoalStars;
        int targetPP = SettingsManager.Current.GoalPP;

        if (targetPlays > 0 && p.Plays < targetPlays) return false;
        if (targetHits > 0 && p.Hits < targetHits) return false;
        if (targetStars > 0 && p.StarPlays < 1) return false;
        if (targetPP > 0 && p.TotalPP < targetPP) return false;
        
        // If no goals are set, we don't consider them met
        if (targetPlays == 0 && targetHits == 0 && targetStars == 0 && targetPP == 0) return false;
        
        return true;
    }
    
    public static async Task CheckAndPlayGoalSound(TrackerDb db, SoundPlayer soundPlayer)
    {
        if (!SettingsManager.Current.GoalSoundEnabled) return;
        if (!ShouldPlayGoalSound()) return;

        // Re-check after a short delay to ensure DB is updated and prevent race conditions
        await Task.Delay(5000);
        
        if (!ShouldPlayGoalSound()) return;

        var progress = await db.GetTodayGoalProgressAsync(SettingsManager.Current.GoalStars);
        if (IsGoalMet(progress))
        {
            RecordGoalSoundPlayed();
            DebugService.Log("[GoalManager] Goals completed! Playing streak.ogg", "Goals");
            soundPlayer.PlayStreak();
        }
    }

    public static async Task InitializeAsync(TrackerDb db)
    {
        var progress = await db.GetTodayGoalProgressAsync(SettingsManager.Current.GoalStars);
        if (IsGoalMet(progress))
        {
            RecordGoalSoundPlayed();
            DebugService.Log("[GoalManager] Goals already met today. Sound suppressed.", "Goals");
        }
    }
}
