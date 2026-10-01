using System;
using System.Globalization;
using System.Linq;
using TokenBurnRate.Models;

namespace TokenBurnRate.Services;

/// <summary>
/// Derives a "how much can I spend today" view from GitHub's point-in-time credit balance.
///
/// GitHub only reports what is left right now, so spend-per-day has to be reconstructed:
/// the balance at the first launch of the day is recorded, and today's usage is the
/// difference between that opening balance and the current one.
///
/// The daily allowance is the balance the day opened with divided by the business days left
/// in the period, so it holds still through the day and is recalculated every day:
/// underspending today raises tomorrow's allowance, overspending lowers it. A day off has no
/// allowance. The week's budget is worked out the same way once, from the balance the week
/// opened with, and holds still through the week. Every period boundary comes from
/// <see cref="BusinessDays.QuotaPeriod"/>.
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
    /// <param name="workWeek">
    /// The days of the week that count as workdays - see <see cref="BusinessDays"/>.
    /// </param>
    public CopilotPacing? Build(CopilotStatus status, DateTime now, WorkWeek workWeek)
    {
        var bucket = SelectCreditBucket(status);
        if (bucket is null) return null;

        var remaining = Math.Max(0, bucket.Entitlement - bucket.Used);
        var state = LoadOrRoll(remaining, now);

        var today = now.Date;
        var period = BusinessDays.QuotaPeriod(today, status.ResetDate);
        var periodWorkdays = BusinessDays.WorkdaysBetween(period.Start, period.End, workWeek);
        var daysLeftInPeriod = periodWorkdays.Count(d => d >= today);
        var isWorkday = BusinessDays.IsBusinessDay(today, workWeek);

        // Today's allowance, from the balance the day opened with rather than what is left
        // right now: dividing the live balance shrank today's allowance with every credit
        // spent today, so the bar's own target moved away as it filled.
        //
        // A day off has none at all. The workdays are the only days the balance is planned to
        // be spent on, so a credit spent on a day off is over budget - not drawn from a day's
        // worth borrowed from the workdays ahead, as it used to be, which offered a full
        // workday's allowance on every Saturday and Sunday of a Monday-Friday week.
        var perDay = isWorkday ? Allowance(state.DayOpening, daysLeftInPeriod) : 0;

        // Usage is the drop from the opening balance; never negative, since a period reset
        // can raise the balance above where the day started.
        var usedToday = Math.Max(0, state.DayOpening - remaining);
        var usedThisWeek = Math.Max(0, state.WeekOpening - remaining);

        // The week's budget is a workday's allowance as it stood when the week's opening
        // balance was taken, once for each of the week's workdays from then on - 431 a day
        // over a Thursday and a Friday is 862 - so it holds still through the week whatever
        // is spent in it. It used to be what earlier days had actually spent plus an
        // allowance for each day ahead, which once no workday was left collapsed to exactly
        // what had been spent: the bar read 100% on every day off after the week's last
        // workday, however little the week had used.
        //
        // The week starts on the day the opening was taken (see WeekOpeningDay), not on
        // Monday: usage is only known from then on, so a budget spread from Monday over a
        // week first seen on Wednesday measured three days of spend against five days of
        // budget. A reset raises the balance and so re-takes the opening, which starts the
        // week at the reset as well. A known reset also bounds it directly, for the one reset
        // that raises nothing - a balance untouched before it - leaving the opening on a day
        // of last period's. An unknown one does not: the calendar month it falls back to is
        // a guess, and the opening is still Monday's.
        var monday = BusinessDays.StartOfWeek(today);
        var weekStart = ParseDay(state.WeekOpeningDay) is { } opened && opened >= monday && opened <= today
            ? opened
            : monday;
        if (status.ResetDate is not null && period.Start > weekStart) weekStart = period.Start;
        var weekEnd = monday.AddDays(6) < period.End ? monday.AddDays(6) : period.End;
        var weekWorkdays = BusinessDays.WorkdaysBetween(weekStart, weekEnd, workWeek);
        var weekPerDay = Allowance(state.WeekOpening,
            BusinessDays.WorkdaysBetween(weekStart, period.End, workWeek).Count);
        var weekBudget = weekPerDay * weekWorkdays.Count;

        // Today's position among the week's workdays, for the pacing marks on the week bar -
        // how many fall on or before today, so on a day off it stays at the last workday
        // before it.
        var workdayIndex = weekWorkdays.Count(d => d <= today);

        return new CopilotPacing
        {
            Entitlement = bucket.Entitlement,
            Unit = bucket.Unit,
            BusinessDaysLeft = daysLeftInPeriod,
            IsWorkdayToday = isWorkday,
            PerDayAllowance = perDay,
            UsedToday = usedToday,
            UsedThisWeek = usedThisWeek,
            WeekBudget = weekBudget,
            UsedThisPeriod = bucket.Used,
            WeekWorkdays = weekWorkdays,
            WorkdayIndexInWeek = workdayIndex,
            PeriodWorkdays = periodWorkdays,
        };
    }

    private static DateTime? ParseDay(string day) =>
        DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    /// <summary>
    /// A balance spread evenly over <paramref name="days"/> business days, in whole credits
    /// rounded down: 9500 over 22 days is 431 a day, and 432 would promise more than the
    /// balance covers. The remainder is not lost - each day's allowance is recalculated from
    /// that day's opening balance. Below one a day the fraction stands, since rounding it
    /// down would leave no allowance at all; no days at all leave none.
    /// </summary>
    private static double Allowance(double balance, int days)
    {
        if (days <= 0) return 0;
        var exact = balance / days;
        return exact >= 1 ? Math.Floor(exact) : exact;
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
            state.WeekOpeningDay = today;
            dirty = true;
        }

        // A balance above the recorded opening means the quota reset; re-anchor to it. The
        // week's opening day moves with it, since usage before the reset is not this week's
        // to measure any more.
        if (remaining > state.DayOpening)
        {
            state.DayOpening = remaining;
            dirty = true;
        }
        if (remaining > state.WeekOpening)
        {
            state.WeekOpening = remaining;
            state.WeekOpeningDay = today;
            dirty = true;
        }

        if (dirty) AppState.Update(a => a.Pacing = state);
        return state;
    }

}
