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
            OnPropertyChanged(nameof(FillColour));
            OnPropertyChanged(nameof(CaptionColour));
            OnPropertyChanged(nameof(CaptionHighlightColour));
            OnPropertyChanged(nameof(ValueColour));
        }
    }

    // The three colours a row is drawn in, resolved here rather than in the template. The
    // rule is one line each and identical for all three panels, which is what lets the
    // panels share a single template instead of repeating the same converter twelve times.

    /// <summary>Red past the allowance, the panel's own accent inside it.</summary>
    public string FillColour => _isOverBudget ? OverColour : Accent;

    /// <summary>Red only where the caption is the figure that went over - see <see cref="WarnCaption"/>.</summary>
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
    public string ValueColour => _isOverBudget ? OverColour : TextColour;

    /// <summary>Red, chosen to stay legible on the dark background the widget uses.</summary>
    private const string OverColour = "#F85149";
    private const string MutedColour = "#6B7079";
    private const string HighlightColour = "#B9BDC6";
    private const string TextColour = "#E8EAED";

    public double Fraction
    {
        get => _fraction;
        set { Set(ref _fraction, value); OnPropertyChanged(nameof(Percent)); }
    }

    public double Percent => Fraction * 100;

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

    /// <summary>Which bar the tray ring follows and what colour it draws in - see ResolveIconState.</summary>
    private string _iconSource = "max";
    private string _iconColour = "accent";
    private double _labelWidth = 86;
    private string _signInText = "";

    // One default accent per panel, matching the section headings in the XAML. They live
    // here because the bars carry them: the three bar templates were identical but for
    // these. A user override (see ResolveColors) replaces the value every bar and header
    // actually reads, but this is still what "default" in the state file resolves to.
    private const string DefaultClaudeAccent = "#D97757";
    private const string DefaultCopilotAccent = "#58A6FF";
    private const string DefaultPacingAccent = "#3FB950";

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
            catch (Exception)
            {
                // A read-only or malformed file must never stop the app from running; the
                // chosen source still governs this run, it just is not recorded.
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
    /// the bar's own FillColour, which already flips to the over-budget red past 100%; an
    /// explicit #RRGGBB overrides the accent but not that flip - past 100% the ring is red
    /// whatever this setting says.
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
        "claude.session" => UsableBar(ClaudeVisible ? ClaudeBarByLabel("SESSION") : null),
        "claude.week" => UsableBar(ClaudeVisible ? ClaudeBarByLabel("WEEK") : null),
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

        if (ClaudeVisible) best = Highest(best, ClaudeBars);
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

    /// <summary>Set by the refresh: does this service have anything to show?</summary>
    public bool ClaudeAvailable
    {
        get => _claudeVisible;
        set
        {
            if (Set(ref _claudeVisible, value)) VisibilityChanged(nameof(ClaudeVisible));
            StopLoading();
        }
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
    public bool CopilotNeedsSignIn { get => _copilotNeedsSignIn; set => Set(ref _copilotNeedsSignIn, value); }
    public string SignInText { get => _signInText; set => Set(ref _signInText, value); }
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
    public bool ClaudeVisible => !_isLoading && _claudeVisible && !_claudeHidden;
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
            // hands off to whichever panel just got its first data.
            if (Set(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(ClaudeVisible));
                OnPropertyChanged(nameof(CopilotVisible));
                OnPropertyChanged(nameof(PacingVisible));
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
    /// failed or came back empty. Set explicitly from the three services' own results (see
    /// <see cref="_claudeFound"/> and its siblings) rather than read off a panel's
    /// IsVisible, which defaults true before any poll ever runs so the panel can show its
    /// placeholder dashes - a check built on that would never see "nothing" even when a
    /// poll genuinely failed everywhere. Drives only the fast-retry back-off in
    /// <see cref="ScheduleRetryIfNothingFound"/>; IsLoading does not depend on this at all -
    /// see its own doc comment.
    /// </summary>
    private bool NothingToShow => !_claudeFound && !_copilotFound && !_pacingFound;

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

            ScheduleRetryIfNothingFound();
        }
    }

    /// <summary>
    /// Arms a fast retry when the poll that just finished found nothing anywhere, or stands
    /// the back-off down once something has.
    ///
    /// The app starts with the session, so the first poll usually runs before the network
    /// is up and both services fail - not worth a full refresh interval of silence, since a
    /// few seconds later it would almost certainly work. RetryDue asks the window to come
    /// back sooner; the interval doubles each time so a machine that is genuinely offline
    /// settles onto the normal cadence instead of polling forever.
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
                    bar.Label = l.Label;
                    bar.Fraction = l.Fraction;
                    bar.ValueText = $"{l.Percent:0}%";
                    bar.DetailText = l.ResetText;
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

            ClaudeAvailable = limits.IsAvailable;
            _claudeFound = limits.IsAvailable;
            ClaudeSubtitle = limits.IsAvailable ? (_claudePlan ?? "") : (limits.Error ?? "");
        }
        else
        {
            // Gate skipped this poll: whatever the previous poll found still stands, so
            // this is not "nothing" for the retry back-off's purposes.
            _claudeFound = ClaudeAvailable;
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
        var pacing = _pacing.Build(status, DateTime.Now);
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
    public const string ProjectUrl = "https://github.com/HovKlan-DH/TokenBurnRate";

    /// <summary>Opens the project page in the default browser.</summary>
    public void OpenProjectPage() => TryOpenBrowser(ProjectUrl);

    /// <summary>
    /// Opens the folder AppState.Path writes the state file into, in the OS file browser -
    /// the same folder CrashLog drops its logs beside. That is the executable's own folder
    /// for a portable copy, but for a Velopack install (where the exe's folder is a
    /// versioned, unwritable app-x.y.z directory) it is %APPDATA%/~/.config instead - so
    /// this must follow AppState's resolution rather than assuming beside-the-exe.
    /// </summary>
    public void OpenApplicationFolder()
    {
        var dir = System.IO.Path.GetDirectoryName(Services.AppState.Path);
        if (string.IsNullOrWhiteSpace(dir)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // No file browser on this desktop, or the folder is gone: nothing to open,
            // nothing worth surfacing to a monitor.
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
            // Not status.NeedsSignIn: the panel stays visible so the sign-in button has
            // somewhere to live, but there is still no actual quota data behind it, so this
            // poll found nothing for the retry back-off's purposes either way.
            _copilotFound = false;
            _pacingFound = false;
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
        catch (Exception)
        {
            // A read-only or malformed file must never stop the app from polling; the
            // clamped value still governs this run, it just is not recorded.
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
        catch (Exception)
        {
            // A read-only or malformed file must never stop the app from running; the
            // resolved values still govern this run, they just are not recorded.
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
        catch (Exception)
        {
            // A read-only or malformed file must never stop the app from running; the
            // resolved colors still govern this run, they just are not recorded.
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
        catch (Exception)
        {
            // A read-only or malformed file must never stop the app from running; the
            // chosen color still governs this run, it just is not recorded.
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
        // on %APPDATA%, possibly a network share, and file I/O on the class-load path both
        // blocks window construction and turns any failure into a permanently unusable
        // type for the rest of the process.
        ResolveRefreshInterval(state);
        ResolveIconConfig(state);
        ResolveColors(state);

        // Absent means never set: stay at the as-designed size. Clamped the same way an
        // out-of-range value would be if it somehow reached here via a hand-edited file.
        _fontScale = Math.Clamp(state.FontScale ?? DefaultFontScale, MinFontScale, MaxFontScale);
        OnPropertyChanged(nameof(FontScale));
        OnPropertyChanged(nameof(CanIncreaseFontScale));
        OnPropertyChanged(nameof(CanDecreaseFontScale));

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
            if (firstRun)
            {
                AutostartService.Set(true);
                AppState.Update(a => a.AutostartInitialised = true);
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
