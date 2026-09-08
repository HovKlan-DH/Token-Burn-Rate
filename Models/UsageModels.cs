using System;
using System.Collections.Generic;

namespace Token_Burn_Rate.Models;

/// <summary>A single billed assistant message pulled from a Claude Code transcript.</summary>
public readonly record struct UsageRecord(
    string MessageId,
    DateTimeOffset Timestamp,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens)
{
    /// <summary>
    /// Cache reads are deliberately excluded from the headline number. They are billed at a
    /// small fraction of input rate and dwarf every other figure by ~100x (3.5B vs 44M in a
    /// month here), so including them would flatten the bars into noise.
    /// </summary>
    public long BillableTokens => InputTokens + OutputTokens + CacheCreationTokens;

    public long TotalTokens => BillableTokens + CacheReadTokens;
}

/// <summary>A usage total over one rolling time window, with the denominator used to fill its bar.</summary>
public sealed class WindowUsage
{
    public required string Label { get; init; }
    public TimeSpan Window { get; init; }
    public long Tokens { get; set; }
    public long Budget { get; set; }
    public bool HasBudget => Budget > 0;

    public double Fraction => HasBudget ? Math.Clamp((double)Tokens / Budget, 0, 1) : 0;
    public double Percent => Fraction * 100;
}

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
    public double Fraction => Entitlement > 0 ? Math.Clamp(Used / Entitlement, 0, 1) : 0;
    public double Percent => Fraction * 100;
}

public sealed class CopilotStatus
{
    public string Plan { get; init; } = "unknown";
    public string Sku { get; init; } = "";
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
    public string Severity { get; init; } = "normal";
    public DateTimeOffset? ResetsAt { get; init; }
    public bool IsActive { get; init; }

    public double Fraction => Math.Clamp(Percent / 100.0, 0, 1);

    /// <summary>Compact "resets in 4h 8m" style text, or empty when no reset is published.</summary>
    public string ResetText
    {
        get
        {
            if (ResetsAt is not { } r) return "";
            var d = r - DateTimeOffset.UtcNow;
            if (d <= TimeSpan.Zero) return "resetting";
            if (d.TotalDays >= 1) return $"resets in {(int)d.TotalDays}d {d.Hours}h";
            if (d.TotalHours >= 1) return $"resets in {(int)d.TotalHours}h {d.Minutes}m";
            return $"resets in {d.Minutes}m";
        }
    }
}

public sealed class ClaudeLimitsStatus
{
    public string Plan { get; set; } = "";
    public List<ClaudeLimit> Limits { get; init; } = new();
    public string? Error { get; init; }
    public bool IsAvailable => Error is null && Limits.Count > 0;

    public static ClaudeLimitsStatus Unavailable(string error) => new() { Error = error };
}
