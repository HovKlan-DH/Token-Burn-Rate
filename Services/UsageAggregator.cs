using System;
using System.Collections.Generic;
using System.Linq;
using Token_Burn_Rate.Models;

namespace Token_Burn_Rate.Services;

/// <summary>
/// Turns raw usage records into windowed totals, and derives the bar denominators.
///
/// Anthropic publishes no per-plan token ceiling, so the budget for each window is
/// calibrated from observed history: the peak usage ever seen in a window of that size
/// becomes 100%. The bar therefore reads "how heavy is this session against my own
/// heaviest", which is self-adjusting and needs no configuration.
/// </summary>
public static class UsageAggregator
{
    public static readonly TimeSpan SessionWindow = TimeSpan.FromHours(5);
    public static readonly TimeSpan WeekWindow = TimeSpan.FromDays(7);
    public static readonly TimeSpan MonthWindow = TimeSpan.FromDays(30);

    public static long SumWindow(IReadOnlyList<UsageRecord> records, TimeSpan window, DateTimeOffset now)
    {
        var cutoff = now - window;
        long total = 0;
        foreach (var r in records)
            if (r.Timestamp > cutoff && r.Timestamp <= now)
                total += r.BillableTokens;
        return total;
    }

    /// <summary>
    /// Peak usage over any window of this size in the history, sampled by sliding over
    /// record boundaries. Used as the bar's 100% mark.
    /// </summary>
    public static long PeakWindow(IReadOnlyList<UsageRecord> records, TimeSpan window)
    {
        if (records.Count == 0) return 0;

        var ordered = records.OrderBy(r => r.Timestamp).ToArray();
        long best = 0, running = 0;
        int left = 0;

        for (int right = 0; right < ordered.Length; right++)
        {
            running += ordered[right].BillableTokens;
            var cutoff = ordered[right].Timestamp - window;
            while (left <= right && ordered[left].Timestamp <= cutoff)
            {
                running -= ordered[left].BillableTokens;
                left++;
            }
            if (running > best) best = running;
        }
        return best;
    }

    /// <summary>Builds the three Claude windows with calibrated budgets.</summary>
    public static List<WindowUsage> BuildClaudeWindows(IReadOnlyList<UsageRecord> records, DateTimeOffset now)
    {
        var specs = new (string Label, TimeSpan Window)[]
        {
            ("5 HOUR", SessionWindow),
            ("WEEK",   WeekWindow),
            ("MONTH",  MonthWindow),
        };

        var result = new List<WindowUsage>(specs.Length);
        foreach (var (label, window) in specs)
        {
            var used = SumWindow(records, window, now);
            var peak = PeakWindow(records, window);
            // Never let the bar sit pinned at 100% just because right now is the new peak;
            // give a little headroom so growth stays visible.
            var budget = Math.Max(peak, used);
            if (budget > 0) budget = (long)(budget * 1.05);

            result.Add(new WindowUsage
            {
                Label = label,
                Window = window,
                Tokens = used,
                Budget = budget,
            });
        }
        return result;
    }

    /// <summary>Tokens per hour over the recent window, for the burn-rate readout.</summary>
    public static double BurnRatePerHour(IReadOnlyList<UsageRecord> records, DateTimeOffset now)
    {
        var used = SumWindow(records, SessionWindow, now);
        return used / SessionWindow.TotalHours;
    }
}
