using System;

namespace TokenBurnRate.Services;

/// <summary>
/// Business-day arithmetic for pacing credit spend. A business day is one of the first
/// <paramref name="workDaysPerWeek"/> days of the week counting from Monday - so 5 (the
/// default) means Monday-Friday, 6 extends into Saturday, and so on through 7. The count is
/// user-configurable via the context menu's "Workdays in a week" and persisted as
/// AppState.WorkDaysPerWeek.
///
/// Public holidays are not modelled: they vary by country and company, and treating one as
/// a working day only makes the daily budget slightly conservative.
/// </summary>
public static class BusinessDays
{
    public const int DefaultWorkDaysPerWeek = 5;
    public const int MinWorkDaysPerWeek = 1;
    public const int MaxWorkDaysPerWeek = 7;

    public static bool IsBusinessDay(DateTime d, int workDaysPerWeek)
    {
        var daysSinceMonday = ((int)d.DayOfWeek + 6) % 7;   // Monday = 0 ... Sunday = 6
        return daysSinceMonday < workDaysPerWeek;
    }

    /// <summary>
    /// Business days from <paramref name="from"/> through <paramref name="to"/> inclusive.
    /// Returns 0 when the range is empty or inverted.
    /// </summary>
    public static int CountInclusive(DateTime from, DateTime to, int workDaysPerWeek)
    {
        from = from.Date;
        to = to.Date;
        if (to < from) return 0;

        var count = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
            if (IsBusinessDay(d, workDaysPerWeek)) count++;
        return count;
    }

    /// <summary>
    /// Business days remaining in the quota period, counting today. The period ends the day
    /// before the reset date, since the reset restores the entitlement.
    /// </summary>
    public static int RemainingInPeriod(DateTime today, DateTimeOffset? resetAt, int workDaysPerWeek)
    {
        today = today.Date;
        var end = resetAt is { } r
            ? r.ToLocalTime().Date.AddDays(-1)
            : new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));

        // A reset already in the past leaves today as the only day we can speak for.
        if (end < today) end = today;
        return Math.Max(1, CountInclusive(today, end, workDaysPerWeek));
    }

    /// <summary>
    /// Business days remaining this week, counting today, and never running past the end of
    /// the quota period.
    /// </summary>
    public static int RemainingInWeek(DateTime today, DateTimeOffset? resetAt, int workDaysPerWeek)
    {
        today = today.Date;

        // Week runs Monday-Sunday; find this week's last workday.
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;   // Monday = 0
        var monday = today.AddDays(-daysSinceMonday);
        var lastWorkday = monday.AddDays(Math.Clamp(workDaysPerWeek, MinWorkDaysPerWeek, MaxWorkDaysPerWeek) - 1);

        var end = lastWorkday;
        if (resetAt is { } r)
        {
            var periodEnd = r.ToLocalTime().Date.AddDays(-1);
            if (periodEnd < end) end = periodEnd;
        }
        if (end < today) end = today;

        return Math.Max(1, CountInclusive(today, end, workDaysPerWeek));
    }

    /// <summary>
    /// Business days from the Monday of the current week through <paramref name="today"/>
    /// inclusive - the workday index today occupies within the week, counting Monday as 1.
    /// Used to place the "today" marker on the My Pace week bar.
    /// </summary>
    public static int ElapsedInWeek(DateTime today, int workDaysPerWeek)
        => CountInclusive(StartOfWeek(today), today, workDaysPerWeek);

    /// <summary>The Monday of the week containing <paramref name="today"/>.</summary>
    public static DateTime StartOfWeek(DateTime today)
    {
        today = today.Date;
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        return today.AddDays(-daysSinceMonday);
    }
}
