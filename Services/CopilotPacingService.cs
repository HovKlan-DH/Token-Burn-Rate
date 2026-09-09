using System;
using TokenBurnRate.Models;

namespace TokenBurnRate.Services;

/// <summary>
/// Derives a "how much can I spend today" view from GitHub's point-in-time credit balance.
///
/// GitHub only reports what is left right now, so spend-per-day has to be reconstructed:
/// the balance at the first launch of the day is recorded, and today's usage is the
/// difference between that opening balance and the current one.
///
/// The daily allowance is the remaining balance divided by the business days left in the
/// period, so it is recalculated every day: underspending today raises tomorrow's
/// allowance, overspending lowers it.
/// </summary>
public sealed class CopilotPacingService
{
    /// <summary>
    /// Picks the bucket that actually meters spend. Business and Enterprise seats meter
    /// premium interactions while completions and chat are unlimited; a personal plan
    /// meters completions. The bucket with a real entitlement wins, preferring premium.
    /// </summary>
    public static CopilotQuota? SelectCreditBucket(CopilotStatus status)
    {
        CopilotQuota? best = null;
        foreach (var q in status.Quotas)
        {
            if (q.Unlimited || q.Entitlement <= 0) continue;
            if (q.Label == "PREMIUM") return q;          // the metered one on paid org seats
            best ??= q;
        }
        return best;
    }

    /// <summary>
    /// Builds the Day / Week / Month pacing bars, or null when no bucket meters credits
    /// (for example a plan where everything is unlimited).
    /// </summary>
    public CopilotPacing? Build(CopilotStatus status, DateTime now)
    {
        var bucket = SelectCreditBucket(status);
        if (bucket is null) return null;

        var remaining = Math.Max(0, bucket.Entitlement - bucket.Used);
        var state = LoadOrRoll(remaining, now);

        var daysLeftInPeriod = BusinessDays.RemainingInPeriod(now, status.ResetDate);
        var daysLeftInWeek = BusinessDays.RemainingInWeek(now, status.ResetDate);

        // Allowance per business day, from what is left right now.
        var perDay = remaining / daysLeftInPeriod;

        // Usage is the drop from the opening balance; never negative, since a period reset
        // can raise the balance above where the day started.
        var usedToday = Math.Max(0, state.DayOpening - remaining);
        var usedThisWeek = Math.Max(0, state.WeekOpening - remaining);

        // The week's target covers the days still ahead of us plus those already spent in
        // it, so the bar measures the whole week rather than only its remainder.
        var weekBudget = perDay * daysLeftInWeek + usedThisWeek;

        return new CopilotPacing
        {
            Entitlement = bucket.Entitlement,
            BusinessDaysLeft = daysLeftInPeriod,
            PerDayAllowance = perDay,
            UsedToday = usedToday,
            UsedThisWeek = usedThisWeek,
            WeekBudget = weekBudget,
            UsedThisPeriod = bucket.Used,
        };
    }

    /// <summary>
    /// Reads the stored opening balances, starting a new day or week when the date has
    /// rolled over. Only the current day and week are tracked, so a day the app is never
    /// opened simply starts fresh at the next launch.
    /// </summary>
    private static AppState.PacingState LoadOrRoll(double remaining, DateTime now)
    {
        var today = now.Date.ToString("yyyy-MM-dd");
        var weekStart = BusinessDays.StartOfWeek(now).ToString("yyyy-MM-dd");

        var state = AppState.Load().Pacing ?? new AppState.PacingState();
        var dirty = false;

        if (state.Day != today)
        {
            state.Day = today;
            state.DayOpening = remaining;
            dirty = true;
        }

        if (state.WeekStart != weekStart)
        {
            state.WeekStart = weekStart;
            state.WeekOpening = remaining;
            dirty = true;
        }

        // A balance above the recorded opening means the quota reset; re-anchor to it.
        if (remaining > state.DayOpening)
        {
            state.DayOpening = remaining;
            dirty = true;
        }
        if (remaining > state.WeekOpening)
        {
            state.WeekOpening = remaining;
            dirty = true;
        }

        if (dirty) AppState.Update(a => a.Pacing = state);
        return state;
    }

}
