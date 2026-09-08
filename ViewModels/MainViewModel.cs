using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TokenBurnRate.Models;
using TokenBurnRate.Services;

namespace TokenBurnRate.ViewModels;

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

    private DateTimeOffset _nextRefresh = DateTimeOffset.UtcNow;
    private string _countdown = "";
    private string _claudeSubtitle = "";
    private string _copilotSubtitle = "";
    private bool _isRefreshing;
    private bool _claudeVisible = true;
    private bool _copilotVisible = true;
    private bool _copilotNeedsSignIn;
    private bool _signInRunning;
    private bool _pacingVisible;
    private string _pacingSubtitle = "";
    // Kept apart from CopilotSubtitle so the pacing panel can borrow it when the Copilot
    // panel is not on screen to show it.
    private string _copilotReset = "";
    private int _pacingDaysLeft;
    private bool _claudeCollapsed;
    private bool _copilotCollapsed;
    private bool _pacingCollapsed;
    private bool _claudeHidden;
    private bool _copilotHidden;
    private bool _pacingHidden;
    private bool _autostartEnabled;
    private bool _pinned = true;
    private bool _closeToTray = true;
    private bool _traySupported;
    private double _labelWidth = 86;
    private string _signInText = "";

    public ObservableCollection<BarViewModel> ClaudeBars { get; } = new();
    public ObservableCollection<BarViewModel> CopilotBars { get; } = new();
    /// <summary>The user's own pacing view: how much of today's share of credits is spent.</summary>
    public ObservableCollection<BarViewModel> PacingBars { get; } = new();

    /// <summary>Plain-language time until the next automatic refresh.</summary>
    public string Countdown { get => _countdown; private set => Set(ref _countdown, value); }
    public string ClaudeSubtitle { get => _claudeSubtitle; set => Set(ref _claudeSubtitle, value); }
    public string CopilotSubtitle { get => _copilotSubtitle; set => Set(ref _copilotSubtitle, value); }
    public bool IsRefreshing { get => _isRefreshing; set => Set(ref _isRefreshing, value); }

    // A panel shows only when the service has data AND the user has not hidden it. The two
    // are tracked separately so a refresh cannot overwrite the user's choice.

    /// <summary>Set by the refresh: does this service have anything to show?</summary>
    public bool ClaudeAvailable
    {
        get => _claudeVisible;
        set { if (Set(ref _claudeVisible, value)) VisibilityChanged(nameof(ClaudeVisible)); }
    }

    public bool CopilotAvailable
    {
        get => _copilotVisible;
        set { if (Set(ref _copilotVisible, value)) VisibilityChanged(nameof(CopilotVisible)); }
    }
    public bool CopilotNeedsSignIn { get => _copilotNeedsSignIn; set => Set(ref _copilotNeedsSignIn, value); }
    public string SignInText { get => _signInText; set => Set(ref _signInText, value); }
    public bool PacingAvailable
    {
        get => _pacingVisible;
        set { if (Set(ref _pacingVisible, value)) VisibilityChanged(nameof(PacingVisible)); }
    }

    /// <summary>What the UI actually binds to.</summary>
    public bool ClaudeVisible => _claudeVisible && !_claudeHidden;
    public bool CopilotVisible => _copilotVisible && !_copilotHidden;
    public bool PacingVisible => _pacingVisible && !_pacingHidden;

    // Hiding is the user's own choice, made from the right-click menu.
    public bool ClaudeHidden
    {
        get => _claudeHidden;
        set { if (SetHidden(ref _claudeHidden, value)) VisibilityChanged(nameof(ClaudeVisible)); }
    }

    public bool CopilotHidden
    {
        get => _copilotHidden;
        set { if (SetHidden(ref _copilotHidden, value)) VisibilityChanged(nameof(CopilotVisible)); }
    }

    public bool PacingHidden
    {
        get => _pacingHidden;
        set { if (SetHidden(ref _pacingHidden, value)) VisibilityChanged(nameof(PacingVisible)); }
    }

    /// <summary>
    /// Applies a hide/show, refusing any change that would leave nothing on screen. Hiding
    /// the last panel would remove the very menu needed to bring one back.
    /// </summary>
    private bool SetHidden(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return false;

        if (value && WouldHideEverything(name)) return false;

        field = value;
        OnPropertyChanged(name!);
        OnPropertyChanged(nameof(CanHideClaude));
        OnPropertyChanged(nameof(CanHideCopilot));
        OnPropertyChanged(nameof(CanHidePacing));
        PersistHidden();
        return true;
    }

    /// <summary>True when hiding the named panel would leave no panel visible.</summary>
    private bool WouldHideEverything(string? name)
    {
        var claude = ClaudeVisible && name != nameof(ClaudeHidden);
        var copilot = CopilotVisible && name != nameof(CopilotHidden);
        var pacing = PacingVisible && name != nameof(PacingHidden);
        return !claude && !copilot && !pacing;
    }

    // The menu greys out an item that is the last one standing.
    public bool CanHideClaude => !ClaudeVisible || !WouldHideEverything(nameof(ClaudeHidden));
    public bool CanHideCopilot => !CopilotVisible || !WouldHideEverything(nameof(CopilotHidden));
    public bool CanHidePacing => !PacingVisible || !WouldHideEverything(nameof(PacingHidden));

    private void VisibilityChanged(string visibleName)
    {
        OnPropertyChanged(visibleName);
        OnPropertyChanged(nameof(CanHideClaude));
        OnPropertyChanged(nameof(CanHideCopilot));
        OnPropertyChanged(nameof(CanHidePacing));
        // Hiding or showing the Copilot panel moves the reset date between the two panels.
        if (visibleName == nameof(CopilotVisible)) UpdatePacingSubtitle();
        SoloChanged();
        OnBarsChanged();
    }

    /// <summary>
    /// Re-evaluates which panel, if any, is the last one standing. Any visibility change can
    /// promote or demote a panel to solo, which decides both its chevron and its expansion.
    /// </summary>
    private void SoloChanged()
    {
        OnPropertyChanged(nameof(ClaudeSolo));
        OnPropertyChanged(nameof(CopilotSolo));
        OnPropertyChanged(nameof(PacingSolo));
        OnPropertyChanged(nameof(ClaudeExpanded));
        OnPropertyChanged(nameof(CopilotExpanded));
        OnPropertyChanged(nameof(PacingExpanded));
    }

    // Collapsing hides a panel's bars but keeps its header, so the panel can be reopened.
    public bool ClaudeCollapsed
    {
        get => _claudeCollapsed;
        set
        {
            if (!Set(ref _claudeCollapsed, value)) return;
            OnPropertyChanged(nameof(ClaudeExpanded));
            Persist();
            OnBarsChanged();
        }
    }

    public bool CopilotCollapsed
    {
        get => _copilotCollapsed;
        set
        {
            if (!Set(ref _copilotCollapsed, value)) return;
            OnPropertyChanged(nameof(CopilotExpanded));
            Persist();
            OnBarsChanged();
        }
    }

    public bool PacingCollapsed
    {
        get => _pacingCollapsed;
        set
        {
            if (!Set(ref _pacingCollapsed, value)) return;
            OnPropertyChanged(nameof(PacingExpanded));
            Persist();
            OnBarsChanged();
        }
    }

    /// <summary>
    /// True when the named panel is the only one on screen. Collapsing it would leave the
    /// widget showing nothing but a header, so the last panel standing is always expanded
    /// and loses its chevron. The stored preference is left untouched: bringing a second
    /// panel back restores whatever collapsed state the user had chosen.
    /// </summary>
    private bool IsOnlyPanel(bool self) =>
        self && (ClaudeVisible ? 1 : 0) + (CopilotVisible ? 1 : 0) + (PacingVisible ? 1 : 0) == 1;

    public bool ClaudeSolo => IsOnlyPanel(ClaudeVisible);
    public bool CopilotSolo => IsOnlyPanel(CopilotVisible);
    public bool PacingSolo => IsOnlyPanel(PacingVisible);

    public bool ClaudeExpanded => !_claudeCollapsed || ClaudeSolo;
    public bool CopilotExpanded => !_copilotCollapsed || CopilotSolo;
    public bool PacingExpanded => !_pacingCollapsed || PacingSolo;

    /// <summary>
    /// Whether the widget floats above other windows. Defaults to on, since an
    /// always-visible status display is the point; turning it off makes it behave like any
    /// other window and be covered by whatever is launched next.
    /// </summary>
    public bool Pinned
    {
        get => _pinned;
        set
        {
            if (!Set(ref _pinned, value)) return;
            OnPropertyChanged(nameof(PinOpacity));
            OnPropertyChanged(nameof(PinTooltip));
            AppState.Update(a => a.Pinned = value);
        }
    }

    /// <summary>
    /// Whether the close button hides the widget to the tray rather than exiting. Defaults
    /// to on: this is a monitor meant to keep running, and the tray is where a background
    /// monitor belongs. Forced off when the desktop has no usable tray, since the close
    /// button would otherwise be a trap with no way to get the window back.
    /// </summary>
    public bool CloseToTray
    {
        get => _closeToTray && TraySupported;
        set
        {
            if (!TraySupported || !Set(ref _closeToTray, value)) return;
            AppState.Update(a => a.CloseToTray = value);
        }
    }

    /// <summary>
    /// Whether a tray icon could be created at all. Set once at startup by the view, which
    /// is the only place that knows whether the platform actually produced one.
    /// </summary>
    public bool TraySupported
    {
        get => _traySupported;
        set
        {
            if (!Set(ref _traySupported, value)) return;
            OnPropertyChanged(nameof(CloseToTray));
        }
    }

    /// <summary>
    /// The pin is drawn as a plain text glyph so it stays monochrome alongside the close
    /// button. Emoji pushpins would render in colour, and Segoe MDL2 icon codes would be a
    /// missing-glyph box off Windows.
    /// </summary>
    public string PinGlyph => "◉";     // fisheye: a filled dot inside a ring

    /// <summary>Dimmed when unpinned, so the state reads at a glance.</summary>
    public double PinOpacity => _pinned ? 1.0 : 0.35;

    public string PinTooltip => _pinned
        ? "Always on top - click to let other windows cover it"
        : "Click to keep it above other windows";

    /// <summary>Whether the widget launches with the desktop session.</summary>
    public bool AutostartEnabled
    {
        get => _autostartEnabled;
        set
        {
            if (_autostartEnabled == value) return;

            // Only claim the new state if the platform actually accepted it, so a failed
            // write leaves the menu showing the truth rather than a lie.
            if (!AutostartService.Set(value)) return;

            _autostartEnabled = value;
            OnPropertyChanged(nameof(AutostartEnabled));
        }
    }

    /// <summary>False on a platform with no autostart mechanism, which greys the menu item.</summary>
    public bool AutostartSupported => AutostartService.IsSupported;

    /// <summary>
    /// Width of the label column, shared by every bar so they line up. It is measured from
    /// only the labels actually on screen, so collapsing the panel with the longest label
    /// hands that space back to the remaining bars.
    /// </summary>
    public double LabelWidth { get => _labelWidth; private set => Set(ref _labelWidth, value); }
    public string PacingSubtitle { get => _pacingSubtitle; private set => Set(ref _pacingSubtitle, value); }

    /// <summary>
    /// Builds the pacing subtitle, appending the reset date only when the Copilot panel is
    /// not on screen to carry it. Both panels measure the same period, so showing it twice
    /// is repetition; showing it nowhere leaves the pacing bars without their end date.
    /// </summary>
    private void UpdatePacingSubtitle()
    {
        var days = $"{_pacingDaysLeft} work days left in month";
        PacingSubtitle = CopilotVisible || _copilotReset.Length == 0
            ? days
            : days + " ·" + _copilotReset;
    }

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
        _nextRefresh = DateTimeOffset.UtcNow + RefreshInterval;
        try
        {
            var claudeTask = RefreshClaudeAsync(ct);
            var copilotTask = RefreshCopilotAsync(ct);
            await Task.WhenAll(claudeTask, copilotTask).ConfigureAwait(true);
            OnBarsChanged();    // labels vary by plan, so re-measure once they are filled
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Each panel reports its own failure, so there is nothing to show globally.
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
        ClaudeAvailable = limits.IsAvailable;

        // Transcripts still supply what the API omits: absolute tokens and burn rate.
        var tokenText = "";
        if (_claude.DataDirectoryExists)
        {
            var records = await Task.Run(() => _claude.LoadAsync(ct), ct).ConfigureAwait(true);
            var now = DateTimeOffset.UtcNow;
            // Tokens used in the current 5-hour window. The API reports percentages only,
            // so this absolute figure is the one thing the transcripts still contribute.
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
            PacingAvailable = false;
            return;
        }

        PacingAvailable = true;
        // The per-day allowance is already the Day bar's denominator, so it is not repeated
        // here; what the bars cannot show is how many days that allowance is spread over.
        _pacingDaysLeft = pacing.BusinessDaysLeft;
        UpdatePacingSubtitle();

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

    /// <summary>The project's home, opened from the context menu.</summary>
    public const string ProjectUrl = "https://github.com/HovKlan-DH/TokenBurnRate";

    /// <summary>Opens the project page in the default browser.</summary>
    public void OpenProjectPage() => TryOpenBrowser(ProjectUrl);

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
            CopilotAvailable = status.NeedsSignIn;
            CopilotSubtitle = status.Error ?? "unavailable";
            SignInText = "Sign in to GitHub";
            _copilotReset = "";
            PacingAvailable = false;
            foreach (var bar in CopilotBars) { bar.ValueText = "n/a"; bar.Fraction = 0; bar.IsEnabled = false; }
            return;
        }

        CopilotNeedsSignIn = false;
        CopilotAvailable = true;

        // On a work machine the plan is org-assigned, so show which org grants it as well
        // as the plan tier; on a personal account there is no org and the tier stands alone.
        var plan = status.Organizations.Count > 0
            ? $"{status.Organizations[0]} · {status.Plan}"
            : status.Plan;
        _copilotReset = status.ResetDate is { } d ? $" resets {d:MMM d}" : "";
        CopilotSubtitle = plan + (_copilotReset.Length > 0 ? " ·" + _copilotReset : "");
        UpdatePacingSubtitle();

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

    /// <summary>How long between automatic refreshes of each service.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Updates the countdown to the next refresh. Both services are refreshed by one timer,
    /// so there is a single figure rather than one per panel. Driven once a second by the
    /// view so it visibly ticks down between refreshes.
    /// </summary>
    public void TickCountdowns()
    {
        var d = _nextRefresh - DateTimeOffset.UtcNow;
        if (d <= TimeSpan.Zero)
        {
            Countdown = "Updating…";
            return;
        }

        // Round up, so the last second reads "1 second" rather than "0 seconds".
        var seconds = (int)Math.Ceiling(d.TotalSeconds);
        Countdown = seconds == 1 ? "Updates in 1 second" : $"Updates in {seconds} seconds";
    }

    /// <summary>Restores collapsed panels from the state file at start-up.</summary>
    public void LoadCollapsedState()
    {
        var state = AppState.Load();

        InitialiseAutostart(state);

        // Absent means never set: stay pinned, which is the widget's reason for existing.
        _pinned = state.Pinned ?? true;
        OnPropertyChanged(nameof(Pinned));
        OnPropertyChanged(nameof(PinOpacity));
        OnPropertyChanged(nameof(PinTooltip));

        // Absent means never set: minimise to the tray, which is where a background monitor
        // belongs. TraySupported still has the final say.
        _closeToTray = state.CloseToTray ?? true;
        OnPropertyChanged(nameof(CloseToTray));

        if (state.Hidden is { } h)
        {
            // Guard against a file that hides everything, which would leave no way back.
            if (!(h.Claude && h.Copilot && h.Pacing))
            {
                _claudeHidden = h.Claude;
                _copilotHidden = h.Copilot;
                _pacingHidden = h.Pacing;
            }

            OnPropertyChanged(nameof(ClaudeHidden));
            OnPropertyChanged(nameof(CopilotHidden));
            OnPropertyChanged(nameof(PacingHidden));
            OnPropertyChanged(nameof(ClaudeVisible));
            OnPropertyChanged(nameof(CopilotVisible));
            OnPropertyChanged(nameof(PacingVisible));
            OnPropertyChanged(nameof(CanHideClaude));
            OnPropertyChanged(nameof(CanHideCopilot));
            OnPropertyChanged(nameof(CanHidePacing));
            UpdatePacingSubtitle();
        }

        var c = state.Collapsed;
        if (c is null) { OnBarsChanged(); return; }

        _claudeCollapsed = c.Claude;
        _copilotCollapsed = c.Copilot;
        _pacingCollapsed = c.Pacing;

        OnPropertyChanged(nameof(ClaudeCollapsed));
        OnPropertyChanged(nameof(CopilotCollapsed));
        OnPropertyChanged(nameof(PacingCollapsed));
        SoloChanged();
        OnBarsChanged();
    }

    /// <summary>
    /// Turns autostart on the first time the app is ever run, then leaves it under the
    /// user's control. The flag records that the default has been applied, so switching it
    /// off later is not undone at the next launch.
    /// </summary>
    private void InitialiseAutostart(AppState state)
    {
        if (!AutostartService.IsSupported)
        {
            OnPropertyChanged(nameof(AutostartSupported));
            return;
        }

        if (state.AutostartInitialised is not true)
        {
            AutostartService.Set(true);
            AppState.Update(a => a.AutostartInitialised = true);
        }

        _autostartEnabled = AutostartService.IsEnabled();
        OnPropertyChanged(nameof(AutostartEnabled));
        OnPropertyChanged(nameof(AutostartSupported));
    }

    private void PersistHidden() => AppState.Update(a => a.Hidden = new AppState.HiddenState
    {
        Claude = _claudeHidden,
        Copilot = _copilotHidden,
        Pacing = _pacingHidden,
    });

    private void Persist() => AppState.Update(a => a.Collapsed = new AppState.CollapsedState
    {
        Claude = _claudeCollapsed,
        Copilot = _copilotCollapsed,
        Pacing = _pacingCollapsed,
    });

    /// <summary>
    /// Recomputes the shared label column from the labels currently on screen. A collapsed
    /// or hidden panel contributes nothing, so its long labels stop padding everyone else.
    /// </summary>
    private void OnBarsChanged()
    {
        var widest = 0.0;

        // Measured from the bars actually on screen, so this has to agree with what the UI
        // binds to: a hidden panel contributes nothing, and a solo panel is expanded even
        // when its stored preference says collapsed.
        if (ClaudeVisible && ClaudeExpanded) widest = Widest(ClaudeBars, widest);
        if (CopilotVisible && CopilotExpanded) widest = Widest(CopilotBars, widest);
        if (PacingVisible && PacingExpanded) widest = Widest(PacingBars, widest);

        // Padding to the right of the text, plus a floor so a single short label does not
        // leave the bars starting awkwardly close to the edge.
        LabelWidth = Math.Max(44, widest + 8);

        static double Widest(ObservableCollection<BarViewModel> bars, double running)
        {
            foreach (var b in bars)
            {
                var w = MeasureLabel(b.Label);
                if (w > running) running = w;
            }
            return running;
        }
    }

    /// <summary>Measures a label in the same typeface and size the bars render it with.</summary>
    private static double MeasureLabel(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        try
        {
            var ft = new Avalonia.Media.FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                Avalonia.Media.FlowDirection.LeftToRight,
                new Avalonia.Media.Typeface(Avalonia.Media.FontFamily.Default),
                9,
                Avalonia.Media.Brushes.Gray);
            return ft.Width;
        }
        catch (Exception)
        {
            // Text measurement needs the font subsystem, which is not up before the app is
            // initialised. Fall back to a rough estimate rather than failing the layout.
            return text.Length * 6.5;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        return true;
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
