using System;
using System.Collections.Generic;

namespace TokenBurnRate.Models;

/// <summary>One Copilot quota bucket as reported live by the GitHub endpoint.</summary>
public sealed class CopilotQuota
{
    public required string Label { get; init; }
    public double Entitlement { get; init; }
    public double Remaining { get; init; }
    public double CreditsUsed { get; init; }
    public bool Unlimited { get; init; }
    public bool HasQuota { get; init; }

    public double Used => Entitlement > 0 ? Math.Max(0, Entitlement - Remaining) : CreditsUsed;

    /// <summary>The bar's fill, which saturates at full - there is no room past the end.</summary>
    public double Fraction => Entitlement > 0 ? Math.Clamp(Used / Entitlement, 0, 1) : 0;

    /// <summary>
    /// The figure shown in the caption, deliberately not clamped. Taking it from Fraction
    /// would cap it at 100 and make a bucket at its limit indistinguishable from one at
    /// three times it - the overspend the caption exists to report.
    /// </summary>
    public double Percent => Entitlement > 0 ? Used / Entitlement * 100 : 0;
}

public sealed class CopilotStatus
{
    public string Plan { get; init; } = "unknown";
    public IReadOnlyList<string> Organizations { get; init; } = Array.Empty<string>();
    public DateTimeOffset? ResetDate { get; init; }
    public List<CopilotQuota> Quotas { get; init; } = new();
    public string? Error { get; set; }
    /// <summary>True when the fix is an interactive sign-in rather than a transient failure.</summary>
    public bool NeedsSignIn { get; set; }
    public bool IsAvailable => Error is null && Quotas.Count > 0;
}

/// <summary>One plan limit as reported by Anthropic, with its real reset time.</summary>
public sealed class ClaudeLimit
{
    public required string Kind { get; init; }
    public required string Label { get; init; }
    /// <summary>Utilization 0-100, straight from the API. Not derived locally.</summary>
    public double Percent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
    public bool IsActive { get; init; }

    public double Fraction => Math.Clamp(Percent / 100.0, 0, 1);

    /// <summary>
    /// Compact "resets in 4h 8m" style text, or a placeholder when no reset is published -
    /// a fresh window that has not taken any usage yet has nothing scheduled to reset.
    ///
    /// Never empty: UsageBar gives a bar with a caption a slim band and one with none the
    /// full row height, so an empty string here would render this bar noticeably taller
    /// than its siblings rather than just missing a line of text.
    ///
    /// The figures are wrapped in <see cref="UsageText.Highlight"/> markers: when a limit is
    /// full, when it comes back is the one thing worth reading in the row, and it is
    /// otherwise nine-point grey among the words that carry no information.
    /// </summary>
    public string ResetText
    {
        get
        {
            if (ResetsAt is not { } r) return "not yet started";
            var now = DateTimeOffset.UtcNow;
            if (r - now <= TimeSpan.Zero) return "resetting";

            // Round first, then derive everything below from the rounded instant. Rounding only
            // the printed clock time let the two halves of one sentence disagree: a 14:58 reset
            // rounds to "15:00" while the countdown still truncated the exact 1h58m, and a 23:58
            // reset rounded forward into the next calendar day, printing a weekday the reset
            // never falls on. One instant in, one consistent sentence out.
            var localReset = RoundToNearest5Minutes(r.ToLocalTime());
            var localNow = now.ToLocalTime();
            var d = localReset - localNow;
            if (d <= TimeSpan.Zero) return "resetting";

            var culture = System.Globalization.CultureInfo.CurrentCulture;

            // A reset landing on a different calendar day gets a weekday label even once the
            // countdown itself drops under 24h (e.g. the week bar late on its reset-eve) - the
            // session bar never crosses midnight within its own window, so it always takes the
            // bare-time branch below. Both the comparison and the label read localReset, so the
            // branch can never be decided on a different day than the one it prints.
            var when = localReset.Date != localNow.Date
                ? $"{localReset:dddd} {localReset.ToString("t", culture)}"
                : localReset.ToString("t", culture);

            if (d.TotalDays >= 1) return $"resets in {Em($"{(int)d.TotalDays}d")} {Em($"{d.Hours}h")} ({when})";
            if (d.TotalHours >= 1) return $"resets in {Em($"{(int)d.TotalHours}h")} {Em($"{d.Minutes}m")} ({when})";
            return $"resets in {Em($"{d.Minutes}m")} ({when})";
        }
    }

    private static string Em(string s) => UsageText.Highlight(s);

    private static DateTimeOffset RoundToNearest5Minutes(DateTimeOffset t)
    {
        var ticks = TimeSpan.FromMinutes(5).Ticks;
        return new DateTimeOffset((t.Ticks + ticks / 2) / ticks * ticks, t.Offset);
    }
}

public sealed class ClaudeLimitsStatus
{
    public string Plan { get; set; } = "";
    public List<ClaudeLimit> Limits { get; init; } = new();
    public string? Error { get; init; }
    public bool IsAvailable => Error is null && Limits.Count > 0;

    /// <summary>
    /// Whether this failure is worth fast-retrying: a network hiccup, a token refresh that
    /// could not reach Anthropic, or an unexpected HTTP status can all clear themselves
    /// within seconds, which is exactly the boot-time race where the network stack is not
    /// up yet when the first poll runs. "not signed in" cannot be fixed by polling again
    /// sooner - only the user signing in does that - so it is excluded the same way
    /// Copilot's NeedsSignIn is excluded from its own retry accounting.
    /// </summary>
    public bool IsTransientFailure { get; init; }

    /// <summary>
    /// Whether signing in to Claude from this app would fix the panel - the Claude analogue
    /// of CopilotStatus.NeedsSignIn.
    ///
    /// Set both when no sign-in exists on this machine yet and when the stored grant was
    /// refused outright (revoked, or invalidated by a password change). Those are the same
    /// situation from the panel's point of view: there is no usable token and only the user
    /// can supply one. A failure that merely could not reach Anthropic is a transient
    /// failure instead, so an offline machine is never told to sign in again.
    /// </summary>
    public bool CanSignIn { get; init; }

    /// <summary>
    /// Whether a Claude sign-in of this app's own is stored on this machine - what the
    /// context menu's "Sign out of Claude" acts on.
    ///
    /// Reported here rather than re-read from the token store by the view model: answering
    /// it means a file read, a DPAPI decrypt and a JSON parse, and the service has just done
    /// all three to build this status. Asking again on every poll would put that work on the
    /// UI thread purely to decide whether one menu item is visible.
    /// </summary>
    public bool HasStoredSignIn { get; set; }

    public static ClaudeLimitsStatus Unavailable(
        string error, bool transient = false, bool canSignIn = false, bool hasStoredSignIn = false) =>
        new()
        {
            Error = error,
            IsTransientFailure = transient,
            CanSignIn = canSignIn,
            HasStoredSignIn = hasStoredSignIn,
        };
}

/// <summary>
/// A pacing view over the Copilot credit balance: how much of today's share is spent,
/// derived from the balance recorded at the first launch of the day.
/// </summary>
public sealed class CopilotPacing
{
    public double Entitlement { get; init; }
    public int BusinessDaysLeft { get; init; }

    public double PerDayAllowance { get; init; }
    public double UsedToday { get; init; }
    public double UsedThisWeek { get; init; }
    public double WeekBudget { get; init; }
    public double UsedThisPeriod { get; init; }

    /// <summary>Today's 1-based workday index within the week (Monday = 1).</summary>
    public int WorkdayIndexInWeek { get; init; }

    /// <summary>Total workdays the week bar's budget spans, from Monday through its last workday.</summary>
    public int WorkdaysInWeek { get; init; }

    public double DayFraction => PerDayAllowance > 0 ? UsedToday / PerDayAllowance : 0;
    public double WeekFraction => WeekBudget > 0 ? UsedThisWeek / WeekBudget : 0;
    public double MonthFraction => Entitlement > 0 ? UsedThisPeriod / Entitlement : 0;

    public double DayPercent => DayFraction * 100;
    public double WeekPercent => WeekFraction * 100;
    public double MonthPercent => MonthFraction * 100;
}
