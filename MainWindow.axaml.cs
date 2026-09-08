using System;
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
    /// lets the window close. Without it, "Quit" from the tray would only hide the window
    /// again and the process would never end.
    /// </summary>
    private bool _exiting;

    /// <summary>
    /// Guards against Shutdown() re-entering OnClosing on the window that is already
    /// closing. See the comment at the point of use.
    /// </summary>
    private bool _shuttingDown;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        RestorePosition();

        // Drag anywhere on the header, since the window has no title bar to grab.
        if (this.FindControl<Grid>("DragHandle") is { } handle)
            handle.PointerPressed += OnDragHandlePressed;

        if (this.FindControl<Button>("RefreshButton") is { } refresh)
            refresh.Click += async (_, _) => await _vm.RefreshAsync(_cts.Token);

        if (this.FindControl<Button>("CloseButton") is { } close)
            close.Click += (_, _) => Close();

        if (this.FindControl<Button>("PinButton") is { } pin)
            pin.Click += (_, _) => _vm.Pinned = !_vm.Pinned;

        if (this.FindControl<MenuItem>("ProjectPageItem") is { } projectPage)
            projectPage.Click += (_, _) => _vm.OpenProjectPage();

        if (this.FindControl<Button>("SignInButton") is { } signIn)
            signIn.Click += async (_, _) => await _vm.SignInToGitHubAsync(_cts.Token);

        // Clicking a section header collapses or expands that panel.
        HookHeader("ClaudeHeader", () => _vm.ClaudeSolo, () => _vm.ClaudeCollapsed = !_vm.ClaudeCollapsed);
        HookHeader("CopilotHeader", () => _vm.CopilotSolo, () => _vm.CopilotCollapsed = !_vm.CopilotCollapsed);
        HookHeader("PacingHeader", () => _vm.PacingSolo, () => _vm.PacingCollapsed = !_vm.PacingCollapsed);

        _vm.LoadCollapsedState();
        SetUpTray();

        // Copilot's quota is a remote call and Claude's parse is incremental, so a 60s
        // cadence keeps the display live without hammering either source.
        _timer = new DispatcherTimer { Interval = MainViewModel.RefreshInterval };
        _timer.Tick += async (_, _) => await _vm.RefreshAsync(_cts.Token);
        _timer.Start();

        // Separate one-second tick so the countdowns visibly run down between refreshes.
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => _vm.TickCountdowns();
        _countdownTimer.Start();

        Opened += async (_, _) =>
        {
            ApplyCursors();     // the visual tree is only complete once the window is open
            _vm.TickCountdowns();
            await _vm.RefreshAsync(_cts.Token);
        };
    }

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
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(_vm.ClaudeSolo) or nameof(_vm.CopilotSolo)
                or nameof(_vm.PacingSolo))
                ApplyHeaderCursors();
        };
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
            var show = new NativeMenuItem("Show");
            show.Click += (_, _) => RestoreFromTray();

            // Quitting has to live here: with close minimising instead of exiting, the tray
            // is the only place left that can actually end the process.
            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) => ExitApplication();

            _tray = new TrayIcon
            {
                Icon = Icon,
                ToolTipText = "TokenBurnRate",
                IsVisible = true,
                Menu = new NativeMenu { show, quit },
            };

            // Left-clicking the icon is the fast path back; the menu is for everything else.
            _tray.Clicked += (_, _) => RestoreFromTray();

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
    /// Tells the user the app is still running, the first time the window disappears into
    /// the tray. Only the first time: after that the behaviour is known, and a notice on
    /// every close would be nagging.
    ///
    /// The flag is only set once the balloon actually appeared, so a failed notification
    /// is retried on the next minimise rather than being silently spent.
    /// </summary>
    private void NotifyMinimisedToTrayOnce()
    {
        if (Services.AppState.Load().TrayNoticeShown == true) return;

        var shown = Services.TrayNotifier.Show(
            "TokenBurnRate is still running",
            "The widget is in the notification area. Click its icon to bring it back, or "
            + "right-click for Quit.");

        if (shown) Services.AppState.Update(a => a.TrayNoticeShown = true);
    }

    /// <summary>Brings the widget back from the tray and puts it in front.</summary>
    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
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
            SavePosition();
            e.Cancel = true;
            Hide();
            NotifyMinimisedToTrayOnce();
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
        _cts.Cancel();

        // Explicitly disposed: a tray icon can otherwise linger in the notification area
        // until the user hovers over it.
        _tray?.Dispose();
        _tray = null;

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
