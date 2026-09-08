using System;

namespace TokenBurnRate.Services;

/// <summary>
/// Business-day arithmetic for pacing credit spend, where a business day is Monday-Friday.
/// Public holidays are not modelled: they vary by country and company, and treating one as
/// a working day only makes the daily budget slightly conservative.
/// </summary>
public static class BusinessDays
{
    public static bool IsBusinessDay(DateTime d)
        => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>
    /// Business days from <paramref name="from"/> through <paramref name="to"/> inclusive.
    /// Returns 0 when the range is empty or inverted.
    /// </summary>
    public static int CountInclusive(DateTime from, DateTime to)
    {
        from = from.Date;
        to = to.Date;
        if (to < from) return 0;

        var count = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
            if (IsBusinessDay(d)) count++;
        return count;
    }

    /// <summary>
    /// Business days remaining in the quota period, counting today. The period ends the day
    /// before the reset date, since the reset restores the entitlement.
    /// </summary>
    public static int RemainingInPeriod(DateTime today, DateTimeOffset? resetAt)
    {
        today = today.Date;
        var end = resetAt is { } r
            ? r.ToLocalTime().Date.AddDays(-1)
            : new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));

        // A reset already in the past leaves today as the only day we can speak for.
        if (end < today) end = today;
        return Math.Max(1, CountInclusive(today, end));
    }

    /// <summary>
    /// Business days remaining this week, counting today, and never running past the end of
    /// the quota period.
    /// </summary>
    public static int RemainingInWeek(DateTime today, DateTimeOffset? resetAt)
    {
        today = today.Date;

        // Week runs Monday-Sunday; find this week's Friday.
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;   // Monday = 0
        var monday = today.AddDays(-daysSinceMonday);
        var friday = monday.AddDays(4);

        var end = friday;
        if (resetAt is { } r)
        {
            var periodEnd = r.ToLocalTime().Date.AddDays(-1);
            if (periodEnd < end) end = periodEnd;
        }
        if (end < today) end = today;

        return Math.Max(1, CountInclusive(today, end));
    }

    /// <summary>The Monday of the week containing <paramref name="today"/>.</summary>
    public static DateTime StartOfWeek(DateTime today)
    {
        today = today.Date;
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        return today.AddDays(-daysSinceMonday);
    }
}
