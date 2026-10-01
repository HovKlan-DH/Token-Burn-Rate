using System;
using System.Collections.Generic;

namespace TokenBurnRate.Services;

/// <summary>
/// Business-day arithmetic for pacing credit spend. A business day is any day of the week
/// the user ticked in the context menu's "Workdays in a week" (see <see cref="WorkWeek"/>,
/// persisted as AppState.WorkDays) - every day until a selection is made. No day is assumed
/// to be the weekend, and the chosen days need not be consecutive. The week itself always
/// runs Monday-Sunday.
///
/// Public holidays are not modelled: they vary by country and company, and treating one as
/// a working day only makes the daily budget slightly conservative.
/// </summary>
public static class BusinessDays
{
    public static bool IsBusinessDay(DateTime d, WorkWeek workWeek) => workWeek.Contains(d.DayOfWeek);

    /// <summary>
    /// The business days from <paramref name="from"/> through <paramref name="to"/>
    /// inclusive, in date order; empty when the range is empty or inverted. The one loop
    /// over a date range - every count is this list's Count, so a divisor and the days it
    /// is spread over cannot come from two loops that disagree.
    /// </summary>
    public static List<DateTime> WorkdaysBetween(DateTime from, DateTime to, WorkWeek workWeek)
    {
        var days = new List<DateTime>();
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            if (IsBusinessDay(d, workWeek)) days.Add(d);
        return days;
    }

    /// <summary>
    /// The quota period <paramref name="today"/> falls in, as its first and last day. The
    /// one place a period boundary is worked out, so a missing or stale reset is handled
    /// the same way by everything that needs one.
    ///
    /// It ends the day before the reset, since the reset restores the entitlement, and
    /// starts a month before that. A reset already reached is stale - GitHub's last poll
    /// reported a period that has since rolled over, or the machine was offline across one -
    /// and is rolled forward a month at a time until it is ahead of today: the period it
    /// started is the one we are in. Treating it as the end instead pinned the period to
    /// [reset, today], which spread the whole balance over one day. With no reset known at
    /// all, the calendar month is the period.
    /// </summary>
    public static (DateTime Start, DateTime End) QuotaPeriod(DateTime today, DateTimeOffset? resetAt)
    {
        today = today.Date;
        if (resetAt is not { } r)
        {
            var first = new DateTime(today.Year, today.Month, 1);
            return (first, first.AddMonths(1).AddDays(-1));
        }

        var reset = r.ToLocalTime().Date;
        var rolled = reset;
        for (var months = 1; rolled <= today; months++)
            rolled = reset.AddMonths(months);     // from the original each time, so day 31 does not decay to 28
        return (rolled.AddMonths(-1), rolled.AddDays(-1));
    }

    /// <summary>The Monday of the week containing <paramref name="today"/>.</summary>
    public static DateTime StartOfWeek(DateTime today)
    {
        today = today.Date;
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        return today.AddDays(-daysSinceMonday);
    }
}
