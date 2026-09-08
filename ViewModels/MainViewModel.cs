using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Token_Burn_Rate.Models;
using Token_Burn_Rate.Services;

namespace Token_Burn_Rate.ViewModels;

public sealed class BarViewModel : INotifyPropertyChanged
{
    private string _label = "";
    private string _valueText = "";
    private string _detailText = "";
    private double _fraction;
    private bool _isEnabled = true;

    public string Label { get => _label; set => Set(ref _label, value); }
    public string ValueText { get => _valueText; set => Set(ref _valueText, value); }
    public string DetailText { get => _detailText; set => Set(ref _detailText, value); }
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }

    public double Fraction
    {
        get => _fraction;
        set { Set(ref _fraction, value); OnPropertyChanged(nameof(Percent)); }
    }

    public double Percent => Fraction * 100;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(n);
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ClaudeUsageService _claude = new();
    private readonly ClaudeLimitsService _claudeLimits = new();
    private readonly CopilotUsageService _copilot = new();
    private readonly CopilotPacingService _pacing = new();

    private string _statusText = "Loading…";
    private string _claudeSubtitle = "";
    private string _copilotSubtitle = "";
    private string _burnRateText = "";
    private bool _isRefreshing;
    private bool _claudeVisible = true;
    private bool _copilotVisible = true;
    private bool _copilotNeedsSignIn;
    private bool _signInRunning;
    private bool _pacingVisible;
    private string _pacingSubtitle = "";
    private string _signInText = "";

    public ObservableCollection<BarViewModel> ClaudeBars { get; } = new();
    public ObservableCollection<BarViewModel> CopilotBars { get; } = new();
    /// <summary>The user's own pacing view: how much of today's share of credits is spent.</summary>
    public ObservableCollection<BarViewModel> PacingBars { get; } = new();

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string ClaudeSubtitle { get => _claudeSubtitle; set => Set(ref _claudeSubtitle, value); }
    public string CopilotSubtitle { get => _copilotSubtitle; set => Set(ref _copilotSubtitle, value); }
    public string BurnRateText { get => _burnRateText; set => Set(ref _burnRateText, value); }
    public bool IsRefreshing { get => _isRefreshing; set => Set(ref _isRefreshing, value); }

    /// <summary>A service with no data at all hides its whole panel rather than showing n/a rows.</summary>
    public bool ClaudeVisible { get => _claudeVisible; set => Set(ref _claudeVisible, value); }
    public bool CopilotVisible { get => _copilotVisible; set => Set(ref _copilotVisible, value); }
    public bool CopilotNeedsSignIn { get => _copilotNeedsSignIn; set => Set(ref _copilotNeedsSignIn, value); }
    public string SignInText { get => _signInText; set => Set(ref _signInText, value); }
    public bool PacingVisible { get => _pacingVisible; set => Set(ref _pacingVisible, value); }
    public string PacingSubtitle { get => _pacingSubtitle; set => Set(ref _pacingSubtitle, value); }

    public MainViewModel()
    {
        foreach (var label in new[] { "SESSION", "WEEK" })
            ClaudeBars.Add(new BarViewModel { Label = label, ValueText = "—" });
        foreach (var label in new[] { "COMPLETIONS", "CHAT", "PREMIUM" })
            CopilotBars.Add(new BarViewModel { Label = label, ValueText = "—" });
        foreach (var label in new[] { "DAY", "WEEK", "MONTH" })
            PacingBars.Add(new BarViewModel { Label = label, ValueText = "—" });
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var claudeTask = RefreshClaudeAsync(ct);
            var copilotTask = RefreshCopilotAsync(ct);
            await Task.WhenAll(claudeTask, copilotTask).ConfigureAwait(true);
            StatusText = "Updated " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = "Error: " + ex.Message;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task RefreshClaudeAsync(CancellationToken ct)
    {
        // Bars come from Anthropic's own utilization figures, which is the only source that
        // agrees with the Usage screen: the limits use fixed reset windows and a ceiling
        // that is not published, so neither can be reconstructed from local transcripts.
        var limits = await _claudeLimits.GetLimitsAsync(ct).ConfigureAwait(true);

        if (limits.IsAvailable)
        {
            SyncBars(ClaudeBars, limits.Limits.Count);
            for (int i = 0; i < limits.Limits.Count; i++)
            {
                var l = limits.Limits[i];
                var bar = ClaudeBars[i];
                bar.Label = l.Label;
                bar.Fraction = l.Fraction;
                bar.ValueText = $"{l.Percent:0}%";
                bar.DetailText = l.ResetText;
                bar.IsEnabled = true;
            }
        }
        ClaudeVisible = limits.IsAvailable;

        // Transcripts still supply what the API omits: absolute tokens and burn rate.
        var tokenText = "";
        if (_claude.DataDirectoryExists)
        {
            var records = await Task.Run(() => _claude.LoadAsync(ct), ct).ConfigureAwait(true);
            var now = DateTimeOffset.UtcNow;
            var rate = UsageAggregator.BurnRatePerHour(records, now);
            BurnRateText = $"{Format.Tokens((long)rate)}/h";
            tokenText = Format.Tokens(UsageAggregator.SumWindow(
                records, UsageAggregator.SessionWindow, now)) + " tokens";
        }

        ClaudeSubtitle = string.IsNullOrEmpty(limits.Plan) ? tokenText : $"{limits.Plan} · {tokenText}";
    }

    /// <summary>
    /// Fills the pacing bars: today's share of the remaining credits, the same for the
    /// current week, and the period total. Hidden when no bucket meters credits.
    /// </summary>
    private void RefreshPacing(CopilotStatus status)
    {
        var pacing = _pacing.Build(status, DateTime.Now);
        if (pacing is null)
        {
            PacingVisible = false;
            return;
        }

        PacingVisible = true;
        PacingSubtitle = $"{pacing.PerDayAllowance:0}/day · {pacing.BusinessDaysLeft} work days left";

        Set(PacingBars[0], pacing.DayFraction, pacing.DayPercent,
            $"{pacing.UsedToday:0}/{pacing.PerDayAllowance:0}", "today");

        Set(PacingBars[1], pacing.WeekFraction, pacing.WeekPercent,
            $"{pacing.UsedThisWeek:0}/{pacing.WeekBudget:0}", "this week");

        Set(PacingBars[2], pacing.MonthFraction, pacing.MonthPercent,
            $"{pacing.UsedThisPeriod:0}/{pacing.Entitlement:0}", "this period");

        static void Set(BarViewModel bar, double fraction, double percent, string value, string what)
        {
            // Spending past the allowance is meaningful, so the number keeps climbing even
            // though the bar itself stops at full.
            bar.Fraction = Math.Clamp(fraction, 0, 1);
            bar.ValueText = value;
            bar.DetailText = $"{percent:0}% of {what}";
            bar.IsEnabled = true;
        }
    }

    /// <summary>
    /// Runs the GitHub device-flow sign-in. Shows the code in the panel, opens the browser,
    /// then waits for approval. Requires no GitHub CLI and no admin rights.
    /// </summary>
    public async Task SignInToGitHubAsync(CancellationToken ct = default)
    {
        if (_signInRunning) return;
        _signInRunning = true;
        try
        {
            var auth = new GitHubDeviceAuth();
            var code = await auth.RequestCodeAsync(ct).ConfigureAwait(true);

            SignInText = $"Code: {code.UserCode}";
            CopilotSubtitle = "waiting for browser approval…";
            TryOpenBrowser(code.VerificationUri);

            var token = await auth.PollForTokenAsync(code, ct).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                SignInText = "Sign in to GitHub";
                CopilotSubtitle = "sign-in cancelled or timed out";
                return;
            }

            GitHubDeviceAuth.SaveToken(token);
            _copilot.InvalidateToken();
            await RefreshCopilotAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SignInText = "Sign in to GitHub";
            CopilotSubtitle = "sign-in failed: " + ex.Message;
        }
        finally
        {
            _signInRunning = false;
        }
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // Headless or restricted desktop: the code is still shown for manual entry.
        }
    }

    /// <summary>Grows or shrinks a bar list so it matches however many limits the API returned.</summary>
    private static void SyncBars(ObservableCollection<BarViewModel> bars, int count)
    {
        while (bars.Count < count) bars.Add(new BarViewModel { Label = "", ValueText = "—" });
        while (bars.Count > count) bars.RemoveAt(bars.Count - 1);
    }

    private async Task RefreshCopilotAsync(CancellationToken ct)
    {
        var status = await _copilot.GetStatusAsync(ct).ConfigureAwait(true);

        if (!status.IsAvailable)
        {
            // Keep the panel when a sign-in would fix it, so the button has somewhere to
            // live; hide it outright for anything else.
            CopilotNeedsSignIn = status.NeedsSignIn;
            CopilotVisible = status.NeedsSignIn;
            CopilotSubtitle = status.Error ?? "unavailable";
            SignInText = "Sign in to GitHub";
            PacingVisible = false;
            foreach (var bar in CopilotBars) { bar.ValueText = "n/a"; bar.Fraction = 0; bar.IsEnabled = false; }
            return;
        }

        CopilotNeedsSignIn = false;
        CopilotVisible = true;

        // On a work machine the plan is org-assigned, so show which org grants it as well
        // as the plan tier; on a personal account there is no org and the tier stands alone.
        var plan = status.Organizations.Count > 0
            ? $"{status.Organizations[0]} · {status.Plan}"
            : status.Plan;
        var reset = status.ResetDate is { } d ? $" · resets {d:MMM d}" : "";
        CopilotSubtitle = plan + reset;

        for (int i = 0; i < CopilotBars.Count; i++)
        {
            var bar = CopilotBars[i];
            if (i >= status.Quotas.Count) { bar.ValueText = "n/a"; bar.IsEnabled = false; continue; }

            var q = status.Quotas[i];
            bar.Label = q.Label;

            if (q.Unlimited)
            {
                bar.ValueText = "∞";
                bar.DetailText = "unlimited";
                bar.Fraction = 0;
                bar.IsEnabled = true;
            }
            else if (!q.HasQuota || q.Entitlement <= 0)
            {
                // Not every plan grants every bucket: the free tier has no premium
                // interactions, so the bar is dimmed rather than shown as an empty quota.
                bar.ValueText = "—";
                bar.DetailText = "not included in plan";
                bar.Fraction = 0;
                bar.IsEnabled = false;
            }
            else
            {
                bar.ValueText = $"{q.Used:0}/{q.Entitlement:0}";
                bar.DetailText = $"{q.Percent:0}% used";
                bar.Fraction = q.Fraction;
                bar.IsEnabled = true;
            }
        }

        RefreshPacing(status);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

internal static class Format
{
    public static string Tokens(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000.0:0.##}B",
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString(),
    };
}
