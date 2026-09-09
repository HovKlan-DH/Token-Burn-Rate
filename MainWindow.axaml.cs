using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TokenBurnRate.ViewModels;

namespace TokenBurnRate;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _countdownTimer;
    private readonly CancellationTokenSource _cts = new();
    private TrayIcon? _tray;

    /// <summary>
    /// Set when the user has chosen to exit for real, so OnClosing stops intercepting and
    /// lets the window close. Without it, "Exit" from the tray would only hide the window
    /// again and the process would never end.
    /// </summary>
    private bool _exiting;

    /// <summary>
    /// Guards against Shutdown() re-entering OnClosing on the window that is already
    /// closing. See the comment at the point of use.
    /// </summary>
    private bool _shuttingDown;

    /// <summary>
    /// Whether the one-off work that needs a realised visual tree has been done. Opened is
    /// raised again by every Show(), so it cannot stand in for "the app has started".
    /// </summary>
    private bool _opened;

    /// <summary>
    /// Holds a single tray click back long enough to see whether a second one follows.
    ///
    /// Avalonia's TrayIcon raises one Clicked per button release and has no separate
    /// double-click event, so a double-click arrives here as two Clicked in quick
    /// succession. Acting on the first immediately would make a double-click toggle twice
    /// and land back where it started; waiting out the system double-click time lets the
    /// second click cancel the pending toggle and show instead.
    ///
    /// Created with the tray it belongs to, so it exists whenever a click can arrive.
    /// </summary>
    private DispatcherTimer? _trayClickTimer;

    /// <summary>
    /// When the burst of clicks the tray is currently reporting is over. Clicks landing
    /// before this are trailing releases of a double-click already acted on, not the start
    /// of a new one - without that, a third click restarts the pending toggle and a fumbled
    /// triple-click hides the window the double-click just showed.
    /// </summary>
    private DateTime _trayClickBurstEnds = DateTime.MinValue;

    /// <summary>The rounded percentage, colour and source name the tray ring was last drawn
    /// with, so an unchanged refresh does not repaint the shell - see UpdateTrayIcon.</summary>
    private int _lastIconPercent = int.MinValue;
    private string? _lastIconColour;
    private string? _lastIconName;
    private bool _lastIconHadSource;

    /// <summary>
    /// The tray menu's "Show on icon" entries, keyed by the source id they set - see
    /// BuildIconSourceMenu. Checked/visible state is refreshed from UpdateTrayIcon rather
    /// than bound, because the Win32 backend never raises NativeMenu.Opening (see the
    /// comment on the Show/Hide item), so nothing else would pick up a source that just
    /// became available or unavailable.
    /// </summary>
    private readonly Dictionary<string, NativeMenuItem> _iconSourceItems = new(StringComparer.Ordinal);

    /// <summary>Repaints the header cursors when a panel becomes or stops being the only one
    /// on screen. Kept so OnClosing can unhook it - see ApplyCursors.</summary>
    private PropertyChangedEventHandler? _soloCursorHandler;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        RestorePosition();

        // Drag anywhere on the header, since the window has no title bar to grab.
        if (this.FindControl<Grid>("DragHandle") is { } handle)
            handle.PointerPressed += OnDragHandlePressed;

        if (this.FindControl<Button>("RefreshButton") is { } refresh)
            refresh.Click += (_, _) => RunRefresh("refresh button");

        if (this.FindControl<Button>("CloseButton") is { } close)
            close.Click += (_, _) => Close();

        if (this.FindControl<Button>("PinButton") is { } pin)
            pin.Click += (_, _) => _vm.Pinned = !_vm.Pinned;

        if (this.FindControl<MenuItem>("OpenFolderItem") is { } openFolder)
            openFolder.Click += (_, _) => _vm.OpenApplicationFolder();

        if (this.FindControl<MenuItem>("ProjectPageItem") is { } projectPage)
            projectPage.Click += (_, _) => _vm.OpenProjectPage();

        if (this.FindControl<MenuItem>("ExitItem") is { } exitItem)
            exitItem.Click += (_, _) => ExitApplication();

        HookColorPicker("ClaudeColorItem", "Claude", ViewModels.MainViewModel.ColorPanel.Claude);
        HookColorPicker("CopilotColorItem", "GitHub Copilot", ViewModels.MainViewModel.ColorPanel.Copilot);
        HookColorPicker("PacingColorItem", "GitHub Copilot : My Pace", ViewModels.MainViewModel.ColorPanel.Pacing);

        if (this.FindControl<Button>("SignInButton") is { } signIn)
            signIn.Click += (_, _) => RunSafely(() => _vm.SignInToGitHubAsync(_cts.Token), "sign-in button");

        // Clicking a section header collapses or expands that panel.
        HookHeader("ClaudeHeader", () => _vm.ClaudeSolo, () => _vm.ClaudeCollapsed = !_vm.ClaudeCollapsed);
        HookHeader("CopilotHeader", () => _vm.CopilotSolo, () => _vm.CopilotCollapsed = !_vm.CopilotCollapsed);
        HookHeader("PacingHeader", () => _vm.PacingSolo, () => _vm.PacingCollapsed = !_vm.PacingCollapsed);

        // Tray first: LoadCollapsedState raises CloseToTray, whose getter is gated on
        // TraySupported. Loading before the tray exists would publish that preference as
        // false whatever the file said, and the menu's two-way binding would latch it.
        SetUpTray();
        _vm.LoadCollapsedState();

        // Copilot's quota is a remote call and Claude's parse is incremental, so a 60s
        // cadence keeps the display live without hammering either source. Read after
        // LoadCollapsedState, which is what resolves it from the state file.
        _timer = new DispatcherTimer { Interval = _vm.RefreshInterval };
        _timer.Tick += (_, _) => RunRefresh("refresh timer", force: false);
        _timer.Start();

        // Separate one-second tick so the countdowns visibly run down between refreshes.
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) =>
        {
            _vm.TickCountdowns();

            // A poll that found nothing shortens its own deadline to retry sooner than the
            // cadence - see SettleLoadingState. The refresh timer runs on a fixed interval
            // and cannot honour that, so the second-tick drives it: this is already the
            // clock watching that deadline, and a retry is only ever due while the widget
            // has nothing to show at all.
            if (_vm.RetryDue) RunRefresh("startup retry", force: false);
        };
        _countdownTimer.Start();

        // Opened fires on every Show(), not just the first one, so this guards itself.
        // Without that, coming back from the tray re-ran the "initial" refresh and pushed
        // the next poll a full interval away - the timer never stopped, but the deadline
        // it was counting towards kept being moved, so the countdown restarted on every
        // show.
        Opened += (_, _) =>
        {
            if (_opened) return;
            _opened = true;

            ApplyCursors();     // the visual tree is only complete once the window is open
            _vm.TickCountdowns();
            RunRefresh("initial refresh");
            Services.CheckInService.PingHome();
            Services.UpdateService.CheckOnLaunch();
        };
    }

    /// <summary>
    /// Runs a refresh and repaints the tray ring once it completes. The single hook every
    /// refresh path goes through, so the icon can never fall out of step with the bars it
    /// mirrors.
    ///
    /// <paramref name="force"/> defaults to true: every caller except the background timer
    /// is the user directly asking for current data, which should always reach the API even
    /// if Claude's skip-until-reset gate would otherwise sit this poll out.
    /// </summary>
    private void RunRefresh(string context, bool force = true) => RunSafely(async () =>
    {
        await _vm.RefreshAsync(_cts.Token, force);
        UpdateTrayIcon();
    }, context);

    /// <summary>
    /// Sets cursors in code rather than XAML: a control that paints its own background -
    /// the inner StackPanels, and Button, which supplies its own - covers the outer
    /// Border's cursor, so the move cursor never reaches the body.
    /// </summary>
    private void ApplyCursors()
    {
        var move = new Cursor(StandardCursorType.SizeAll);
        var hand = new Cursor(StandardCursorType.Hand);

        // Only the top row actually starts a drag, so only the top row shows the move
        // cursor. Advertising it over the whole body promised something the window does
        // not do - the pointer said "drag me" and nothing happened.
        if (Content is Control body) body.Cursor = Cursor.Default;
        if (this.FindControl<Control>("DragHandle") is { } dragHandle) dragHandle.Cursor = move;

        // The clickable things take the hand. The buttons sit inside the drag handle, so
        // they must be set after it to win over the move cursor they would inherit.
        foreach (var name in new[]
                 {
                     "RefreshButton", "CloseButton", "PinButton", "SignInButton",
                 })
        {
            if (this.FindControl<Control>(name) is { } c) c.Cursor = hand;
        }

        // Section headers are the exception: the last panel standing cannot collapse, so it
        // shows the default pointer rather than a hand promising a toggle. Re-applied when a
        // panel is shown or hidden, since that is what promotes or demotes a header to solo.
        void ApplyHeaderCursors()
        {
            SetCursor("ClaudeHeader", _vm.ClaudeSolo);
            SetCursor("CopilotHeader", _vm.CopilotSolo);
            SetCursor("PacingHeader", _vm.PacingSolo);

            void SetCursor(string name, bool solo)
            {
                if (this.FindControl<Control>(name) is { } c) c.Cursor = solo ? Cursor.Default : hand;
            }
        }

        ApplyHeaderCursors();

        // Held in a field so OnClosing can unhook it. The view model outlives the close -
        // the dispatcher can still deliver a queued Solo change after the window is gone,
        // and the handler would then run FindControl against a closed window.
        _soloCursorHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(_vm.ClaudeSolo) or nameof(_vm.CopilotSolo)
                or nameof(_vm.PacingSolo))
                ApplyHeaderCursors();
        };
        _vm.PropertyChanged += _soloCursorHandler;
    }

    /// <summary>
    /// Wires a section header so a left click toggles its panel. The last panel standing
    /// cannot collapse, so its header simply does nothing; dragging stays on the top row.
    /// </summary>
    private void HookHeader(string name, Func<bool> isSolo, Action toggle)
    {
        if (this.FindControl<Control>(name) is not { } header) return;

        header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (isSolo()) { e.Handled = true; return; }
            toggle();
            e.Handled = true;       // never start a window drag from a header click
        };
    }

    private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>
    /// Wires one "Panel colors" submenu entry to open the picker for that panel, preset to
    /// its current color, and apply whatever comes back - a hex string, or "default" to
    /// clear the override. Nothing runs if the user cancels: PickAsync returns null and the
    /// panel is left exactly as it was.
    /// </summary>
    private void HookColorPicker(string itemName, string panelLabel, ViewModels.MainViewModel.ColorPanel panel)
    {
        if (this.FindControl<MenuItem>(itemName) is not { } item) return;

        // Through RunSafely like every other async handler here: ShowDialog can throw - an
        // owner already closing, or the picker's theme resources failing to resolve - and an
        // async void handler would discard that, leaving a menu item that does nothing with
        // nothing logged to say why.
        item.Click += (_, _) => RunSafely(async () =>
        {
            var current = _vm.AccentColorFor(panel);
            var chosen = await Views.ColorPickerWindow.PickAsync(this, panelLabel, current);
            if (chosen is null) return;     // cancelled

            _vm.SetAccentColor(panel, chosen);

            // The tray ring reads a bar's Accent through FillColour, same as the widget's
            // own header and bars - but nothing else re-renders the tray on demand, so a
            // change made here would sit unseen on the icon until the next timed refresh.
            UpdateTrayIcon();
        }, "panel colour picker");
    }

    // ---- tray ---------------------------------------------------------------------------

    /// <summary>
    /// Creates the notification-area icon. Failure is not fatal and not an error worth
    /// showing: some Linux desktops ship no system tray at all. TraySupported records the
    /// outcome, and the close-to-tray option hides itself when there is nowhere to minimise
    /// to, so the close button can never become a dead end.
    /// </summary>
    private void SetUpTray()
    {
        try
        {
            // "Show" was wrong once the icon click became a toggle: with the window
            // already up, the menu offered a Show that did nothing while the icon beside
            // it hid - two controls for one concept, disagreeing. This runs the same
            // toggle as the icon, so they cannot diverge.
            //
            // The label says both states rather than tracking the current one. Retitling
            // it as the menu opens is what NativeMenu.Opening is for, but only the macOS
            // backend raises it: Win32 builds the tray menu in TrayIconImpl.OnRightClicked
            // as a managed flyout and never goes through that event, so a handler there
            // would leave the item stuck at whichever word it was born with - worse than
            // not tracking state at all, because it would be confidently wrong.
            var toggle = new NativeMenuItem("Show / Hide");
            toggle.Click += (_, _) => ToggleFromTray();

            // Exiting has to live here: with close minimising instead of exiting, the tray
            // is the only place left that can actually end the process.
            var exit = new NativeMenuItem("Exit");
            exit.Click += (_, _) => ExitApplication();

            var iconSourceMenu = BuildIconSourceMenu();

            _tray = new TrayIcon
            {
                Icon = Icon,
                ToolTipText = "TokenBurnRate",
                IsVisible = true,
                Menu = new NativeMenu { toggle, iconSourceMenu, exit },
            };

            // Left-clicking the icon toggles the widget and double-clicking always shows
            // it; the menu is for everything else. A method group rather than a lambda, so
            // OnClosing can unsubscribe the same delegate it added.
            _tray.Clicked += OnTrayClicked;

            // Built with the tray, not on first click: it is only meaningful when there is
            // an icon to click, and creating it here means every use site can count on it.
            _trayClickTimer = new DispatcherTimer { Interval = Services.DoubleClick.Interval };
            _trayClickTimer.Tick += OnTrayClickSettled;

            _vm.TraySupported = true;
        }
        catch (Exception)
        {
            // No tray on this desktop: close keeps its original meaning of exiting.
            _tray = null;
            _vm.TraySupported = false;
        }
    }

    /// <summary>
    /// Builds the "Show on icon" submenu: one checkable entry per named source plus the
    /// "Highest" default, so the ring can be pointed at a specific bar without editing the
    /// state file. Built once with every entry always present in the NativeMenu itself -
    /// only IsVisible is toggled at refresh time, since NativeMenuItem instances cannot be
    /// inserted or removed from a live Win32 tray menu without rebuilding the whole thing.
    /// </summary>
    private NativeMenuItem BuildIconSourceMenu()
    {
        var submenu = new NativeMenu();

        foreach (var (source, name) in ViewModels.MainViewModel.IconSourceNames)
            AddItem(source, name);

        var root = new NativeMenuItem("Show on icon") { Menu = submenu };
        return root;

        void AddItem(string source, string header)
        {
            // Avalonia 12 folded NativeMenuItemToggleType into MenuItemToggleType, the same
            // enum the in-window MenuItem uses - same members, one name for both now.
            var item = new NativeMenuItem(header) { ToggleType = MenuItemToggleType.Radio };
            item.Click += (_, _) =>
            {
                _vm.IconSource = source;
                RefreshIconSourceMenu();
                UpdateTrayIcon();
            };
            submenu.Add(item);
            _iconSourceItems[source] = item;
        }
    }

    /// <summary>
    /// Syncs the tray's "Show on icon" entries with the view model: ticks the active source
    /// and hides whichever named bars are not currently available. Called from
    /// UpdateTrayIcon, which already runs after every refresh - the one point where bar
    /// availability can have changed.
    /// </summary>
    private void RefreshIconSourceMenu(IReadOnlyDictionary<string, bool>? available = null)
    {
        available ??= _vm.ResolveIconState().Available;

        foreach (var (source, item) in _iconSourceItems)
        {
            item.IsChecked = _vm.IconSource == source;
            item.IsVisible = source == "max"
                || (available.TryGetValue(source, out var ok) && ok);
        }
    }

    /// <summary>
    /// Repaints the tray icon as a "quota ring" for whichever bar ResolveIconState picks.
    /// See Assets/live-ring-icon.md. Called after every refresh path, so the ring can never
    /// sit stale while the panel it mirrors has moved on.
    /// </summary>
    private void UpdateTrayIcon()
    {
        if (_tray is null) return;     // no tray on this desktop

        try
        {
            // Availability can change on any refresh, and the winning bar falls out of the
            // same pass - resolved once here rather than re-derived per menu entry.
            var (available, source) = _vm.ResolveIconState();
            RefreshIconSourceMenu(available);

            if (source is null)
            {
                // Nothing on screen has a usable fraction: keep the static app identity
                // rather than drawing an empty ring.
                if (!_lastIconHadSource) return;
                _lastIconHadSource = false;
                _tray.Icon = Icon;
                _tray.ToolTipText = "TokenBurnRate";
                return;
            }

            var colour = _vm.ResolveIconColour(source);
            var percent = (int)Math.Round(source.Percent, MidpointRounding.AwayFromZero);

            // Which bar is showing is part of what the tray displays, not just how it is
            // drawn: under "Auto (highest)" the winning bar can change to a different panel
            // sitting at the same rounded percentage and the same colour - two bars over
            // budget both resolve to the same red - and a guard on appearance alone would
            // then leave the tooltip naming the panel that used to be highest. The tooltip
            // is the only text saying which bar this is, so a stale one is confidently wrong.
            var name = _vm.IconSourceDisplayName(source);

            if (_lastIconHadSource && percent == _lastIconPercent
                && string.Equals(colour, _lastIconColour, StringComparison.OrdinalIgnoreCase)
                && string.Equals(name, _lastIconName, StringComparison.Ordinal))
                return;     // nothing visible changed - a 60s poll must not repaint the shell

            // WindowIcon is not IDisposable in Avalonia - the bitmap it wraps is released by
            // the GC once nothing references it, which is as soon as this assignment runs.
            _tray.Icon = Services.TrayIconRenderer.Render(source.Fraction, colour);
            _tray.ToolTipText = $"{name}\nUsed {percent}%";

            _lastIconHadSource = true;
            _lastIconPercent = percent;
            _lastIconColour = colour;
            _lastIconName = name;
        }
        catch (Exception ex)
        {
            // A failed icon repaint must never take down a monitor.
            Services.CrashLog.Record(ex, "tray icon update");
        }
    }

    /// <summary>
    /// Tells the user the app is still running, the first time the window disappears into
    /// the tray. Only the first time: after that the behaviour is known, and a notice on
    /// every close would be nagging.
    ///
    /// The flag is only set once the balloon actually appeared, so a failed notification
    /// is retried on the next minimise rather than being silently spent. The retry costs
    /// nothing: TrayNotifier reuses the window and icon it created the first time.
    /// </summary>
    private void NotifyMinimisedToTrayOnce()
    {
        if (Services.AppState.Load().TrayNoticeShown == true) return;

        var shown = Services.TrayNotifier.Show(
            "TokenBurnRate is still running.",
            "The application is visible in the tray area. Click its icon to bring it back, "
            + "or right-click for Exit.");

        if (shown) Services.AppState.Update(a => a.TrayNoticeShown = true);
    }

    /// <summary>
    /// Puts the widget away into the tray.
    ///
    /// <paramref name="explain"/> is set only by the close button, which is the one route
    /// where the window vanishing is a surprise worth a one-off balloon. A tray click is
    /// not: the notice would be telling the user how to do the thing they just did, from
    /// the icon they just clicked, and it would spend the one-shot flag that the close
    /// button's case actually needs.
    ///
    /// Position is saved on the way in so the widget reopens where it was left even if the
    /// process is killed while hidden. Only when it can have moved: hiding a window that
    /// is already hidden, or that has not been shown since the last save, would repeat a
    /// synchronous read-modify-write of the state file on the UI thread for nothing.
    /// </summary>
    private void HideToTray(bool explain)
    {
        if (IsVisible) SavePosition();
        Hide();

        // The refresh timer keeps running. This is a monitor: the poll cadence belongs to
        // the app, not to whether anyone is looking, so the figures go on being collected
        // while the widget is in the tray and showing it reveals a history that never had
        // a gap. Stopping it made every show restart the interval, so a widget toggled
        // often polled far more than its setting asked for.
        //
        // The countdown is the opposite case: it only rewrites a label nobody can see, and
        // RestoreFromTray recomputes it from the deadline, so there is nothing to preserve
        // by leaving it running once a second.
        _countdownTimer.Stop();

        if (explain) NotifyMinimisedToTrayOnce();
    }

    /// <summary>
    /// Brings the widget back from the tray and puts it in front.
    ///
    /// The refresh timer was never stopped, so in the ordinary case the figures on screen
    /// are the ones the running poll has already reached and the countdown resumes towards
    /// the existing deadline rather than starting over. An overdue deadline is the
    /// exception: a refresh that failed still pushes the deadline a full interval out, and
    /// a machine suspended with the widget hidden misses ticks entirely - either way the
    /// user is opening this to read a current figure, so a stale one is refreshed here.
    /// </summary>
    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();

        _vm.TickCountdowns();       // the label is as old as the hide; catch it up before it shows
        _countdownTimer.Start();

        if (_vm.RefreshOverdue) RunRefresh("restore from tray");
    }

    /// <summary>
    /// Flips the widget between shown and hidden. Shared by the single tray click and the
    /// tray menu's item, so the icon and the menu beside it can never disagree about what
    /// a click means.
    /// </summary>
    private void ToggleFromTray()
    {
        if (IsVisible) HideToTray(explain: false);
        else RestoreFromTray();
    }

    /// <summary>
    /// A click on the tray icon: one toggles the widget, two always show it.
    ///
    /// TrayIcon has no double-click event, so the two are told apart by timing - the first
    /// click starts a timer and only acts when it expires unanswered, and a second click
    /// arriving first cancels it and shows unconditionally. Deferring the toggle is what
    /// makes the double-click meaningful: acting at once would toggle twice on a
    /// double-click and end up back where it started.
    ///
    /// The wait is the system double-click time, so the window matches every other icon on
    /// the desktop rather than a figure invented here.
    /// </summary>
    private void OnTrayClicked(object? sender, EventArgs e)
    {
        // A click can still be delivered after the window has closed: the handler is live
        // until the tray icon is disposed, and the deferred toggle widens that window
        // further. Show() on a closed window throws, which on the UI thread takes the
        // process down during what is meant to be an orderly exit.
        if (_exiting || _shuttingDown || _trayClickTimer is null) return;

        var now = DateTime.UtcNow;

        if (_trayClickTimer.IsEnabled)
        {
            // Second click of a pair: the pending toggle is cancelled and the answer is
            // always "show", whichever state the first click found the window in. The
            // burst runs on from here so the trailing clicks of a fumbled triple are
            // swallowed rather than starting a fresh toggle.
            _trayClickTimer.Stop();
            _trayClickBurstEnds = now + Services.DoubleClick.Interval;
            RestoreFromTray();
            return;
        }

        // Still inside a burst that was already answered: absorb it, and let the quiet
        // period start again from this click so holding the button down never gets through.
        if (now < _trayClickBurstEnds)
        {
            _trayClickBurstEnds = now + Services.DoubleClick.Interval;
            return;
        }

        _trayClickTimer.Start();
    }

    /// <summary>No second click arrived, so the first one stands as a toggle.</summary>
    private void OnTrayClickSettled(object? sender, EventArgs e)
    {
        _trayClickTimer!.Stop();

        // The tick can be the last thing queued before an exit, in which case the window
        // it would act on is already gone. See OnTrayClicked.
        if (_exiting || _shuttingDown) return;

        // Hide() clears IsVisible, so that alone is the whole of the distinction: the
        // widget has no taskbar entry and no minimise affordance (ShowInTaskbar="False",
        // WindowDecorations="BorderOnly"), which leaves hidden-in-tray as the only way it
        // can be off screen.
        ToggleFromTray();
    }

    /// <summary>
    /// Runs a task from an event handler without leaving it unobserved.
    ///
    /// An `async void` handler discards its Task, so anything escaping the callee surfaces
    /// only as an unobserved exception at the next GC - by which point the user has seen a
    /// button do nothing with no explanation. This awaits it and records the failure.
    /// </summary>
    private static async void RunSafely(Func<Task> work, string context)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // Shutdown cancelled the token: the expected way for this to end.
        }
        catch (Exception ex)
        {
            Services.CrashLog.Record(ex, context);
        }
    }

    /// <summary>Closes for real, bypassing the minimise-to-tray interception.</summary>
    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The close button is a minimise unless the user has turned that off, or this is a
        // genuine quit from the tray menu. Position is saved either way, so the widget
        // reopens where it was left even if the process is killed while hidden.
        if (!_exiting && _vm.CloseToTray)
        {
            e.Cancel = true;
            HideToTray(explain: true);
            return;
        }

        // Shutdown() closes every open window, which re-enters this method on this same
        // window. Without this guard that recursion never bottoms out and the process dies
        // with a StackOverflowException instead of exiting.
        if (_shuttingDown)
        {
            base.OnClosing(e);
            return;
        }

        _shuttingDown = true;

        SavePosition();
        _timer.Stop();
        _countdownTimer.Stop();
        _trayClickTimer?.Stop();     // null when the desktop has no tray
        _cts.Cancel();

        // Explicitly disposed: a tray icon can otherwise linger in the notification area
        // until the user hovers over it. The notifier's own hidden icon needs the same,
        // plus the window and icon handles it holds open to keep its balloon alive.
        //
        // Unhooked first. Disposal is not instant on every backend, and a click serviced
        // in the gap would run the handler against a window that is on its way out.
        if (_tray is { } tray)
        {
            tray.Clicked -= OnTrayClicked;
            tray.Dispose();
        }

        _tray = null;
        Services.TrayNotifier.Cleanup();

        // Unhooked for the same reason as the tray click: a Solo change already queued on
        // the dispatcher would otherwise reach a handler that walks a closed window's tree.
        if (_soloCursorHandler is { } soloCursor)
        {
            _vm.PropertyChanged -= soloCursor;
            _soloCursorHandler = null;
        }

        base.OnClosing(e);

        // Shutdown is explicit (see App), so closing the window is no longer enough to end
        // the process - the lifetime has to be told. Queued rather than called inline: this
        // is still inside the window's own close, and Shutdown wants to drive that itself.
        if (Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop)
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
    }

    // ---- window position persistence -------------------------------------------------

    private void RestorePosition()
    {
        try
        {
            var state = Services.AppState.Load().Window;
            if (state is null) return;

            // Only restore if the point still lands on a connected screen, otherwise the
            // widget can end up invisible after a monitor change.
            var pos = new PixelPoint(state.X, state.Y);
            if (Screens.ScreenFromPoint(pos) is not null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = pos;
            }
        }
        catch (Exception)
        {
            // A corrupt settings file must never stop the app from opening.
        }
    }

    private void SavePosition()
    {
        try
        {
            Services.AppState.Update(a => a.Window = new Services.AppState.WindowState
            {
                X = Position.X,
                Y = Position.Y,
            });
        }
        catch (Exception)
        {
        }
    }
}
