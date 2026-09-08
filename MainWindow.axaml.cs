using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Token_Burn_Rate.ViewModels;

namespace Token_Burn_Rate;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();

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

        if (this.FindControl<Button>("SignInButton") is { } signIn)
            signIn.Click += async (_, _) => await _vm.SignInToGitHubAsync(_cts.Token);

        // Copilot's quota is a remote call and Claude's parse is incremental, so a 60s
        // cadence keeps the display live without hammering either source.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += async (_, _) => await _vm.RefreshAsync(_cts.Token);
        _timer.Start();

        Opened += async (_, _) => await _vm.RefreshAsync(_cts.Token);
    }

    private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SavePosition();
        _timer.Stop();
        _cts.Cancel();
        base.OnClosing(e);
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
