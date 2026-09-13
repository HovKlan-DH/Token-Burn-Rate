using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
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
    private bool _isOverBudget;
    private bool _isUnlimited;

    private string _accent = "";

    /// <summary>
    /// The panel's accent, as the colour string the fill uses while inside its allowance.
    ///
    /// It lives on the bar rather than in the template because it is the only thing that
    /// separated the three per-panel templates; carrying it here lets all three rows share
    /// one template instead of three copies differing by a single literal.
    ///
    /// Settable rather than init-only: a colour override picked from the state file is
    /// applied to bars already constructed, at startup, not baked in at creation time.
    /// </summary>
    public required string Accent
    {
        get => _accent;
        set
        {
            if (!Set(ref _accent, value)) return;
            OnPropertyChanged(nameof(FillColour));
        }
    }

    /// <summary>
    /// Whether the caption should turn red along with the bar.
    ///
    /// False for Claude, whose caption is a reset time rather than a percentage - colouring
    /// it would read as a problem with the reset itself. True where the caption is the
    /// overspend figure, which is exactly what the colour is reporting.
    /// </summary>
    public bool WarnCaption { get; init; } = true;

    public string Label { get => _label; set => Set(ref _label, value); }
    public string ValueText { get => _valueText; set => Set(ref _valueText, value); }
    public string DetailText { get => _detailText; set => Set(ref _detailText, value); }
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }

    /// <summary>
    /// Whether this bar reports an unlimited quota, which prints "∞" rather than a
    /// percentage. Not a percentage at all, so the tray ring's "max" source excludes it -
    /// see ResolveIconState.
    /// </summary>
    public bool IsUnlimited { get => _isUnlimited; set => Set(ref _isUnlimited, value); }

    /// <summary>
    /// Whether this bar has passed its allowance. The bar itself saturates at full, so
    /// without this the difference between spending exactly the budget and spending nearly
    /// four times it is a percentage in small grey text - which is what it looked like.
    /// </summary>
    public bool IsOverBudget
    {
        get => _isOverBudget;
        set
        {
            if (!Set(ref _isOverBudget, value)) return;
            WarningColoursChanged();
        }
    }

    /// <summary>
    /// Announces every colour derived from the row's warning state. Both inputs to that state
    /// - <see cref="IsOverBudget"/> and <see cref="IsAheadOfPace"/> - raise the same set, so
    /// they share one method: two hand-kept lists would drift the moment a fifth colour was
    /// added to one and not the other, and the symptom would be a bar painting the wrong
    /// colour until some unrelated property happened to change.
    /// </summary>
    private void WarningColoursChanged()
    {
        OnPropertyChanged(nameof(FillColour));
        OnPropertyChanged(nameof(CaptionColour));
        OnPropertyChanged(nameof(CaptionHighlightColour));
        OnPropertyChanged(nameof(ValueColour));
    }

    // The three colours a row is drawn in, resolved here rather than in the template. The
    // rule is one line each and identical for all three panels, which is what lets the
    // panels share a single template instead of repeating the same converter twelve times.

    /// <summary>
    /// The two states that redden a row, treated as one: the allowance is spent, or the
    /// spending has outrun the calendar (see <see cref="IsAheadOfPace"/>). Both mean the same
    /// thing to someone glancing at the widget - more has gone than should have by now - so
    /// they are drawn identically rather than given two reds to tell apart.
    /// </summary>
    private bool IsWarning => _isOverBudget || IsAheadOfPace;

    /// <summary>Red past the allowance or ahead of pace, the panel's own accent inside both.</summary>
    public string FillColour => IsWarning ? OverColour : Accent;

    /// <summary>
    /// Red only where the caption is the figure that went over - see <see cref="WarnCaption"/>.
    ///
    /// Deliberately keyed on <see cref="IsOverBudget"/> alone rather than the full warning
    /// state: the caption carrying red is what the "⚠" prefix accompanies, and that prefix is
    /// written from the spent test at the point the caption is composed. Reddening here for a
    /// bar that is merely ahead of pace would print a red caption with no warning sign beside
    /// it, indistinguishable from a genuine overspend except by reading the numbers.
    /// </summary>
    public string CaptionColour => _isOverBudget && WarnCaption ? OverColour : MutedColour;

    /// <summary>
    /// The colour for the emphasised runs of the caption - on Claude, the reset figures.
    ///
    /// Lifted out of the muted grey the rest of the caption sits in, but held back from
    /// the full white the value uses so the percentage stays the loudest thing in the row.
    /// It follows the red where the caption reddens, since a two-tone caption there would
    /// read as two separate states.
    /// </summary>
    public string CaptionHighlightColour
        => _isOverBudget && WarnCaption ? OverColour : HighlightColour;

    /// <summary>The percentage or count, which reddens on every panel.</summary>
    public string ValueColour => IsWarning ? OverColour : TextColour;

    /// <summary>Red, chosen to stay legible on the dark background the widget uses.</summary>
    private const string OverColour = "#F85149";
    private const string MutedColour = "#6B7079";
    private const string HighlightColour = "#B9BDC6";
    private const string TextColour = "#E8EAED";

    public double Fraction
    {
        get => _fraction;
        set
        {
            if (!Set(ref _fraction, value)) return;
            OnPropertyChanged(nameof(Percent));
            PaceChanged();
        }
    }

    public double Percent => Fraction * 100;

    private IReadOnlyList<double>? _markers;

    /// <summary>
    /// Pacing tick marks along the bar, as fractions 0-1 - currently only populated on the
    /// My Pace week bar and Claude's WEEK bar, one per workday in the week, spanning the
    /// whole week rather than only the days elapsed so far. See <see cref="TodayMarkerIndex"/>
    /// for which one is "today"; null on every other bar, where the track is left plain.
    /// </summary>
    public IReadOnlyList<double>? Markers => _markers;

    private int _todayMarkerIndex = -1;

    /// <summary>
    /// Index into <see cref="Markers"/> that stands for "today". Negative means there is no
    /// "today" to mark and every tick draws muted - a weekly window that has not started
    /// yet. An index past the last entry is not an error either: on the week's final workday
    /// the boundary is the bar's own right edge, which already marks it.
    /// </summary>
    public int TodayMarkerIndex => _todayMarkerIndex;

    /// <summary>
    /// Sets the tick marks and which of them is "today" as one change.
    ///
    /// Read-only individually and written only through here because
    /// <see cref="IsAheadOfPace"/> is derived from both together: two separate setters each
    /// announcing their own change would publish an intermediate state - the new list paired
    /// with the old index - and a shrinking list makes that pairing out of range, so the pace
    /// colour resolves against a boundary that belongs to neither poll.
    /// </summary>
    public void SetMarkers(IReadOnlyList<double>? markers, int todayIndex)
    {
        var changed = !ReferenceEquals(_markers, markers) || _todayMarkerIndex != todayIndex;
        if (!changed) return;

        _markers = markers;
        _todayMarkerIndex = todayIndex;

        OnPropertyChanged(nameof(Markers));
        OnPropertyChanged(nameof(TodayMarkerIndex));
        PaceChanged();
    }

    /// <summary>
    /// Whether the fill has run past the midnight that ends today - the whole allowance this
    /// far into the window is already spent, even though the window itself has room left.
    ///
    /// The marker is the boundary the day in progress runs out at (see
    /// <see cref="TodayMarkerIndex"/>), so a fill beyond it is spending that has outrun the
    /// calendar rather than the budget. Bars with no "today" to mark - every bar outside the
    /// two week views, and a window whose day in progress is bounded by the bar's own edge -
    /// have no pace to be ahead of and report false.
    /// </summary>
    public bool IsAheadOfPace
    {
        get
        {
            var markers = _markers;
            if (markers is null) return false;
            if (_todayMarkerIndex < 0 || _todayMarkerIndex >= markers.Count) return false;

            var marker = markers[_todayMarkerIndex];
            if (double.IsNaN(marker) || double.IsInfinity(marker)) return false;

            // Must match UsageBar.DrawMarkers' clamp of the same fraction: that one decides
            // where the tick is drawn, this one decides what the fill is compared against,
            // and they have to be the same number. Out of range they would disagree - the
            // tick pinned to the bar's edge while this tested the raw value - reddening a row
            // whose fill visibly falls short of the tick. Change one, change the other.
            marker = Math.Clamp(marker, 0, 1);

            return _fraction > marker;
        }
    }

    /// <summary>
    /// Raised whenever an input to <see cref="IsAheadOfPace"/> changes - the pace half of the
    /// warning state, announced through the same shared list the spent half uses.
    /// </summary>
    private void PaceChanged()
    {
        OnPropertyChanged(nameof(IsAheadOfPace));
        WarningColoursChanged();
    }

    /// <summary>
    /// The reset instant the current <see cref="Markers"/> were derived from, on the bars
    /// that have a rolling window (Claude's WEEK). Kept so the accented "today" tick can be
    /// recomputed when the clock crosses midnight without a poll - see
    /// MainViewModel.RefreshDayMarkers. Null on every bar whose ticks are not date-derived.
    /// </summary>
    public DateTimeOffset? MarkerWindowEnd { get; set; }

    /// <summary>
    /// The instant this bar's caption counts down to, kept so the countdown can be re-derived
    /// between polls - see MainViewModel.RefreshResetCaptions.
    ///
    /// The caption is a live figure ("resets in 4h 55m") but <see cref="DetailText"/> holds a
    /// string, so without this it is frozen at whatever the last poll rendered. That is not a
    /// small window: once every active limit is spent the Claude poll is suppressed until the
    /// reset (see MainViewModel._claudeSkipUntil), which on the week bar can be days - so an
    /// idle widget would sit on a countdown hours out of date.
    ///
    /// Null whenever the caption must not animate: bars whose caption is not a countdown
    /// (both Copilot panels), and Claude bars whose figures have gone stale behind a failed
    /// poll - a live countdown over stale percentages would keep running past its own reset
    /// and then claim "resetting" indefinitely, asserting something the panel cannot know.
    /// </summary>
    public DateTimeOffset? ResetsAt { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(n);
        return true;
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ClaudeLimitsService _claudeLimits = new();
    private readonly CopilotUsageService _copilot = new();
    private readonly CopilotPacingService _pacing = new();

    /// <summary>
    /// The status behind the last successful Copilot poll, kept only so changing "Workdays
    /// in a week" can recompute the pacing bars immediately instead of waiting for the next
    /// poll cycle.
    /// </summary>
    private CopilotStatus? _lastCopilotStatus;

    private DateTimeOffset _nextRefresh = DateTimeOffset.UtcNow;

    /// <summary>
    /// Set once every active Claude limit is spent, to the earliest of their reset times -
    /// there is nothing left to poll for until then, and the endpoint behind this call is
    /// undocumented and rate-limits hard (see CLAUDE.md), so a maxed-out account should not
    /// keep hitting it every cadence tick. Null means poll normally. Cleared the moment a
    /// poll actually runs past the gate, so a wrong or stale skip cannot wedge the panel.
    /// </summary>
    private DateTimeOffset? _claudeSkipUntil;

    /// <summary>The plan name from the last poll that actually ran, so a skipped one can
    /// still rebuild the subtitle around a freshly parsed token count.</summary>
    private string? _claudePlan;

    private bool _isLoading = true;

    // Whether the poll that just finished actually found each service available - false
    // until a real result has come in, unlike ClaudeAvailable/CopilotAvailable/
    // PacingAvailable's backing fields, which start true (Claude, Copilot) so their panels
    // can show placeholder dashes before the first poll. Used only for NothingToShow.
    private bool _claudeFound;
    private bool _copilotFound;
    private bool _pacingFound;

    /// <summary>
    /// Set when the poll that just finished failed to reach Claude for a reason a few more
    /// seconds could fix - see <see cref="ClaudeLimitsStatus.IsTransientFailure"/> - as
    /// opposed to "not signed in", which polling again sooner cannot help. Folded into
    /// <see cref="NothingToShow"/> so a boot-time race that only knocks out Claude
    /// (Copilot's endpoint came up first, say) still gets the fast retry: the old
    /// all-or-nothing check stood the back-off down the moment any one service had data,
    /// which is exactly the case that left the Claude panel missing until the next
    /// full-interval poll or a manual restart.
    /// </summary>
    private bool _claudeTransientFailure;

    /// <summary>
    /// Whether signing in from this app would fix the Claude panel - set from
    /// ClaudeLimitsStatus.CanSignIn. Drives the panel's sign-in button, and is what makes
    /// the panel useful on a machine that has never had Claude Code on it.
    /// </summary>
    private bool _claudeCanSignIn;

    /// <summary>Re-entry guard for the sign-in dialog, which is modal but launched from two places.</summary>
    private bool _claudeSignInRunning;

    /// <summary>
    /// Whether any poll this run has returned real limits. Gates keeping stale bars up
    /// through an expired session: with nothing ever polled there is nothing to keep, and
    /// the panel has to fall back to the explainer.
    /// </summary>
    private bool _claudeHasPolled;

    /// <summary>How long the current back-off is, or null when the last poll found data.</summary>
    private TimeSpan? _retryDelay;

    /// <summary>
    /// How soon to retry after a first poll that found nothing. Short enough that a boot
    /// which just beat the network recovers while the user is still looking at the widget,
    /// long enough not to hammer a service that is genuinely down - and it doubles from
    /// here anyway.
    /// </summary>
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The placeholder's only text. IsLoading covers exactly the gap before the first poll
    /// has ever finished (see its doc comment), and clears unconditionally the moment that
    /// poll completes - so by the time a retry could be scheduled the placeholder is
    /// already gone and has nothing left to say differently.
    /// </summary>
    private const string InitialLoadingText = "Wait - loading usage data…";

    private string _countdown = "";
    private string _claudeSubtitle = "";
    private string _copilotSubtitle = "";
    private bool _isRefreshing;
    private bool _claudeVisible = true;
    private bool _copilotVisible = true;
    private bool _copilotNeedsSignIn;
    private bool _signInRunning;

    /// <summary>
    /// When the device code currently on screen stops being usable. The sign-in guard is
    /// scoped to this rather than to <see cref="_signInRunning"/> alone: the flow polls
    /// GitHub for the code's full lifetime (15 minutes by default) and nothing can cancel it,
    /// so a user who starts a sign-in and walks away would otherwise freeze the Copilot panel
    /// for that whole window - every refresh short-circuiting to protect a code that expired
    /// minutes ago.
    /// </summary>
    private DateTimeOffset _signInCodeExpiry;

    private bool _pacingVisible;
    private string _pacingSubtitle = "";
    // Kept apart from CopilotSubtitle so the pacing panel can borrow it when the Copilot
    // panel is not on screen to show it. Holds the bare phrase - "resets Mar 3" - with no
    // leading space or separator: both readers append it through AppendReset, which owns
    // the separator, rather than each relying on whitespace baked into the format string.
    private string _copilotReset = "";
    private int _pacingDaysLeft;
    private bool _claudeCollapsed;
    private bool _copilotCollapsed;
    private bool _pacingCollapsed;
    private bool _claudeHidden;
    private bool _copilotHidden;
    private bool _pacingHidden;
    private bool _autostartEnabled;

    /// <summary>
    /// Set once the user has toggled autostart themselves, so the startup probe - which
    /// runs off-thread and may land afterwards - cannot overwrite their choice.
    /// </summary>
    private bool _autostartTouched;
    private bool _pinned = true;
    private bool _closeToTray = true;
    private bool _traySupported;
    private bool _autoUpdate = true;
    private bool _updateIncludeAlpha;
    private bool _updateIncludeBeta;

    /// <summary>Which bar the tray ring follows and what colour it draws in - see ResolveIconState.</summary>
    private string _iconSource = "max";
    private string _iconColour = "accent";
    private double _labelWidth = 86;
    private string _signInText = "";
    private string _signInCode = "";

    // One default accent per panel, matching the section headings in the XAML. They live
    // here because the bars carry them: the three bar templates were identical but for
    // these. A user override (see ResolveColors) replaces the value every bar and header
    // actually reads, but this is still what "default" in the state file resolves to.
    private const string DefaultClaudeAccent = "#D97757";
    private const string DefaultCopilotAccent = "#58A6FF";
    private const string DefaultPacingAccent = "#3FB950";

    /// <summary>Red, matching BarViewModel's own OverColour so the header dot reads as the
    /// same "trouble" colour as an over-budget bar rather than an unrelated shade.</summary>
    private const string StatusDotRed = "#F85149";

    private string _claudeAccentColor = DefaultClaudeAccent;
    private string _copilotAccentColor = DefaultCopilotAccent;
    private string _pacingAccentColor = DefaultPacingAccent;

    /// <summary>
    /// The color the Claude panel is drawn in: its section header, chevron, and every bar's
    /// fill while inside budget. "default" in the state file resolves to
    /// <see cref="DefaultClaudeAccent"/>; anything else must be a valid #RRGGBB, corrected
    /// the same way an invalid icon source is - see ResolveColors.
    /// </summary>
    public string ClaudeAccentColor { get => _claudeAccentColor; private set => Set(ref _claudeAccentColor, value); }

    /// <summary>The GitHub Copilot panel's equivalent of <see cref="ClaudeAccentColor"/>.</summary>
    public string CopilotAccentColor { get => _copilotAccentColor; private set => Set(ref _copilotAccentColor, value); }

    /// <summary>The My Pace panel's equivalent of <see cref="ClaudeAccentColor"/>.</summary>
    public string PacingAccentColor { get => _pacingAccentColor; private set => Set(ref _pacingAccentColor, value); }

    /// <summary>
    /// A Claude bar, whose caption is a reset time and so is left uncoloured when the
    /// limit is full - see <see cref="BarViewModel.WarnCaption"/>.
    /// </summary>
    private BarViewModel NewClaudeBar(string label)
        => new() { Label = label, ValueText = "—", Accent = ClaudeAccentColor, WarnCaption = false };

    public ObservableCollection<BarViewModel> ClaudeBars { get; } = new();
    public ObservableCollection<BarViewModel> CopilotBars { get; } = new();
    /// <summary>The user's own pacing view: how much of today's share of credits is spent.</summary>
    public ObservableCollection<BarViewModel> PacingBars { get; } = new();

    /// <summary>
    /// Which bar the tray ring follows: "max" for whichever is highest, or one of the named
    /// sources in <see cref="ValidIconSources"/>. Settable from the tray's right-click menu
    /// as well as the state file, so a choice made there is persisted the same way.
    /// </summary>
    public string IconSource
    {
        get => _iconSource;
        set
        {
            if (!IsValidSource(value) || _iconSource == value) return;
            _iconSource = value;
            OnPropertyChanged(nameof(IconSource));
            try
            {
                // Only the source is this setter's to change. Writing _iconColour alongside
                // it would publish whatever that field happens to hold, and it holds its
                // default until ResolveIconConfig runs - so a source chosen from the tray
                // before the state file is read would overwrite an explicit ring colour with
                // "accent". Reading the colour back off the state being updated keeps the
                // two settings independent whatever order they are touched in.
                AppState.Update(a => a.Icon = new AppState.IconState
                {
                    Source = value,
                    Colour = a.Icon?.Colour ?? _iconColour,
                });
            }
            catch (Exception ex)
            {
                // A read-only or malformed file must never stop the app from running; the
                // chosen source still governs this run, it just is not recorded.
                AppLog.Warn($"Settings: could not persist icon source - {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Whether a named icon source currently resolves to a usable bar - drives
    /// whether the matching tray menu entry is shown at all.</summary>
    public bool IsIconSourceAvailable(string source) => ResolveNamedIconSource(source) is not null;

    /// <summary>
    /// One pass over the bars answering both questions the tray asks each refresh: which
    /// named sources are currently available, and which bar the ring should draw.
    ///
    /// The two used to be asked separately - IsIconSourceAvailable once per menu entry, then
    /// IconSourceBar - so a refresh re-ran the same switch and the same linear label scans
    /// nine or ten times over to compute a set that changes only when a bar's IsEnabled or
    /// IsUnlimited flips. Resolving each source once and handing back both answers together
    /// costs one traversal.
    /// </summary>
    public (IReadOnlyDictionary<string, bool> Available, BarViewModel? Source) ResolveIconState()
    {
        var available = new Dictionary<string, bool>(StringComparer.Ordinal);
        BarViewModel? named = null;

        foreach (var (id, _) in IconSourceNames)
        {
            if (id == "max") continue;      // always offered; resolves through MaxIconSource

            var bar = ResolveNamedIconSource(id);
            available[id] = bar is not null;
            if (bar is not null && string.Equals(id, _iconSource, StringComparison.OrdinalIgnoreCase))
                named = bar;
        }

        return (available, named ?? MaxIconSource());
    }

    /// <summary>
    /// The colour the tray ring should draw the source bar in. "accent" (the default) takes
    /// the bar's own FillColour, which also carries the red it flips to past 100% or when the
    /// bar runs ahead of pace; an explicit #RRGGBB overrides the accent but not a spent
    /// allowance - past 100% the ring is red whatever this setting says.
    ///
    /// Deliberately gated on IsOverBudget rather than the bar's full warning state: a spent
    /// quota is worth overriding a colour the user chose, while merely being ahead of pace -
    /// which the week bars report for much of any normal week - is not.
    /// </summary>
    public string ResolveIconColour(BarViewModel source)
    {
        if (source.IsOverBudget) return source.FillColour;
        return string.Equals(_iconColour, "accent", StringComparison.OrdinalIgnoreCase)
            ? source.FillColour
            : _iconColour;
    }

    private BarViewModel? ResolveNamedIconSource(string source) => source switch
    {
        "claude.session" => UsableBar(ClaudeVisible && ClaudeAvailable ? ClaudeBarByLabel("SESSION") : null),
        "claude.week" => UsableBar(ClaudeVisible && ClaudeAvailable ? ClaudeBarByLabel("WEEK") : null),
        "copilot.completions" => UsableBar(CopilotVisible ? CopilotBarAt(0) : null),
        "copilot.chat" => UsableBar(CopilotVisible ? CopilotBarAt(1) : null),
        "copilot.premium" => UsableBar(CopilotVisible ? CopilotBarAt(2) : null),
        "pacing.day" => UsableBar(PacingVisible ? PacingBarAt(0) : null),
        "pacing.week" => UsableBar(PacingVisible ? PacingBarAt(1) : null),
        "pacing.month" => UsableBar(PacingVisible ? PacingBarAt(2) : null),
        _ => null,     // "max" and anything unrecognised resolve through MaxIconSource
    };

    private BarViewModel? ClaudeBarByLabel(string label)
    {
        foreach (var bar in ClaudeBars)
            if (string.Equals(bar.Label, label, StringComparison.OrdinalIgnoreCase))
                return bar;
        return null;
    }

    private BarViewModel? CopilotBarAt(int index) => index < CopilotBars.Count ? CopilotBars[index] : null;
    private BarViewModel? PacingBarAt(int index) => index < PacingBars.Count ? PacingBars[index] : null;

    /// <summary>A named bar counts only while it is enabled and reports a real percentage.</summary>
    private static BarViewModel? UsableBar(BarViewModel? bar) =>
        bar is { IsEnabled: true, IsUnlimited: false } ? bar : null;

    /// <summary>The highest fraction among all visible, enabled, non-unlimited bars.</summary>
    private BarViewModel? MaxIconSource()
    {
        BarViewModel? best = null;

        if (ClaudeVisible && ClaudeAvailable) best = Highest(best, ClaudeBars);
        if (CopilotVisible) best = Highest(best, CopilotBars);
        if (PacingVisible) best = Highest(best, PacingBars);

        return best;

        static BarViewModel? Highest(BarViewModel? running, ObservableCollection<BarViewModel> bars)
        {
            foreach (var bar in bars)
            {
                if (UsableBar(bar) is not { } usable) continue;
                if (running is null || usable.Fraction > running.Fraction) running = usable;
            }
            return running;
        }
    }

    /// <summary>Plain-language time until the next automatic refresh.</summary>
    public string Countdown { get => _countdown; private set => Set(ref _countdown, value); }
    public string ClaudeSubtitle { get => _claudeSubtitle; set => Set(ref _claudeSubtitle, value); }
    public string CopilotSubtitle { get => _copilotSubtitle; set => Set(ref _copilotSubtitle, value); }
    public bool IsRefreshing { get => _isRefreshing; set => Set(ref _isRefreshing, value); }

    // A panel shows only when the service has data AND the user has not hidden it. The two
    // are tracked separately so a refresh cannot overwrite the user's choice.

    /// <summary>
    /// Set by the refresh: does this service have anything to show? Unlike
    /// CopilotAvailable/PacingAvailable this no longer gates ClaudeVisible (see its own
    /// comment) - it only decides whether the panel shows bars or the install/sign-in
    /// explainer, so a change re-measures bar widths (the two layouts differ) rather than
    /// going through VisibilityChanged, which exists for panels that actually appear or
    /// disappear.
    /// </summary>
    public bool ClaudeAvailable
    {
        get => _claudeVisible;
        set
        {
            if (Set(ref _claudeVisible, value))
            {
                OnPropertyChanged(nameof(ClaudeNeedsSignIn));
                ClaudeBodyChanged();
                OnBarsChanged();
            }
            StopLoading();
        }
    }

    /// <summary>
    /// True when Claude has nothing to show, so the panel should offer the explainer and
    /// the sign-in button instead of bars.
    /// </summary>
    public bool ClaudeNeedsSignIn => !_claudeVisible;

    /// <summary>
    /// The two things the Claude panel's body can be: its bars, or the sign-in explainer
    /// standing in for them. Both gate on ClaudeExpanded, so collapsing the header hides
    /// whichever is showing - an explainer that ignored it would leave the panel refusing
    /// to collapse while its chevron claimed it had.
    /// </summary>
    public bool ClaudeBarsVisible => ClaudeExpanded && !ClaudeNeedsSignIn;
    public bool ClaudeInstallHintVisible => ClaudeExpanded && ClaudeNeedsSignIn;

    /// <summary>
    /// What the panel says in place of its bars.
    ///
    /// Tracks <see cref="ClaudeSignInVisible"/> rather than assuming a sign-in is always the
    /// answer: the panel also stands empty when a poll simply could not reach Anthropic -
    /// the commonest case being the very first poll at boot, before the network is up - and
    /// telling that user to sign in would be wrong twice over. They may already be signed in,
    /// and the button that text points at is hidden, leaving an instruction with nothing to
    /// act on. That pairing is the reason both halves are raised together in
    /// <see cref="ClaudeExplainerChanged"/>.
    /// </summary>
    public string ClaudeExplainerText => _claudeCanSignIn
        ? "Sign in to see your Claude usage here. This is a one-time sign-in for this machine - "
          + "right-click this window to hide the Claude panel instead."
        : "Claude usage is unavailable right now - this usually clears on its own within a "
          + "few seconds. Right-click this window to hide the Claude panel.";

    /// <summary>
    /// Whether to offer the Claude sign-in - the only way the panel ever gets a token, so
    /// it shows whenever there is nothing to display.
    /// </summary>
    public bool ClaudeSignInVisible => _claudeCanSignIn;

    /// <summary>
    /// Announces the panel's sign-in affordance whenever the input behind it changes.
    /// </summary>
    private void ClaudeExplainerChanged()
    {
        OnPropertyChanged(nameof(ClaudeExplainerText));
        OnPropertyChanged(nameof(ClaudeSignInVisible));
    }

    /// <summary>
    /// Announces both halves of the Claude panel's body at once. They are complements of
    /// the same two inputs, so every caller that changes either input has to raise both -
    /// routed through here so a later edit cannot update one and leave the other stale.
    /// </summary>
    private void ClaudeBodyChanged()
    {
        OnPropertyChanged(nameof(ClaudeBarsVisible));
        OnPropertyChanged(nameof(ClaudeInstallHintVisible));
    }

    /// <summary>
    /// Announces both halves of the Copilot panel's body at once - the Copilot analogue of
    /// <see cref="ClaudeBodyChanged"/>, for the same reason: CopilotBarsVisible and
    /// CopilotSignInHintVisible are complements of the same two inputs (CopilotExpanded,
    /// CopilotNeedsSignIn), so every caller that changes either has to raise both.
    /// </summary>
    private void CopilotBodyChanged()
    {
        OnPropertyChanged(nameof(CopilotBarsVisible));
        OnPropertyChanged(nameof(CopilotSignInHintVisible));
    }

    public bool CopilotAvailable
    {
        get => _copilotVisible;
        set
        {
            if (Set(ref _copilotVisible, value)) VisibilityChanged(nameof(CopilotVisible));
            StopLoading();
        }
    }
    public bool CopilotNeedsSignIn
    {
        get => _copilotNeedsSignIn;
        set
        {
            if (!Set(ref _copilotNeedsSignIn, value)) return;

            // Both, not just the first: this swaps the panel's body between its bars and the
            // sign-in explainer, which changes which labels are on screen, and LabelWidth is
            // shared across all three panels - so the Claude and Pacing bars would otherwise
            // keep an indent earned by COMPLETIONS/PREMIUM labels that are no longer drawn.
            // ClaudeAvailable's setter pairs the same two calls for the same reason.
            CopilotBodyChanged();
            OnBarsChanged();
        }
    }

    /// <summary>
    /// The two things the Copilot panel's body can be: its bars, or the sign-in explainer
    /// standing in for them - the same split ClaudeBarsVisible/ClaudeInstallHintVisible make
    /// for the Claude panel. Both gate on CopilotExpanded, so collapsing the header hides
    /// whichever is showing.
    ///
    /// Before this, "not signed in" showed the bars anyway - three rows of "n/a" placeholder
    /// dashes sitting above the sign-in button - which read as a broken panel rather than an
    /// unconfigured one, especially beside the Claude panel's clear explainer for the same
    /// situation on a machine with nothing signed in at all.
    /// </summary>
    public bool CopilotBarsVisible => CopilotExpanded && !CopilotNeedsSignIn;
    public bool CopilotSignInHintVisible => CopilotExpanded && CopilotNeedsSignIn;

    /// <summary>What the panel says in place of its bars while signed out.</summary>
    public string CopilotExplainerText =>
        "Sign in to see your GitHub Copilot usage here. This is a one-time sign-in for this machine.";

    public string SignInText { get => _signInText; set => Set(ref _signInText, value); }
    /// <summary>
    /// The device-flow code on its own, separate from <see cref="SignInText"/> so the
    /// button can render "Code:" and the code itself in different weights - empty outside
    /// the code-shown state.
    /// </summary>
    public string SignInCode { get => _signInCode; set => Set(ref _signInCode, value); }
    public bool PacingAvailable
    {
        get => _pacingVisible;
        set
        {
            if (Set(ref _pacingVisible, value)) VisibilityChanged(nameof(PacingVisible));
            StopLoading();
        }
    }

    /// <summary>
    /// Clears the loading placeholder the instant any service reports a real result -
    /// called unconditionally from every *Available setter, not just when the value
    /// changes: ClaudeAvailable and CopilotAvailable start true (see the field
    /// initialisers) so their panels can show "—" placeholder dashes before the first poll,
    /// so a poll that confirms "yes, available" would otherwise be a same-value write that
    /// Set() treats as a no-op and never reaches here.
    ///
    /// Called from inside the same setter that just recorded the result, not from a
    /// continuation on the refresh task: Claude and Copilot poll concurrently on their own
    /// schedules, so waiting for the whole poll (or even for a task continuation queued
    /// after it) to clear this would leave whichever service resolves first painting real
    /// data for one dispatcher pass with the placeholder still covering it.
    /// </summary>
    private void StopLoading()
    {
        if (_isLoading) IsLoading = false;
    }

    /// <summary>What the UI actually binds to.</summary>
    // !_isLoading gates all three: ClaudeAvailable/CopilotAvailable default true from
    // construction (see the field initialisers) so that once loading ends, whichever panel
    // already has data shows immediately rather than waiting for a property no poll would
    // otherwise touch - but that same default must not let a panel's empty skeleton (header
    // plus bare bar tracks, no figures) render underneath the loading placeholder before
    // the first poll has answered. Panel and placeholder are drawn from the same IsLoading
    // flag so they are never both visible: see IsLoading's own doc comment for why it
    // clears atomically with the data that makes a panel worth showing.
    //
    // Claude, unlike Copilot and pacing, stays visible even when the service has nothing to
    // show: not being signed in yet is the single most likely reason someone opens this
    // widget and sees an empty window, and a panel that only appears once the sign-in is
    // already done is the worst possible place to offer it (see ClaudeNeedsSignIn below,
    // which drives the explainer and button shown in its place).
    public bool ClaudeVisible => !_isLoading && !_claudeHidden;
    public bool CopilotVisible => !_isLoading && _copilotVisible && !_copilotHidden;
    public bool PacingVisible => !_isLoading && _pacingVisible && !_pacingHidden;

    /// <summary>
    /// True until whichever service reports in first - Claude or Copilot - has set its own
    /// availability for the first time.
    ///
    /// Deliberately not derived from panel visibility. Claude and Copilot default their
    /// panels visible from construction (see the field initialisers below) precisely so
    /// they can show "—" dash placeholders before the first poll rather than popping into
    /// existence when it lands - so ClaudeVisible/CopilotVisible already read true on a
    /// cold, unpolled window, and a flag built on "is any panel visible" would clear itself
    /// at construction, before there is any real data, and never get to cover the gap it
    /// exists for.
    ///
    /// Cleared by <see cref="StopLoading"/>, called from inside the ClaudeAvailable/
    /// CopilotAvailable/PacingAvailable setters themselves - not from a continuation
    /// scheduled after the refresh task, and not by RefreshAsync as a whole once both
    /// services join. Claude and Copilot poll concurrently and rarely land together: a
    /// flag cleared any later than the setter itself would leave whichever answers first
    /// painting real numbers for at least one dispatcher pass with the placeholder still
    /// covering it, which is the flicker this exists to avoid.
    ///
    /// Cleared once and never set again: a later refresh that finds nothing new has
    /// previous figures on screen to leave standing, so nothing here needs covering.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            // ClaudeVisible/CopilotVisible/PacingVisible all gate on this too (see their
            // own comment), so their bindings need telling the instant it changes - nothing
            // else would otherwise touch those properties at the one moment the placeholder
            // hands off to whichever panel just got its first data. The status dot gates on
            // it as well (see AnyAccountConnected), and StopLoading can clear this from the
            // first *Available setter to answer, before RefreshAsync's own announcement.
            if (Set(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(ClaudeVisible));
                OnPropertyChanged(nameof(CopilotVisible));
                OnPropertyChanged(nameof(PacingVisible));
                StatusDotChanged();
            }
        }
    }

    /// <summary>
    /// What the placeholder says. Fixed text, not a settable property: IsLoading is only
    /// ever true before the first poll, so there is only ever one thing worth saying here.
    /// </summary>
    public string LoadingText => InitialLoadingText;

    /// <summary>
    /// True when the poll that just finished found nothing anywhere - every service either
    /// failed or came back empty - or when Claude specifically hit a failure worth
    /// fast-retrying on its own (see <see cref="_claudeTransientFailure"/>). Set explicitly
    /// from the three services' own results (see <see cref="_claudeFound"/> and its
    /// siblings) rather than read off a panel's IsVisible, which defaults true before any
    /// poll ever runs so the panel can show its placeholder dashes - a check built on that
    /// would never see "nothing" even when a poll genuinely failed everywhere. Drives only
    /// the fast-retry back-off in <see cref="ScheduleRetryIfNothingFound"/>; IsLoading does
    /// not depend on this at all - see its own doc comment.
    /// </summary>
    private bool NothingToShow => (!_claudeFound && !_copilotFound && !_pacingFound) || _claudeTransientFailure;

    /// <summary>
    /// True while at least one service has real data - the header dot's colour (see
    /// <see cref="StatusDotColor"/>). Built on the same <see cref="_claudeFound"/>/
    /// <see cref="_copilotFound"/>/<see cref="_pacingFound"/> flags as
    /// <see cref="NothingToShow"/> rather than a panel's IsVisible, for the same reason:
    /// those default true before the first poll so the panels can show placeholder dashes,
    /// so a dot built on them would claim a connection the app has not actually made yet.
    ///
    /// <see cref="IsLoading"/> is folded in because those three flags default false, which
    /// is indistinguishable from "every poll failed": without it the dot would sit red from
    /// window creation until the first poll lands, accusing both services of failing before
    /// either had been asked. Treating the pre-poll gap as connected keeps the dot quiet
    /// until there is a real result to report.
    ///
    /// Unlike NothingToShow this ignores _claudeTransientFailure - that flag is about retry
    /// pacing, not about whether an account is connected, and a transient Claude hiccup
    /// should not turn the dot red while Copilot is still reporting fine.
    /// </summary>
    public bool AnyAccountConnected => _isLoading || _claudeFound || _copilotFound || _pacingFound;

    /// <summary>
    /// The header dot's colour: green while any account has data, red once a completed poll
    /// has found none anywhere.
    /// </summary>
    public string StatusDotColor => AnyAccountConnected ? DefaultPacingAccent : StatusDotRed;

    public string StatusDotTip => AnyAccountConnected
        ? "At least one account is connected"
        : "No account connected - Claude and Copilot both failed to report usage";

    /// <summary>
    /// Announces all three status-dot properties at once. They are three views of the same
    /// inputs, so every caller that changes one changes all of them - routed through here so
    /// a later edit cannot update one and leave the others stale.
    /// </summary>
    private void StatusDotChanged()
    {
        OnPropertyChanged(nameof(AnyAccountConnected));
        OnPropertyChanged(nameof(StatusDotColor));
        OnPropertyChanged(nameof(StatusDotTip));
    }

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

        if (value && WouldHideEverything(name))
        {
            // Refused, but the menu's two-way IsChecked has already cleared the tick on the
            // way in. Raise the unchanged property so the binding reads the field back and
            // the checkmark returns - otherwise the item looks unticked while its panel is
            // still on screen.
            //
            // Routed through the same notifier the accepted path uses, so whatever a hide
            // is made to affect in future is refreshed here too rather than only in the
            // branch someone remembered to update.
            HiddenChanged(name!, accepted: false);
            return false;
        }

        field = value;
        HiddenChanged(name!, accepted: true);
        return true;
    }

    /// <summary>
    /// Announces the state of one hide flag and does everything else a hide affects.
    /// Called for an accepted change and for a refused one alike: a refusal has to repaint
    /// the tick just as an acceptance does, and the two differ only in
    /// <paramref name="accepted"/> - whether the field actually moved and so whether the
    /// new value is worth writing down.
    ///
    /// Persistence lives here rather than in the caller so this really is the one place a
    /// hide's effects are listed. With it in the accepted branch instead, the first thing a
    /// hide was made to affect already sat outside the shared path this exists to be.
    /// </summary>
    private void HiddenChanged(string name, bool accepted)
    {
        OnPropertyChanged(name);
        if (accepted) PersistHidden();
    }

    /// <summary>True when hiding the named panel would leave no panel visible.</summary>
    private bool WouldHideEverything(string? name)
    {
        var claude = ClaudeVisible && name != nameof(ClaudeHidden);
        var copilot = CopilotVisible && name != nameof(CopilotHidden);
        var pacing = PacingVisible && name != nameof(PacingHidden);
        return !claude && !copilot && !pacing;
    }

    private void VisibilityChanged(string visibleName)
    {
        OnPropertyChanged(visibleName);
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
        ClaudeBodyChanged();
        CopilotBodyChanged();
    }

    // Collapsing hides a panel's bars but keeps its header, so the panel can be reopened.
    public bool ClaudeCollapsed
    {
        get => _claudeCollapsed;
        set
        {
            if (!Set(ref _claudeCollapsed, value)) return;
            OnPropertyChanged(nameof(ClaudeExpanded));
            ClaudeBodyChanged();
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
            CopilotBodyChanged();
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
            // Compared against what the getter reports, not against the raw field. Those
            // differ whenever there is no tray, and comparing the field would let a
            // write-back of the gated value be swallowed as "no change" while the stored
            // preference quietly said the opposite.
            if (CloseToTray == value) return;
            if (!TraySupported) return;     // nothing to minimise to; the menu item is hidden

            _closeToTray = value;
            OnPropertyChanged(nameof(CloseToTray));
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

    /// <summary>Dimmed when unpinned, so the state reads at a glance. The pin glyph itself is
    /// the ThumbtackIcon geometry in MainWindow.axaml; brightness is the whole of the state
    /// it shows, deliberately - see that resource for why there is no second, slashed icon.
    /// </summary>
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

            // Whatever the startup probe comes back with, the user has now said otherwise.
            _autostartTouched = true;

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
    /// Whether the widget checks for and silently applies updates at all (see
    /// Services/UpdateService.cs). Defaults to on. This gates the update check only - the
    /// mailscan.dk "ping home" (see CheckInService) is unrelated telemetry and always runs
    /// regardless of this setting.
    /// </summary>
    public bool AutoUpdate
    {
        get => _autoUpdate;
        set
        {
            if (!Set(ref _autoUpdate, value)) return;
            AppState.Update(a => a.AutoUpdate = value);
        }
    }

    /// <summary>
    /// Whether the update check (see Services/UpdateService.cs) will offer an alpha build.
    /// Defaults to off: only a real (bare X.Y.Z) release is offered until the user opts in.
    /// Independent of <see cref="UpdateIncludeBeta"/> - each checkbox is its own gate, so
    /// checking ALPHA alone offers alpha and release builds but not beta, and both can be
    /// checked together to widen further. UpdateService.MaxTierRequested folds the two back
    /// into a single tier ceiling.
    /// </summary>
    public bool UpdateIncludeAlpha
    {
        get => _updateIncludeAlpha;
        set
        {
            if (!Set(ref _updateIncludeAlpha, value)) return;
            AppState.Update(a => a.UpdateIncludeAlpha = value);
        }
    }

    /// <summary>
    /// Whether the update check will offer a beta build. Defaults to off. Independent of
    /// <see cref="UpdateIncludeAlpha"/> - see that property for why.
    /// </summary>
    public bool UpdateIncludeBeta
    {
        get => _updateIncludeBeta;
        set
        {
            if (!Set(ref _updateIncludeBeta, value)) return;
            AppState.Update(a => a.UpdateIncludeBeta = value);
        }
    }

    // ---- font scale ----------------------------------------------------------------------

    /// <summary>The widget's as-designed size - what "Reset text size" returns to.</summary>
    private const double DefaultFontScale = 1.0;

    private const double MinFontScale = 0.75;
    private const double MaxFontScale = 2.0;
    private const double FontScaleStep = 0.125;

    private double _fontScale = DefaultFontScale;

    /// <summary>
    /// Uniform scale applied to the whole widget from the context menu's "Make bigger" /
    /// "Make smaller", for anyone who finds the default text too small - or too large - to
    /// read comfortably.
    ///
    /// Applied as a single render transform on the window's content (see MainWindow.axaml)
    /// rather than per-control FontSize bindings: the styles in Window.Styles set FontSize
    /// as literal pixel values, and Grid.ColumnDefinitions cannot be bound in Avalonia (see
    /// CLAUDE.md) - the bar's own layout would need rebuilding to react to a scale otherwise.
    /// A render transform scales every control, including the custom-drawn bars, from one
    /// number with no per-control wiring.
    /// </summary>
    public double FontScale
    {
        get => _fontScale;
        private set
        {
            if (!Set(ref _fontScale, value)) return;
            OnPropertyChanged(nameof(CanIncreaseFontScale));
            OnPropertyChanged(nameof(CanDecreaseFontScale));
        }
    }

    /// <summary>Whether "Make bigger" has anywhere left to go - greys the menu item at the ceiling.</summary>
    public bool CanIncreaseFontScale => _fontScale < MaxFontScale - 1e-9;

    /// <summary>Whether "Make smaller" has anywhere left to go - greys the menu item at the floor.</summary>
    public bool CanDecreaseFontScale => _fontScale > MinFontScale + 1e-9;

    public void IncreaseFontScale() => SetFontScale(_fontScale + FontScaleStep);
    public void DecreaseFontScale() => SetFontScale(_fontScale - FontScaleStep);
    public void ResetFontScale() => SetFontScale(DefaultFontScale);

    private void SetFontScale(double value)
    {
        FontScale = Math.Clamp(value, MinFontScale, MaxFontScale);
        AppState.Update(a => a.FontScale = FontScale);
    }

    // ---- workdays per week ----------------------------------------------------------------

    private int _workDaysPerWeek = BusinessDays.DefaultWorkDaysPerWeek;

    /// <summary>
    /// How many days of the week count as workdays for "My Pace" - the first N days
    /// starting Monday (see <see cref="BusinessDays"/>), set from the context menu's
    /// "Workdays in a week". Replaces the previous hardcoded Monday-Friday week.
    /// </summary>
    public int WorkDaysPerWeek
    {
        get => _workDaysPerWeek;
        set
        {
            var clamped = Math.Clamp(value, BusinessDays.MinWorkDaysPerWeek, BusinessDays.MaxWorkDaysPerWeek);
            if (!Set(ref _workDaysPerWeek, clamped)) return;
            AppState.Update(a => a.WorkDaysPerWeek = _workDaysPerWeek);
            for (int n = BusinessDays.MinWorkDaysPerWeek; n <= BusinessDays.MaxWorkDaysPerWeek; n++)
                OnPropertyChanged(WorkDaysCheckedProperty(n));

            // Recompute immediately from the last known status rather than waiting for the
            // next poll - otherwise the menu selection would appear to do nothing until the
            // next refresh cycle. Claude's rolling week bar is unaffected: its markers are
            // real calendar-day boundaries, not workday counts - see RollingWeekTodayMarkers.
            if (_lastCopilotStatus is { } status) RefreshPacing(status);
        }
    }

    private static string WorkDaysCheckedProperty(int n) => n switch
    {
        1 => nameof(IsWorkDays1),
        2 => nameof(IsWorkDays2),
        3 => nameof(IsWorkDays3),
        4 => nameof(IsWorkDays4),
        5 => nameof(IsWorkDays5),
        6 => nameof(IsWorkDays6),
        _ => nameof(IsWorkDays7),
    };

    /// <summary>
    /// Backs the "Workdays in a week" items 1-7 in the context menu. They are CheckBox
    /// items, so clicking the one already ticked pushes false rather than being swallowed
    /// the way a radio group would: <see cref="SelectWorkDays"/> is what puts the tick back.
    /// </summary>
    public bool IsWorkDays1 { get => _workDaysPerWeek == 1; set => SelectWorkDays(1, value); }
    public bool IsWorkDays2 { get => _workDaysPerWeek == 2; set => SelectWorkDays(2, value); }
    public bool IsWorkDays3 { get => _workDaysPerWeek == 3; set => SelectWorkDays(3, value); }
    public bool IsWorkDays4 { get => _workDaysPerWeek == 4; set => SelectWorkDays(4, value); }
    public bool IsWorkDays5 { get => _workDaysPerWeek == 5; set => SelectWorkDays(5, value); }
    public bool IsWorkDays6 { get => _workDaysPerWeek == 6; set => SelectWorkDays(6, value); }
    public bool IsWorkDays7 { get => _workDaysPerWeek == 7; set => SelectWorkDays(7, value); }

    /// <summary>
    /// Applies a click on one of the workday items. Ticking one selects it; unticking the
    /// one already selected is not a real choice - there is no "no workdays" state - so the
    /// value is left alone and the property re-announced, which snaps the checkmark the menu
    /// just cleared back on. Without that echo the binding keeps its own false and the
    /// submenu sits with nothing ticked while pacing still uses the unchanged value.
    /// </summary>
    private void SelectWorkDays(int days, bool isChecked)
    {
        if (isChecked) WorkDaysPerWeek = days;
        else OnPropertyChanged(WorkDaysCheckedProperty(days));
    }

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
        PacingSubtitle = CopilotVisible ? days : AppendReset(days);
    }

    /// <summary>
    /// Appends the Copilot reset phrase to a subtitle, separator and all, or returns the
    /// subtitle unchanged when there is no reset date to show.
    ///
    /// The one place the separator is written. Both subtitles used to concatenate it by
    /// hand against a leading space inside the format string, so the spacing was correct
    /// only as long as three literals across three methods agreed on it.
    /// </summary>
    private string AppendReset(string subtitle) =>
        _copilotReset.Length == 0 ? subtitle : $"{subtitle} · {_copilotReset}";

    public MainViewModel()
    {
        foreach (var label in new[] { "SESSION", "WEEK" })
            ClaudeBars.Add(NewClaudeBar(label));
        foreach (var label in new[] { "COMPLETIONS", "CHAT", "PREMIUM" })
            CopilotBars.Add(new BarViewModel { Label = label, ValueText = "—", Accent = CopilotAccentColor });
        foreach (var label in new[] { "DAY", "WEEK", "MONTH" })
            PacingBars.Add(new BarViewModel { Label = label, ValueText = "—", Accent = PacingAccentColor });
    }

    /// <summary>
    /// Polls both services. <paramref name="force"/> bypasses the Claude skip-until-reset
    /// gate (see <see cref="_claudeSkipUntil"/>): a background timer tick should not spend
    /// a call on an account that cannot have changed, but a refresh the user asked for
    /// directly - the button, or bringing the window back from the tray - might be checking
    /// for exactly the kind of out-of-band change (a plan upgrade, an admin reset) the gate
    /// cannot know about, so it always reaches the API.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default, bool force = false)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var claudeTask = RefreshClaudeAsync(ct, force);
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
            // Counted from when the work finished, not when it started. A refresh that
            // overruns the interval would otherwise leave the countdown sitting at zero
            // while the timer tick it collided with was turned away by the guard above -
            // the display promising 60 seconds and taking up to 120.
            _nextRefresh = DateTimeOffset.UtcNow + RefreshInterval;
            IsRefreshing = false;

            // Backstop: StopLoading, called from every *Available setter (see IsLoading's
            // doc comment), is the normal way this clears - as soon as whichever service
            // answers first sets its own availability. This covers the path that cannot
            // reach a setter at all - an exception thrown before either service got that
            // far - so the placeholder still cannot outlive the one poll it exists to cover.
            IsLoading = false;

            // _claudeFound/_copilotFound/_pacingFound all just settled above, so this is
            // the one place a poll can change what the dot should show.
            StatusDotChanged();

            ScheduleRetryIfNothingFound();
        }
    }

    /// <summary>
    /// Arms a fast retry when the poll that just finished found nothing anywhere, or when
    /// Claude alone hit a transient failure, or stands the back-off down once neither is
    /// true - see <see cref="NothingToShow"/>.
    ///
    /// The app starts with the session, so the first poll usually runs before the network
    /// is up. That can knock out every service, or just one of them - Copilot and Claude
    /// hit different hosts and rarely fail together - so a boot-time race that only cost
    /// Claude its connection still needs to be caught here rather than waiting out a full
    /// refresh interval with the panel missing. RetryDue asks the window to come back
    /// sooner; the interval doubles each time so a machine that is genuinely offline settles
    /// onto the normal cadence instead of polling forever.
    /// </summary>
    private void ScheduleRetryIfNothingFound()
    {
        if (!NothingToShow)
        {
            _retryDelay = null;
            return;
        }

        var next = _retryDelay is { } previous
            ? TimeSpan.FromTicks(previous.Ticks * 2)
            : FirstRetryDelay;

        // Never past the normal cadence: at that point the ordinary timer is the retry, and
        // a longer gap than the user asked for would be a worse promise than no retry.
        if (next > RefreshInterval) next = RefreshInterval;

        _retryDelay = next;
        _nextRefresh = DateTimeOffset.UtcNow + next;
    }

    private async Task RefreshClaudeAsync(CancellationToken ct, bool force)
    {
        // Every active limit was spent last poll and none of them reset yet: there is
        // nothing new to learn from the API, so the call is skipped rather than hammering an
        // endpoint documented as rate-limiting hard on an account whose bars cannot move.
        // The bars are left exactly as the last real poll drew them, reset caption included.
        // A forced refresh (the button, or restoring from the tray) always goes through, in
        // case something changed the gate cannot know about - a plan upgrade, an admin reset.
        //
        // The gate stops at the API call. The transcript parse below is local file I/O
        // against no rate limit at all, and the token figure it produces is precisely what
        // keeps moving while the percentage bars are pinned: a spent limit is one window,
        // and tokens spent against another are still worth counting. Skipping it would
        // freeze the one number on the panel still capable of changing.
        var skipped = !force && _claudeSkipUntil is { } skipUntil && DateTimeOffset.UtcNow < skipUntil;

        if (!skipped)
        {
            _claudeSkipUntil = null;

            // Bars come from Anthropic's own utilization figures, which is the only source
            // that agrees with the Usage screen: the limits use fixed reset windows and a
            // ceiling that is not published, so neither can be reconstructed from transcripts.
            var limits = await _claudeLimits.GetLimitsAsync(ct).ConfigureAwait(true);

            if (limits.IsAvailable)
            {
                SyncClaudeBars(ClaudeBars, limits.Limits.Count);
                for (int i = 0; i < limits.Limits.Count; i++)
                {
                    var l = limits.Limits[i];
                    var bar = ClaudeBars[i];

                    // Markers before Fraction, so every read of the pace colours in between
                    // sees one poll's values rather than this poll's fraction against the
                    // last one's boundary. Both are set inside a single dispatcher pass, so
                    // this orders the notifications, not the painting.
                    var isWeekly = IsWeeklyLimit(l.Kind);
                    if (isWeekly)
                    {
                        var (markers, todayIndex) = RollingWeekTodayMarkers(l.ResetsAt);
                        bar.SetMarkers(markers, todayIndex);
                        bar.MarkerWindowEnd = l.ResetsAt;
                    }
                    else
                    {
                        bar.SetMarkers(null, -1);
                        bar.MarkerWindowEnd = null;
                    }

                    bar.Label = l.Label;
                    bar.Fraction = l.Fraction;
                    bar.ValueText = $"{l.Percent:0}%";
                    bar.DetailText = l.ResetText;
                    bar.ResetsAt = l.ResetsAt;
                    bar.IsEnabled = true;

                    // No warning glyph here: this caption is the reset time, not a percentage,
                    // so a "⚠" in front of it would read as a problem with the reset. The bar
                    // and the percentage still turn red, which is where a full limit shows.
                    //
                    // Tested on the rounded figure, not the raw one: the percentage is printed
                    // to the nearest whole number, so 99.6 shows as "100%" and would otherwise
                    // sit there in white - the one reading the colour is there to explain.
                    bar.IsOverBudget = IsSpent(l.Percent);
                }

                _claudeSkipUntil = NextClaudePoll(limits.Limits);

                // Only a successful poll updates the remembered plan name - a transient
                // failure (Unavailable always carries Plan == "") must not blank out the
                // last plan the panel showed.
                _claudePlan = limits.Plan;
            }

            // Offer the sign-in only while the panel has nothing of its own to show. A
            // successful poll never needs it, and a failure that kept its stale bars (below)
            // would otherwise put a sign-in button under figures still on screen.
            _claudeCanSignIn = !limits.IsAvailable && limits.CanSignIn;

            ClaudeExplainerChanged();

            // Bars already on screen are worth more than an explainer standing where they
            // were. A transient failure - the network down, a refresh that could not reach
            // Anthropic - says nothing about the figures last polled: they were true when
            // read and the token renews itself once the connection is back. So a blip under
            // an idle window keeps its bars and reports the staleness in the subtitle
            // instead of replacing a full panel with a sign-in prompt. Only a failure that
            // actually needs the user - or one with nothing behind it - gives the panel over
            // to the explainer.
            var keepStaleBars = !limits.IsAvailable && limits.IsTransientFailure && _claudeHasPolled;

            // Stop the countdown on bars the panel is only still showing because the poll
            // failed. The figures beside it are frozen at the last successful read, and a
            // caption that kept counting would run past its own reset and then sit on
            // "resetting" forever - claiming a reset the panel has no way to have seen.
            if (keepStaleBars)
                foreach (var bar in ClaudeBars) bar.ResetsAt = null;

            ClaudeAvailable = limits.IsAvailable || keepStaleBars;
            _claudeFound = limits.IsAvailable;
            _claudeTransientFailure = !limits.IsAvailable && limits.IsTransientFailure;
            if (limits.IsAvailable) _claudeHasPolled = true;
            ClaudeSubtitle = limits.IsAvailable ? (_claudePlan ?? "") : (limits.Error ?? "");
        }
        else
        {
            // Gate skipped this poll: whatever the previous poll found still stands, so
            // this is not "nothing" for the retry back-off's purposes.
            _claudeFound = ClaudeAvailable;
            _claudeTransientFailure = false;
            ClaudeSubtitle = _claudePlan ?? "";
        }
    }

    /// <summary>
    /// When every active Claude limit is spent, the earliest of their reset times - the
    /// moment before which polling again cannot possibly show anything new. Null when at
    /// least one active limit still has room, or when a spent limit's reset time is
    /// unknown: skipping needs a concrete time to resume at, never an open-ended guess.
    /// </summary>
    private static DateTimeOffset? NextClaudePoll(IReadOnlyList<ClaudeLimit> limits)
    {
        DateTimeOffset? earliest = null;

        foreach (var l in limits)
        {
            if (!l.IsActive) continue;
            if (!IsSpent(l.Percent)) return null;      // still room on this one - poll as normal
            if (l.ResetsAt is not { } resets) return null;

            if (earliest is null || resets < earliest) earliest = resets;
        }

        return earliest;
    }

    /// <summary>
    /// Fills the pacing bars: today's share of the remaining credits, the same for the
    /// current week, and the period total. Hidden when no bucket meters credits.
    /// </summary>
    private void RefreshPacing(CopilotStatus status)
    {
        _lastCopilotStatus = status;
        var pacing = _pacing.Build(status, DateTime.Now, _workDaysPerWeek);
        if (pacing is null)
        {
            // Clear the bars as well as hiding the panel. They are reused, so a day that
            // went over budget before the credit bucket dropped out would keep its red
            // fill and its "⚠ 377% of today" caption, ready to be shown as current the
            // moment pacing became available again.
            PacingAvailable = false;
            _pacingFound = false;
            foreach (var bar in PacingBars) Reset(bar);
            return;
        }

        PacingAvailable = true;
        _pacingFound = true;
        // The per-day allowance is already the Day bar's denominator, so it is not repeated
        // here; what the bars cannot show is how many days that allowance is spread over.
        _pacingDaysLeft = pacing.BusinessDaysLeft;
        UpdatePacingSubtitle();

        Set(PacingBars[0], pacing.DayFraction, pacing.DayPercent,
            pacing.UsedToday, pacing.PerDayAllowance, "today");

        // Markers before Set, which assigns Fraction - the same ordering the Claude week bar's
        // poll uses, and for the same reason: the pace colours are derived from the fraction
        // and the today marker together (see BarViewModel.IsAheadOfPace).
        PacingBars[1].SetMarkers(
            WeekMarkers(pacing.WorkdaysInWeek), pacing.WorkdayIndexInWeek - 1);
        Set(PacingBars[1], pacing.WeekFraction, pacing.WeekPercent,
            pacing.UsedThisWeek, pacing.WeekBudget, "this week");

        Set(PacingBars[2], pacing.MonthFraction, pacing.MonthPercent,
            pacing.UsedThisPeriod, pacing.Entitlement, "this month");

        static void Set(BarViewModel bar, double fraction, double percent, double used, double budget, string what)
        {
            // Spending past the allowance is meaningful, so the number keeps climbing even
            // though the bar itself stops at full.
            bar.Fraction = Math.Clamp(fraction, 0, 1);
            bar.ValueText = $"{percent:0}%";

            // At or past the allowance the caption turns red and carries a warning sign.
            // The bar cannot show this on its own: it saturates at full, so 100% and 377%
            // draw identically, and the overspend was legible only in the small print.
            var over = IsSpent(percent);
            bar.IsOverBudget = over;
            bar.DetailText = $"{Warning(over)}{used:0} of {budget:0} tokens used {what}";
            bar.IsEnabled = true;
        }
    }

    /// <summary>
    /// One marker per workday boundary in the week, Monday through the day before the
    /// week's last workday - the days still ahead included, not only those elapsed so far,
    /// so the whole week's shape is visible against the fill. The very last boundary is
    /// skipped: it always sits exactly at the bar's own right edge, which already marks it,
    /// so a tick there (red on the last workday included - see
    /// <see cref="BarViewModel.TodayMarkerIndex"/>, set by the caller) would be redundant.
    /// </summary>
    private static IReadOnlyList<double>? WeekMarkers(int workdaysInWeek)
    {
        var count = workdaysInWeek - 1;
        if (count <= 0) return null;

        var markers = new double[count];
        for (var i = 0; i < count; i++)
            markers[i] = (double)(i + 1) / workdaysInWeek;
        return markers;
    }

    /// <summary>Whether a Claude limit's Kind is the rolling 7-day window - see ClaudeLimitsService.LabelFor.</summary>
    private static bool IsWeeklyLimit(string kind) => kind is "weekly_all" or "seven_day";

    /// <summary>
    /// Builds markers for Claude's rolling 7-day window, spanning the whole window - days
    /// still ahead included - on the same "spend it within your work week" reading as
    /// Copilot's My Pace; see <see cref="WeekMarkers"/>. Unlike that grid, this one carries
    /// no workday count: Claude's window is calendar days, not a configurable work week.
    ///
    /// Ticks fall on every local midnight strictly inside the window, so the separators are
    /// calendar day boundaries - the thing a person actually means by "yesterday" - rather
    /// than multiples of whatever clock time the account happens to reset at.
    ///
    /// That makes the first and last segments short, and deliberately so: a window running
    /// Saturday 18:00 to Saturday 18:00 reads as 6h (Sat evening) + 24h x6 (Sun..Fri) + 18h
    /// (Sat until the reset) - eight segments from seven ticks. Spacing the ticks 24h apart
    /// from the reset instead was tried and is wrong: it draws six evenly spaced separators
    /// at 18:00 each day, which are not day boundaries at all and leave the bar unable to
    /// answer "how much did I spend yesterday".
    ///
    /// Each midnight is rebuilt from its own calendar date with that date's own UTC offset,
    /// rather than by repeatedly adding 24h: the day a DST transition falls on is 23 or 25
    /// real hours, so a fixed step would drift every later tick off midnight. The window
    /// start is likewise derived in UTC - DateTimeOffset.AddDays would carry the end's
    /// offset backwards unchanged and land an hour off across a transition, which silently
    /// dropped a whole separator through the "> start" filter below.
    ///
    /// Every marker is a midnight; "now" is never one of them. The accented marker is the
    /// midnight that *ends* today - tonight's boundary - so on a Sunday the red tick sits
    /// between Sunday and Monday, marking where the day in progress runs out. An earlier
    /// version inserted "now" as an extra marker and accented that, which put the red line
    /// at the current clock time: it drifted every poll, was not a day boundary, and left
    /// the bar with one more separator than the window has days. This matches the My Pace
    /// week bar, where TodayMarkerIndex likewise selects one of the existing boundaries
    /// rather than adding one.
    ///
    /// The index is -1 when tonight's midnight is not a tick on this bar: on the window's
    /// final day it falls past the reset, and during the window's first partial day it has
    /// not been reached yet. In both cases the day in progress is bounded by the bar's own
    /// edge rather than by a separator, so every tick draws muted.
    /// </summary>
    private static (IReadOnlyList<double>? Markers, int TodayIndex) RollingWeekTodayMarkers(
        DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } reset) return (null, -1);

        var end = reset.ToLocalTime();
        var start = TimeZoneInfo.ConvertTime(
            new DateTimeOffset(end.UtcDateTime.AddDays(-7), TimeSpan.Zero), TimeZoneInfo.Local);

        var span = (end - start).Ticks;
        if (span <= 0) return (null, -1);

        // Local midnights strictly inside (start, end) - the endpoints are the bar's own
        // edges already and need no tick of their own.
        var ticks = new List<double>(7);
        for (var day = start.Date.AddDays(1); day < end.DateTime.AddDays(1); day = day.AddDays(1))
        {
            var midnight = LocalMidnight(day);
            if (midnight <= start) continue;
            if (midnight >= end) break;

            ticks.Add((midnight - start).Ticks / (double)span);
        }

        // The midnight that *ends* today - tonight's boundary, the right edge of the day in
        // progress. On a Sunday that is the Sun->Mon tick, so the accent sits where today
        // stops rather than where it began.
        //
        // Compared as instants rather than fractions: the tick list is built from instants
        // too, so matching on the same basis avoids a rounding difference deciding which
        // side of a boundary "tonight" falls on.
        var tonight = LocalMidnight(DateTimeOffset.Now.LocalDateTime.Date.AddDays(1));

        var todayIndex = -1;
        for (var i = 0; i < ticks.Count; i++)
        {
            var tick = start.AddTicks((long)Math.Round(ticks[i] * span));
            if (tick == tonight) { todayIndex = i; break; }
        }

        // No match on the window's last day: tonight's midnight falls past the reset, so it
        // is not a tick on this bar at all and the day in progress simply runs to the bar's
        // own right edge. The same is true before the first midnight of a window that starts
        // mid-day. Both leave every tick muted, which is what -1 means.
        return (ticks, todayIndex);
    }

    /// <summary>
    /// Midnight at the start of <paramref name="date"/>, carrying the UTC offset that
    /// actually applies on that date rather than one inherited from another day.
    ///
    /// On the day a DST transition lands, local midnight may not exist at all (spring
    /// forward in zones that shift at 00:00, e.g. parts of South America): TimeZoneInfo
    /// reports such a time as invalid, and the first valid instant of that day is what the
    /// bar should tick at instead.
    /// </summary>
    private static DateTimeOffset LocalMidnight(DateTime date)
    {
        var tz = TimeZoneInfo.Local;
        var midnight = date.Date;

        if (tz.IsInvalidTime(midnight))
        {
            // Skipped-over local time: walk forward to the first minute that exists.
            for (var i = 1; i <= 180; i++)
            {
                var candidate = midnight.AddMinutes(i);
                if (!tz.IsInvalidTime(candidate))
                    return new DateTimeOffset(candidate, tz.GetUtcOffset(candidate));
            }
        }

        // An ambiguous local time (autumn fall-back) resolves to the earlier of the two
        // instants, which GetUtcOffset already returns - the standard-time one.
        return new DateTimeOffset(midnight, tz.GetUtcOffset(midnight));
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

            _signInCodeExpiry = DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn);
            SignInText = "Code:";
            SignInCode = code.UserCode;
            CopilotSubtitle = "waiting for browser approval…";
            TryOpenBrowser(code.VerificationUri);

            var token = await auth.PollForTokenAsync(code, ct).ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(token))
            {
                AppLog.Info("GitHub: sign-in cancelled or timed out");
                SignInText = "Sign in to GitHub";
                SignInCode = "";
                CopilotSubtitle = "sign-in cancelled or timed out";
                return;
            }

            AppLog.Info("GitHub: sign-in approved, token saved");
            GitHubDeviceAuth.SaveToken(token);
            _copilot.InvalidateToken();

            // Cleared before the refresh, not in the finally below: RefreshCopilotAsync
            // skips its whole body while this is set (see the guard there), so leaving it
            // set until the finally would make the one refresh that actually has a token to
            // use do nothing - the panel would sit on the consumed code and "waiting for
            // browser approval…" until a timer tick up to RefreshInterval later. There is
            // nothing left to protect at this point: the code is spent and the token saved.
            _signInRunning = false;
            await RefreshCopilotAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("GitHub: sign-in failed", ex);
            SignInText = "Sign in to GitHub";
            SignInCode = "";
            CopilotSubtitle = "sign-in failed: " + ex.Message;
        }
        finally
        {
            _signInRunning = false;
        }
    }

    /// <summary>
    /// Runs this app's own Claude sign-in, so the panel works on a machine that has never
    /// had Claude Code installed.
    ///
    /// The dialog is modal and owns the OAuth attempt (see ClaudeSignInWindow), so this is
    /// only the plumbing around it: guard against a second dialog, then refresh the panel
    /// immediately on success rather than leaving the user looking at the explainer until
    /// the next poll comes round.
    ///
    /// <paramref name="showDialog"/> is injected rather than constructed here so the view
    /// model keeps no reference to a Window - the same separation every other dialog in
    /// this app uses (see MainWindow's colour-picker wiring).
    /// </summary>
    public async Task SignInToClaudeAsync(Func<Task<bool>> showDialog, CancellationToken ct = default)
    {
        if (_claudeSignInRunning) return;
        _claudeSignInRunning = true;
        try
        {
            if (!await showDialog().ConfigureAwait(true))
            {
                AppLog.Info("Claude: sign-in dialog cancelled");
                return;
            }

            AppLog.Info("Claude: sign-in approved, token saved");

            // A fresh sign-in invalidates every reason the panel had to be skipping polls:
            // the gate below is keyed on limits that were read with a token we no longer
            // use, and a forced refresh is what the user just asked for by signing in.
            _claudeSkipUntil = null;
            await RefreshClaudeAsync(ct, force: true).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("Claude: sign-in failed", ex);
            ClaudeSubtitle = "sign-in failed: " + ex.Message;
        }
        finally
        {
            _claudeSignInRunning = false;
        }
    }

    /// <summary>
    /// The running build's version, shown as a non-interactive entry in the context menu
    /// now that the app updates itself silently (see Services/UpdateService.cs) - without
    /// this there was no on-screen way to tell which build was actually running.
    /// Read from InformationalVersion (the csproj's human-facing "0.1.0-alpha.5", not the
    /// three-part AssemblyVersion) via reflection, since that is the same value Velopack
    /// packages under.
    /// </summary>
    public static string VersionText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            // SourceLink stamps a "+<git-sha>" build-metadata suffix onto InformationalVersion
            // at compile time - that's not part of what the csproj declares, so it is trimmed
            // back off rather than shown as though it were part of the version.
            if (version is not null)
            {
                var plusIndex = version.IndexOf('+');
                if (plusIndex >= 0) version = version[..plusIndex];
            }

            return string.IsNullOrWhiteSpace(version) ? "Version unknown" : $"Version {version}";
        }
    }

    /// <summary>The project's home, opened from the context menu.</summary>
    public const string ProjectUrl = "https://github.com/HovKlan-DH/Token-Burn-Rate";

    /// <summary>Opens the project page in the default browser.</summary>
    public void OpenProjectPage() => TryOpenBrowser(ProjectUrl);

    /// <summary>Opens the folder holding the running executable, in the OS file browser.</summary>
    public void OpenApplicationFolder() => OpenFolder(ApplicationFolder);

    /// <summary>
    /// Opens the folder AppState.Path writes the state file into, in the OS file browser -
    /// the same folder CrashLog drops its logs beside. That is the executable's own folder
    /// for a portable copy, but for a Velopack install (where the exe's folder is a
    /// versioned, unwritable app-x.y.z directory) it is %LOCALAPPDATA%/~/.local/share instead - so
    /// this must follow AppState's resolution rather than assuming beside-the-exe.
    /// </summary>
    public void OpenConfigurationFolder() => OpenFolder(ConfigurationFolder);

    /// <summary>The running executable's own folder.</summary>
    private static string? ApplicationFolder =>
        System.IO.Path.GetDirectoryName(Environment.ProcessPath);

    /// <summary>The folder AppState.Path writes the state file into - see OpenConfigurationFolder.</summary>
    private static string? ConfigurationFolder =>
        System.IO.Path.GetDirectoryName(Services.AppState.Path);

    /// <summary>
    /// Whether the configuration folder needs its own menu entry - false whenever it is the
    /// same folder as the application's (a portable copy), which is the common case outside
    /// of a Velopack install.
    /// </summary>
    public bool ConfigurationFolderDiffers =>
        !string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(ApplicationFolder ?? string.Empty),
            System.IO.Path.TrimEndingDirectorySeparator(ConfigurationFolder ?? string.Empty),
            StringComparison.Ordinal);

    /// <summary>
    /// Opens a folder in the OS file browser, or a file in whatever the OS treats its
    /// extension as - ShellExecute (UseShellExecute) does not distinguish the two.
    /// </summary>
    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // No associated application on this desktop, or the path is gone: nothing to
            // open, nothing worth surfacing to a monitor.
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

    /// <summary>
    /// Returns a bar to the state a bar with nothing to show should be in.
    ///
    /// Bars are reused across refreshes, so every field a previous poll set has to be
    /// cleared - the over-budget flag above all. Leaving it set is what made a service that
    /// dropped out keep the red fill and the "⚠ 100% used" caption it earned minutes ago,
    /// with "n/a" printed over the top of them.
    /// </summary>
    private static void Reset(BarViewModel bar)
    {
        bar.ValueText = "n/a";
        bar.DetailText = "";
        bar.Fraction = 0;
        bar.IsEnabled = false;
        bar.IsOverBudget = false;
        bar.IsUnlimited = false;
        bar.SetMarkers(null, -1);
        bar.MarkerWindowEnd = null;
        bar.ResetsAt = null;
    }

    /// <summary>
    /// Whether a percentage counts as spent, judged on the figure actually printed.
    ///
    /// Every caption formats with "0", so 99.6 reaches the screen as "100%". Testing the
    /// raw value would leave that reading sitting in white while claiming to be full,
    /// which is the one case the colour exists to explain.
    /// </summary>
    private static bool IsSpent(double percent) => Math.Round(percent, MidpointRounding.AwayFromZero) >= 100;

    /// <summary>The caption's warning prefix, kept apart so the format string is written once.</summary>
    private static string Warning(bool over) => over ? "⚠ " : "";

    /// <summary>
    /// Grows or shrinks the Claude bar list so it matches however many limits the API
    /// returned. Only Claude needs this: the other two panels have a fixed set of rows.
    /// </summary>
    private void SyncClaudeBars(ObservableCollection<BarViewModel> bars, int count)
    {
        while (bars.Count < count) bars.Add(NewClaudeBar(""));
        while (bars.Count > count) bars.RemoveAt(bars.Count - 1);
    }

    private async Task RefreshCopilotAsync(CancellationToken ct)
    {
        // A device-code sign-in is showing the code and polling GitHub for approval; this
        // is a background timer tick that runs concurrently with it (see RefreshAsync's
        // Task.WhenAll). Without this guard, the tick's "needs sign-in" branch below
        // overwrites SignInText back to "Sign in to GitHub" mid-flow, wiping the code off
        // screen seconds after it appeared even though nothing has actually changed.
        //
        // Skipped, not failed: whatever the previous poll found still stands, so the found
        // flags are re-asserted rather than left stale - RefreshAsync still runs
        // ScheduleRetryIfNothingFound afterwards, which would otherwise score this poll on
        // figures no longer being maintained. Same reasoning as RefreshClaudeAsync's own
        // skip path.
        //
        // Bounded by the code's own expiry - see _signInCodeExpiry - so an abandoned sign-in
        // stops holding the panel once there is no live code left to protect.
        if (_signInRunning && DateTimeOffset.UtcNow < _signInCodeExpiry)
        {
            _copilotFound = CopilotAvailable;
            _pacingFound = PacingAvailable;
            return;
        }

        var status = await _copilot.GetStatusAsync(ct).ConfigureAwait(true);

        if (!status.IsAvailable)
        {
            // Keep the panel when a sign-in would fix it, so the button has somewhere to
            // live; hide it outright for anything else.
            CopilotNeedsSignIn = status.NeedsSignIn;
            CopilotAvailable = status.NeedsSignIn;
            CopilotSubtitle = status.Error ?? "unavailable";
            SignInText = "Sign in to GitHub";
            SignInCode = "";
            _copilotReset = "";
            PacingAvailable = false;

            // Not status.NeedsSignIn: the panel stays visible so the sign-in button has
            // somewhere to live, but there is still no actual quota data behind it, so this
            // poll found nothing for the retry back-off's purposes either way.
            _copilotFound = false;
            _pacingFound = false;
            _lastCopilotStatus = null;
            foreach (var bar in CopilotBars) Reset(bar);
            foreach (var bar in PacingBars) Reset(bar);
            return;
        }

        CopilotNeedsSignIn = false;
        CopilotAvailable = true;
        _copilotFound = true;

        // On a work machine the plan is org-assigned, so show which org grants it as well
        // as the plan tier; on a personal account there is no org and the tier stands alone.
        var plan = status.Organizations.Count > 0
            ? $"{status.Organizations[0]} · {status.Plan}"
            : status.Plan;
        _copilotReset = status.ResetDate is { } d ? $"resets {d:MMM d}" : "";

        CopilotSubtitle = AppendReset(plan);

        UpdatePacingSubtitle();

        for (int i = 0; i < CopilotBars.Count; i++)
        {
            var bar = CopilotBars[i];
            // Bars are reused across refreshes, so the over-budget flag has to be cleared
            // here too - otherwise a bucket that drops out of the response keeps the red
            // it earned on the previous poll.
            if (i >= status.Quotas.Count)
            {
                Reset(bar);
                continue;
            }

            var q = status.Quotas[i];
            bar.Label = q.Label;

            if (q.Unlimited)
            {
                bar.ValueText = "∞";
                bar.DetailText = "unlimited";
                bar.Fraction = 0;
                bar.IsEnabled = true;
                bar.IsOverBudget = false;
                bar.IsUnlimited = true;
            }
            else if (!q.HasQuota || q.Entitlement <= 0)
            {
                // Not every plan grants every bucket: the free tier has no premium
                // interactions, so the bar is dimmed rather than shown as an empty quota.
                bar.ValueText = "—";
                bar.DetailText = "not included in plan";
                bar.Fraction = 0;
                bar.IsEnabled = false;
                bar.IsOverBudget = false;
                bar.IsUnlimited = false;
            }
            else
            {
                // A quota at 100% is spent, not merely nearly spent, and that is worth
                // seeing at a glance rather than reading off the small print.
                var spent = IsSpent(q.Percent);
                bar.ValueText = $"{q.Percent:0}%";
                bar.DetailText = $"{Warning(spent)}{q.Used:0} of {q.Entitlement:0} used";
                bar.Fraction = q.Fraction;
                bar.IsEnabled = true;
                bar.IsUnlimited = false;
                bar.IsOverBudget = spent;
            }
        }

        RefreshPacing(status);
    }

    /// <summary>
    /// The poll cadence used when the state file says nothing.
    ///
    /// Three minutes rather than one because of what is on the other end. Anthropic's
    /// usage endpoint is undocumented, publishes no limit and no Retry-After, and is
    /// widely reported to 429 at a minute and then stay 429 for hours - and a rate-limited
    /// OAuth token can need a re-login to clear, which would disrupt the CLI on the
    /// machine, not just this widget. GitHub is nowhere near its 5,000/hour: 180s is the
    /// price of keeping one timer for both, and it costs nothing to read, since the
    /// figures behind every bar move over hours.
    /// </summary>
    private const int DefaultRefreshSeconds = 180;

    /// <summary>
    /// How long between automatic refreshes of each service.
    ///
    /// Read once at startup from "refreshSeconds" in the state file, which is not surfaced
    /// anywhere in the UI - it is an escape hatch for the odd machine that wants a gentler
    /// or tighter poll, not a setting. Read once rather than per tick because the timer
    /// interval is fixed when it is created, so re-reading would have no effect anyway.
    ///
    /// Clamped to 5s..1h: below that the Copilot call and the transcript parse would still
    /// be running when the next tick arrived, and above it the countdown stops being
    /// meaningful. A missing, zero or unparseable value falls back to the default. Within
    /// that range the file is obeyed as written, including values below the default - the
    /// risk of a tighter poll is the reader's to take, and it is spelled out for them in
    /// the file itself.
    /// </summary>
    public TimeSpan RefreshInterval { get; private set; } = TimeSpan.FromSeconds(DefaultRefreshSeconds);

    /// <summary>
    /// Reads the cadence out of the state we have already loaded, correcting the file when
    /// what it holds is not what the app will act on.
    ///
    /// Anything outside 5s..1h is clamped and the clamped figure written back, so the file
    /// always agrees with the running app. Leaving it alone was worse than it sounds: a
    /// file asking for 1 second would be honoured as 5 and go on saying 1 for ever, so the
    /// setting looked accepted and stored while being quietly overruled at each launch.
    ///
    /// A value inside the range is never rewritten, however far below the default it sits:
    /// correcting one that the app is honouring would read as the setting being refused.
    /// The "//" note explaining the risk is restored by the writer itself, so it comes
    /// back on the next write whether or not this pass changes the number.
    /// </summary>
    private void ResolveRefreshInterval(AppState state)
    {
        var seconds = state.RefreshSeconds is > 0
            ? Math.Clamp(state.RefreshSeconds.Value, 5, 3600)
            // Absent or nonsensical: fall back to the default, which is then written out
            // below so the key sits in the file ready to be edited. A setting nobody can
            // find is no setting at all, and this one is deliberately not in the UI.
            : DefaultRefreshSeconds;

        RefreshInterval = TimeSpan.FromSeconds(seconds);

        if (state.RefreshSeconds == seconds) return;

        try
        {
            AppState.Update(a => a.RefreshSeconds = seconds);
        }
        catch (Exception ex)
        {
            // A read-only or malformed file must never stop the app from polling; the
            // clamped value still governs this run, it just is not recorded.
            AppLog.Warn($"Settings: could not persist refresh interval - {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads which bar the tray ring should follow and what colour it draws in, correcting
    /// the file the same way <see cref="ResolveRefreshInterval"/> does: an invalid value is
    /// replaced with its default both in memory and on disk, rather than silently ignored
    /// forever. Absent is not invalid - it is the default, first-run shape, and is written
    /// out so the key sits in the file ready to be edited.
    /// </summary>
    private void ResolveIconConfig(AppState state)
    {
        var icon = state.Icon;
        var source = icon?.Source ?? "max";
        var colour = icon?.Colour ?? "accent";

        var validSource = IsValidSource(source) ? source : "max";

        // Upper-cased for the same reason accents are - see ResolveAccent.
        var validColour = string.Equals(colour, "accent", StringComparison.OrdinalIgnoreCase) ? colour
            : IsHexColour(colour) ? colour.ToUpperInvariant()
            : "accent";

        _iconSource = validSource;
        _iconColour = validColour;

        if (icon is { } i && i.Source == validSource && i.Colour == validColour) return;

        try
        {
            AppState.Update(a => a.Icon = new AppState.IconState { Source = validSource, Colour = validColour });
        }
        catch (Exception ex)
        {
            // A read-only or malformed file must never stop the app from running; the
            // resolved values still govern this run, they just are not recorded.
            AppLog.Warn($"Settings: could not persist icon config - {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the per-panel accent overrides, correcting the file the same way
    /// <see cref="ResolveIconConfig"/> does, then applies each resolved color to every bar
    /// already constructed in that panel - the three per-panel templates and headers all
    /// read Accent/AccentColor rather than a literal, so one assignment here repaints
    /// everything the color touches.
    /// </summary>
    private void ResolveColors(AppState state)
    {
        var c = state.Colors;
        var corrected = false;

        foreach (var panel in ColorPanels)
        {
            var (stored, resolved) = ResolveAccent(StoredAccentFor(c, panel), DefaultAccentFor(panel));
            ApplyAccent(panel, resolved);
            if (c is null || !string.Equals(StoredAccentFor(c, panel), stored, StringComparison.Ordinal))
                corrected = true;
        }

        if (!corrected) return;

        try
        {
            AppState.Update(a =>
            {
                var colors = a.Colors ?? new AppState.ColorsState();
                foreach (var panel in ColorPanels)
                    StoreAccent(colors, panel,
                        ResolveAccent(StoredAccentFor(colors, panel), DefaultAccentFor(panel)).stored);
                a.Colors = colors;
            });
        }
        catch (Exception ex)
        {
            // A read-only or malformed file must never stop the app from running; the
            // resolved colors still govern this run, they just are not recorded.
            AppLog.Warn($"Settings: could not persist resolved colors - {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The three panels a color override applies to, and what each one's bars and header
    /// need touched when it changes - the one place that mapping is written, so the picker
    /// menu (MainWindow.axaml.cs) and ResolveColors cannot drift apart on which bars
    /// belong to which panel.
    /// </summary>
    public enum ColorPanel { Claude, Copilot, Pacing }

    /// <summary>
    /// Every panel a color applies to, so the load path and the picker both iterate the
    /// mapping rather than each listing the panels again. Adding a fourth panel is then a
    /// new enum member plus the four switches below, none of which compile until they cover
    /// it - rather than a pair of hardcoded three-line blocks that silently stay at three.
    /// </summary>
    private static readonly ColorPanel[] ColorPanels =
        { ColorPanel.Claude, ColorPanel.Copilot, ColorPanel.Pacing };

    /// <summary>The built-in accent for <paramref name="panel"/>, used when no override is set.</summary>
    private static string DefaultAccentFor(ColorPanel panel) => panel switch
    {
        ColorPanel.Claude => DefaultClaudeAccent,
        ColorPanel.Copilot => DefaultCopilotAccent,
        _ => DefaultPacingAccent,
    };

    /// <summary>The bars one panel's accent paints.</summary>
    private IEnumerable<BarViewModel> BarsFor(ColorPanel panel) => panel switch
    {
        ColorPanel.Claude => ClaudeBars,
        ColorPanel.Copilot => CopilotBars,
        _ => PacingBars,
    };

    private static string? StoredAccentFor(AppState.ColorsState? c, ColorPanel panel) => panel switch
    {
        ColorPanel.Claude => c?.Claude,
        ColorPanel.Copilot => c?.Copilot,
        _ => c?.Pacing,
    };

    private static void StoreAccent(AppState.ColorsState c, ColorPanel panel, string value)
    {
        switch (panel)
        {
            case ColorPanel.Claude: c.Claude = value; break;
            case ColorPanel.Copilot: c.Copilot = value; break;
            default: c.Pacing = value; break;
        }
    }

    /// <summary>
    /// Sets one panel's accent and repaints its bars. The per-panel templates and headers
    /// all read Accent/AccentColor rather than a literal, so this is everything the color
    /// touches.
    /// </summary>
    private void ApplyAccent(ColorPanel panel, string resolved)
    {
        switch (panel)
        {
            case ColorPanel.Claude: ClaudeAccentColor = resolved; break;
            case ColorPanel.Copilot: CopilotAccentColor = resolved; break;
            default: PacingAccentColor = resolved; break;
        }

        foreach (var bar in BarsFor(panel)) bar.Accent = resolved;
    }

    /// <summary>
    /// Returns both the text that belongs in the file - "default" preserved verbatim so a
    /// user who asked for the built-in color keeps reading that word rather than a hex code
    /// that happens to match it - and the resolved color the UI actually uses. A value that
    /// is neither "default" nor a valid #RRGGBB is corrected to "default" in the file as
    /// well as in memory.
    ///
    /// Shared by the load path and the picker so a value written can always be read back.
    ///
    /// A hex value is upper-cased on the way through. #abcdef and #ABCDEF are one colour,
    /// but the caches downstream key on the string: ColourBrushConverter's is Ordinal, so
    /// the two spellings would each mint their own SolidColorBrush, and UsageBar drops its
    /// shaped-caption cache on ReferenceEquals against the brush it was given - so bars
    /// differing only in the case of a hand-typed hex would re-shape their text on every
    /// paint. Normalising once here means one colour has one spelling everywhere after.
    /// </summary>
    private static (string stored, string resolved) ResolveAccent(string? stored, string builtIn)
    {
        if (stored is null) return ("default", builtIn);
        if (string.Equals(stored, "default", StringComparison.OrdinalIgnoreCase)) return (stored, builtIn);
        if (IsHexColour(stored))
        {
            var normalised = stored.ToUpperInvariant();
            return (normalised, normalised);
        }
        return ("default", builtIn);
    }

    /// <summary>The color currently shown for <paramref name="panel"/> - what the picker opens preset to.</summary>
    public string AccentColorFor(ColorPanel panel) => panel switch
    {
        ColorPanel.Claude => ClaudeAccentColor,
        ColorPanel.Copilot => CopilotAccentColor,
        _ => PacingAccentColor,
    };

    /// <summary>
    /// Applies a color chosen from the picker to one panel and persists it: value is
    /// either "default" (clear the override) or a #RRGGBB string.
    ///
    /// Validated through the same helper ResolveColors reads with, so what this writes is
    /// by construction something the next load will accept. The picker does produce its
    /// hex from a real Color, but the writer and the reader agreeing on what is storable
    /// should not depend on the one caller that happens to exist today: an unvalidated
    /// value reaches FillColour, which the brush converter cannot parse, and every bar in
    /// the panel then renders in the over-budget red - indistinguishable from a real
    /// maxed-out limit.
    /// </summary>
    public void SetAccentColor(ColorPanel panel, string value)
    {
        var (stored, resolved) = ResolveAccent(value, DefaultAccentFor(panel));
        ApplyAccent(panel, resolved);

        try
        {
            AppState.Update(a =>
            {
                var c = a.Colors ?? new AppState.ColorsState();
                StoreAccent(c, panel, stored);
                a.Colors = c;
            });
        }
        catch (Exception ex)
        {
            // A read-only or malformed file must never stop the app from running; the
            // chosen color still governs this run, it just is not recorded.
            AppLog.Warn($"Settings: could not persist accent color - {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Every icon source id, in menu order, mapped to the name shown for it in both the
    /// tray's "Show on icon" menu and the icon's tooltip - one place for the two to agree,
    /// rather than the tooltip printing a bar's own short label ("SESSION") while the menu
    /// spells it out ("Claude : Session").
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> IconSourceNames = new[]
    {
        new KeyValuePair<string, string>("max", "Auto (highest)"),
        new KeyValuePair<string, string>("claude.session", "Claude : Session"),
        new KeyValuePair<string, string>("claude.week", "Claude : Week"),
        new KeyValuePair<string, string>("copilot.completions", "GitHub : Completions"),
        new KeyValuePair<string, string>("copilot.chat", "GitHub : Chat"),
        new KeyValuePair<string, string>("copilot.premium", "GitHub : Premium"),
        new KeyValuePair<string, string>("pacing.day", "My Pace : Day"),
        new KeyValuePair<string, string>("pacing.week", "My Pace : Week"),
        new KeyValuePair<string, string>("pacing.month", "My Pace : Month"),
    };

    private static readonly HashSet<string> ValidIconSources =
        new(IconSourceNames.Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);

    private static bool IsValidSource(string source) => ValidIconSources.Contains(source);

    /// <summary>The display name for the bar the icon is currently tracking - "Auto (highest)"
    /// resolves to whichever panel actually won, so the auto case names that panel instead.</summary>
    public string IconSourceDisplayName(BarViewModel source)
    {
        if (!string.Equals(_iconSource, "max", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (id, name) in IconSourceNames)
                if (id == _iconSource) return name;
        }

        // Auto, or a named source that fell back to auto: name the panel the winning bar
        // actually belongs to, since "Auto (highest)" alone does not say what is showing.
        if (ClaudeBars.Contains(source)) return $"Claude : {Title(source.Label)}";
        if (CopilotBars.Contains(source)) return $"GitHub : {Title(source.Label)}";
        if (PacingBars.Contains(source)) return $"My Pace : {Title(source.Label)}";
        return source.Label;

        static string Title(string label) => label.Length == 0
            ? label
            : char.ToUpperInvariant(label[0]) + label[1..].ToLowerInvariant();
    }

    private static bool IsHexColour(string s) =>
        s.Length == 7 && s[0] == '#' && s[1..].AsSpan().IndexOfAnyExcept("0123456789ABCDEFabcdef") < 0;

    /// <summary>
    /// Whether the deadline the countdown is running towards has already passed, so the
    /// figures on screen are older than the interval promises.
    ///
    /// A timer tick normally clears this within the second. It stays true when the tick
    /// never came - the machine was suspended - or when the refresh it started failed,
    /// since the deadline is pushed a full interval out either way. Read when the widget
    /// comes back from the tray, which is exactly when a stale figure would be believed.
    /// </summary>
    public bool RefreshOverdue => DateTimeOffset.UtcNow > _nextRefresh;

    /// <summary>
    /// Whether a shortened retry deadline has come due. True only while a back-off is armed
    /// - that is, only while no panel has anything to show - so once any data arrives this
    /// goes quiet and the ordinary refresh timer is the only thing polling again.
    /// </summary>
    public bool RetryDue => _retryDelay is not null && !IsRefreshing
        && DateTimeOffset.UtcNow >= _nextRefresh;

    /// <summary>The local date the week bar's "today" tick was last computed for.</summary>
    private DateTime _markersComputedOn = DateTime.Now.Date;

    /// <summary>
    /// Recomputes the accented "today" tick on any bar whose ticks are date-derived, once the
    /// local date has actually changed.
    ///
    /// The markers are otherwise only built by a poll, and a poll is exactly what does not
    /// happen across the midnight in question: once every active Claude limit is spent,
    /// <see cref="_claudeSkipUntil"/> suppresses the call until the reset, which for the
    /// seven-day window can be days away. A limit spent at 23:50 would leave the red tick
    /// marking yesterday's boundary for the rest of the skip - not a stale figure but a wrong
    /// one, since the tick names a specific day.
    ///
    /// Cheap enough for the one-second countdown tick to carry: the date comparison is all
    /// that runs on all but one tick a day.
    /// </summary>
    private void RefreshDayMarkers()
    {
        var today = DateTime.Now.Date;
        if (today == _markersComputedOn) return;
        _markersComputedOn = today;

        foreach (var bar in ClaudeBars)
        {
            if (bar.MarkerWindowEnd is not { } end) continue;

            var (markers, todayIndex) = RollingWeekTodayMarkers(end);
            bar.SetMarkers(markers, todayIndex);
        }

        // The pacing week bar carries a today marker too, and its index is derived from the
        // date inside Build rather than from a reset instant - so it cannot be recomputed
        // here directly and is rebuilt from the last known status instead, exactly as the
        // "Workdays in a week" menu does. Without this its marker, and the pace colour now
        // derived from it, would name yesterday until the next Copilot poll.
        if (_lastCopilotStatus is { } status) RefreshPacing(status);
    }

    /// <summary>
    /// Re-derives the "resets in 4h 55m" captions from the clock, so they count down while
    /// the widget sits idle rather than freezing at whatever the last poll rendered.
    ///
    /// Needed because a poll is exactly what does not happen here: once every active limit is
    /// spent, <see cref="_claudeSkipUntil"/> suppresses the Claude poll until the reset, which
    /// on the seven-day window can be days away - so the countdown would stand still for the
    /// whole skip while the time it names kept approaching.
    ///
    /// The caption has minute resolution, so this rebuilds at most once a minute rather than
    /// on every tick. Leaving it to BarViewModel.Set's string comparison would suppress the
    /// repaint but not the work that produced the string - several allocations and a culture
    /// lookup per bar per second, on the UI thread, for a figure that had not changed.
    /// </summary>
    private void RefreshResetCaptions()
    {
        var minute = new DateTimeOffset(
            DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute,
            TimeSpan.Zero);
        if (minute == _resetCaptionsBuiltFor) return;
        _resetCaptionsBuiltFor = minute;

        foreach (var bar in ClaudeBars)
        {
            if (bar.ResetsAt is { } resetsAt)
                bar.DetailText = ClaudeLimit.ResetTextFor(resetsAt);
        }
    }

    /// <summary>The minute <see cref="RefreshResetCaptions"/> last rebuilt for.</summary>
    private DateTimeOffset? _resetCaptionsBuiltFor;

    /// <summary>
    /// Updates the countdown to the next refresh. Both services are refreshed by one timer,
    /// so there is a single figure rather than one per panel. Driven once a second by the
    /// view so it visibly ticks down between refreshes.
    /// </summary>
    public void TickCountdowns()
    {
        RefreshDayMarkers();
        RefreshResetCaptions();

        var d = _nextRefresh - DateTimeOffset.UtcNow;
        if (d <= TimeSpan.Zero)
        {
            Countdown = "Updating…";
            return;
        }

        // Round up, so the last second reads "1 second" rather than "0 seconds".
        var seconds = (int)Math.Ceiling(d.TotalSeconds);

        // From a minute up, count in whole minutes only. Appending the seconds would
        // rewrite the line every tick to change one digit, which on a long interval is
        // flicker rather than information; the last minute switches to seconds, where the
        // count is worth watching.
        //
        // The boundary is inclusive so that a full minute reads "1 minute" and the seconds
        // run 59, 58, ... - at "> 60" the changeover printed "60 seconds" for one tick,
        // which is the same duration said twice in two different units.
        // An hour is the top of the permitted range, and at that end "60 minutes" is both
        // the wrong unit and a figure the minute form was never meant to print. Hours and
        // minutes together keep it readable without the line growing a third component.
        if (seconds >= 3600)
        {
            var hours = seconds / 3600;
            var rest = seconds % 3600 / 60;
            Countdown = rest == 0
                ? (hours == 1 ? "Updates in 1 hour" : $"Updates in {hours} hours")
                : $"Updates in {hours}h {rest}m";
            return;
        }

        if (seconds >= 60)
        {
            // Rounded down, so the figure only ever falls: rounding up would show "2
            // minutes" at 61s and then drop to "1 minute" a tick later, which reads as the
            // clock jumping backwards.
            var minutes = seconds / 60;
            Countdown = minutes == 1 ? "Updates in 1 minute" : $"Updates in {minutes} minutes";
            return;
        }

        Countdown = seconds == 1 ? "Updates in 1 second" : $"Updates in {seconds} seconds";
    }

    /// <summary>Restores collapsed panels from the state file at start-up.</summary>
    public void LoadCollapsedState()
    {
        var state = AppState.Load();

        // Read from the state already in hand rather than loading the file a second time,
        // and from here rather than a static initializer: the fallback path puts this file
        // on %LOCALAPPDATA%, possibly a network share, and file I/O on the class-load path
        // both blocks window construction and turns any failure into a permanently
        // unusable type for the rest of the process.
        ResolveRefreshInterval(state);
        ResolveIconConfig(state);
        ResolveColors(state);

        // Absent means never set: stay at the as-designed size. Clamped the same way an
        // out-of-range value would be if it somehow reached here via a hand-edited file.
        _fontScale = Math.Clamp(state.FontScale ?? DefaultFontScale, MinFontScale, MaxFontScale);
        OnPropertyChanged(nameof(FontScale));
        OnPropertyChanged(nameof(CanIncreaseFontScale));
        OnPropertyChanged(nameof(CanDecreaseFontScale));

        // Absent means never set: stay at the as-designed 5-day (Monday-Friday) week.
        _workDaysPerWeek = Math.Clamp(state.WorkDaysPerWeek ?? BusinessDays.DefaultWorkDaysPerWeek,
            BusinessDays.MinWorkDaysPerWeek, BusinessDays.MaxWorkDaysPerWeek);
        OnPropertyChanged(nameof(WorkDaysPerWeek));
        for (int n = BusinessDays.MinWorkDaysPerWeek; n <= BusinessDays.MaxWorkDaysPerWeek; n++)
            OnPropertyChanged(WorkDaysCheckedProperty(n));

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

        // Absent means never set: auto-update stays on, matching the always-on behaviour
        // before this was a UI toggle.
        _autoUpdate = state.AutoUpdate ?? true;
        OnPropertyChanged(nameof(AutoUpdate));

        // Absent means never set: stay on real releases only, the same as before these were
        // UI toggles instead of --update-include-alpha/--update-include-beta command-line flags.
        _updateIncludeAlpha = state.UpdateIncludeAlpha ?? false;
        _updateIncludeBeta = state.UpdateIncludeBeta ?? false;
        OnPropertyChanged(nameof(UpdateIncludeAlpha));
        OnPropertyChanged(nameof(UpdateIncludeBeta));

        if (state.Hidden is { } h)
        {
            _claudeHidden = h.Claude;
            _copilotHidden = h.Copilot;
            _pacingHidden = h.Pacing;

            // Guard against a file that hides everything, which would leave no way back.
            // Only the last panel is forced open: discarding all three flags would throw
            // away two perfectly legal choices to correct the one that is not.
            if (_claudeHidden && _copilotHidden && _pacingHidden) _claudeHidden = false;

            OnPropertyChanged(nameof(ClaudeHidden));
            OnPropertyChanged(nameof(CopilotHidden));
            OnPropertyChanged(nameof(PacingHidden));
            OnPropertyChanged(nameof(ClaudeVisible));
            OnPropertyChanged(nameof(CopilotVisible));
            OnPropertyChanged(nameof(PacingVisible));
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

        var firstRun = state.AutostartInitialised is not true;

        // Registry, plist and desktop-file writes are all disk or hive I/O, and the two
        // AppState round-trips behind them are more of the same. This runs from the window
        // constructor, so doing it inline holds the window off the screen for as long as
        // the profile takes to answer - seconds, on a roaming or network-backed one.
        Task.Run(() =>
        {
            // Before anything reads or writes the current entry: the pre-rename one still
            // fires at login and is invisible to both, so it has to go regardless of whether
            // this is a first run.
            AutostartService.RemoveLegacyEntry();

            if (firstRun)
            {
                AutostartService.Set(true);
                AppState.Update(a => a.AutostartInitialised = true);
            }
            else
            {
                // An entry can name a path that no longer launches anything - most of all on
                // Linux, where an AppImage's autostart entry was for several releases written
                // with the transient /tmp/.mount_* path the app happened to be running from,
                // and where moving the .AppImage has the same effect. The user turned
                // autostart on and it silently stopped working, with the menu agreeing it was
                // off because the entry no longer points here. Rewriting it costs one file
                // write on the launches where it is actually broken, and nothing at all
                // otherwise.
                AutostartService.RepairIfStale();
            }

            var enabled = AutostartService.IsEnabled();

            // Back to the UI thread: this sets a bound property, and the menu may already
            // be on screen by the time the probe finishes.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // The menu is live while this runs, so the user may have toggled autostart
                // already. Their choice is newer than this probe and wins.
                if (_autostartTouched) return;

                _autostartEnabled = enabled;
                OnPropertyChanged(nameof(AutostartEnabled));
            });
        });

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
        if (ClaudeVisible && ClaudeBarsVisible) widest = Widest(ClaudeBars, widest);
        if (CopilotVisible && CopilotBarsVisible) widest = Widest(CopilotBars, widest);
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

    /// <summary>
    /// Widths already measured, keyed by label.
    ///
    /// OnBarsChanged runs several times per refresh - every Available and Hidden setter
    /// reaches it through VisibilityChanged - while the labels themselves come from a fixed
    /// handful (SESSION, WEEK, COMPLETIONS, CHAT, PREMIUM, DAY, MONTH). Without this, each
    /// pass re-shapes text that has not changed since the app started.
    ///
    /// A plain Dictionary is enough: every path into OnBarsChanged is on the UI thread -
    /// the property setters, the constructor, and the refresh, which resumes there via
    /// ConfigureAwait(true) before measuring.
    /// </summary>
    private static readonly Dictionary<string, double> _labelWidths = new(StringComparer.Ordinal);

    /// <summary>Measures a label in the same typeface and size the bars render it with.</summary>
    private static double MeasureLabel(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (_labelWidths.TryGetValue(text, out var cached)) return cached;

        var width = MeasureLabelCore(text);

        // Only cached once the font subsystem is up: the fallback below is an estimate, and
        // caching it would keep a wrong width for the life of the process.
        if (_fontsReady) _labelWidths[text] = width;
        return width;
    }

    /// <summary>
    /// False until a real measurement succeeds. The first calls happen before Avalonia has
    /// initialised, where measurement throws and the estimate stands in.
    /// </summary>
    private static bool _fontsReady;

    private static double MeasureLabelCore(string text)
    {
        try
        {
            var ft = new Avalonia.Media.FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                Avalonia.Media.FlowDirection.LeftToRight,
                new Avalonia.Media.Typeface(Avalonia.Media.FontFamily.Default),
                9,
                Avalonia.Media.Brushes.Gray);

            _fontsReady = true;
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
