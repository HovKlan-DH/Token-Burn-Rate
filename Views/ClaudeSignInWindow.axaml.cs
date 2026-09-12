using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using TokenBurnRate.Services;

namespace TokenBurnRate.Views;

/// <summary>
/// The "Sign in to Claude" dialog: opens the browser to Anthropic's authorize page, then
/// takes the code the user is shown there and redeems it.
///
/// A paste box rather than the device-code button the Copilot panel uses, because the two
/// flows genuinely differ: GitHub's device flow hands the app a token by polling, while
/// Anthropic offers only authorization-code + PKCE against a hosted callback page that
/// displays the code. There is no redirect this app could listen on, so the copy-paste step
/// is the flow, not a shortcut - see ClaudeOAuth for the detail.
///
/// The dialog owns the <see cref="ClaudeOAuth"/> instance for the duration of one sign-in,
/// so the PKCE verifier lives exactly as long as the window and goes out of scope with it.
/// </summary>
public partial class ClaudeSignInWindow : Window
{
    private readonly ClaudeOAuth _oauth = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _busy;

    /// <summary>True once a code has been redeemed and tokens stored.</summary>
    private bool _signedIn;

    /// <summary>
    /// Set when the window has closed, so a redeem still in flight stops short of updating
    /// controls that are no longer on screen.
    /// </summary>
    private bool _closed;

    public ClaudeSignInWindow()
    {
        InitializeComponent();

        var codeBox = this.FindControl<TextBox>("CodeBox")!;
        var signInButton = this.FindControl<Button>("SignInButton")!;
        var cancelButton = this.FindControl<Button>("CancelButton")!;
        var openBrowser = this.FindControl<Button>("OpenBrowserButton")!;

        openBrowser.Click += (_, _) => OpenAuthorizePage();

        // Both handlers go through RunSafely rather than being bare async lambdas. An
        // async void handler that throws takes the process down with it, and these run a
        // 120-second network exchange and then touch controls that may be gone if the
        // window closed underneath them - see MainWindow's handlers for the same guard.
        signInButton.Click += (_, _) => RunSafely(() => RedeemAsync(codeBox.Text));

        // Enter in the box is the obvious way to submit a pasted code.
        codeBox.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            e.Handled = true;
            RunSafely(() => RedeemAsync(codeBox.Text));
        };

        cancelButton.Click += (_, _) => Close();

        // The widget itself is Topmost whenever it is pinned (MainWindow's Topmost binding),
        // and a modal dialog does not automatically outrank it: two topmost windows are just
        // two topmost windows, so the dialog opened *behind* the widget and could not be
        // reached at all - the widget covered it and stayed in front. Raising it once the
        // handle exists puts it decisively on top; Focus() then makes sure the paste box has
        // the keyboard, since a window raised this way does not always take focus with it.
        Opened += (_, _) =>
        {
            Activate();
            Focus();
            this.FindControl<TextBox>("CodeBox")?.Focus();
        };

        // Abandons the attempt if the window is closed mid-flow, so a verifier never
        // outlives the dialog that created it.
        //
        // Cancel but deliberately do not Dispose: a redeem may still be awaiting on this
        // token, and disposing the source out from under it turns a clean cancellation into
        // an ObjectDisposedException on a continuation nothing is left to catch. The source
        // is finalizable and this dialog is short-lived, so letting it go with the window
        // costs nothing; _closed is what stops the continuation touching dead controls.
        Closed += (_, _) =>
        {
            _closed = true;
            _oauth.Cancel();
            _cts.Cancel();
        };
    }

    /// <summary>
    /// Runs one sign-in. Returns true when tokens were stored, so the caller can refresh
    /// the panel immediately rather than waiting for the next poll.
    /// </summary>
    public static async Task<bool> SignInAsync(Window owner)
    {
        var window = new ClaudeSignInWindow();

        // Started before the dialog is shown so the browser is already on its way while the
        // user is reading step one.
        window.OpenAuthorizePage();

        // The widget is normally pinned above every other window, which left this dialog
        // stuck underneath it and unreachable - the whole point of the pin is that nothing
        // covers the widget, including its own dialogs. Standing the pin down for exactly as
        // long as the dialog is up is the only thing that reliably wins: two topmost windows
        // have no defined order between them. Restored in the finally so a crash mid-sign-in
        // cannot leave the widget silently unpinned.
        //
        // Set on the window rather than through MainViewModel.Pinned deliberately: that
        // property persists to the state file and drives the pin glyph, so routing this
        // through it would write a preference the user never expressed and flip the button
        // under them. Topmost is bound one-way to Pinned, which would normally overwrite
        // this - but Pinned only changes when the pin button is clicked, and that cannot
        // happen while this dialog is modal over the window holding it.
        var wasTopmost = owner.Topmost;
        owner.Topmost = false;
        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            owner.Topmost = wasTopmost;
        }

        return window._signedIn;
    }

    /// <summary>
    /// Begins (or restarts) the attempt and opens its URL in the default browser.
    ///
    /// Every press issues a fresh verifier and state, which is deliberate: if the user goes
    /// back to the browser it is because the first attempt was abandoned, and reusing its
    /// PKCE material would leave a live verifier bound to a code that may already have been
    /// seen. The cost is that a code from the earlier page will no longer be accepted, which
    /// is the correct outcome.
    /// </summary>
    private void OpenAuthorizePage()
    {
        var attempt = _oauth.Begin();
        TryOpenBrowser(attempt.AuthorizeUrl);
    }

    private async Task RedeemAsync(string? pasted)
    {
        if (_busy) return;

        if (string.IsNullOrWhiteSpace(pasted))
        {
            ShowError("Paste the code Claude showed you after approving.");
            return;
        }

        _busy = true;
        SetBusy(true);
        ShowError(null);

        try
        {
            var result = await _oauth.RedeemAsync(pasted, _cts.Token).ConfigureAwait(true);

            // The window can have been closed while the exchange was in flight. The tokens
            // are saved either way - that happens inside RedeemAsync - so the sign-in still
            // counts; there is simply no dialog left to close or report into.
            if (_closed) { _signedIn = result.Success; return; }

            if (result.Success)
            {
                _signedIn = true;
                Close();
                return;
            }

            ShowError(result.Error ?? "Sign-in failed.");

            // A refused code leaves no pending attempt (the verifier is spent either way),
            // so the user needs a fresh page rather than another paste of the same code.
            if (!_oauth.HasPendingAttempt)
                ShowStatus("Use \"Open the sign-in page again\" to retry.");
        }
        catch (OperationCanceledException)
        {
            // Window closed mid-exchange; nothing to report to a dialog that is gone.
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        if (_closed) return;

        // Null-tolerant lookups, not null-forgiving: the window may be tearing down around
        // this call, and a "!" that turned into a NullReferenceException here would surface
        // on a continuation with no handler left above it.
        var button = this.FindControl<Button>("SignInButton");
        var box = this.FindControl<TextBox>("CodeBox");
        if (button is not null) button.IsEnabled = !busy;
        if (box is not null) box.IsEnabled = !busy;

        // The exchange can take the better part of a minute (see ClaudeOAuth), which is
        // long enough that a dialog with no feedback reads as hung.
        if (busy) ShowStatus("Signing in…");
        else ShowStatus(null);
    }

    private void ShowError(string? message)
    {
        if (_closed) return;
        if (this.FindControl<TextBlock>("ErrorText") is not { } error) return;

        error.Text = message ?? "";
        error.IsVisible = !string.IsNullOrEmpty(message);
    }

    private void ShowStatus(string? message)
    {
        if (_closed) return;
        if (this.FindControl<TextBlock>("StatusText") is not { } status) return;

        status.Text = message ?? "";
        status.IsVisible = !string.IsNullOrEmpty(message);
    }

    /// <summary>
    /// Runs an async handler so nothing can escape into an async void continuation. The
    /// dialog is a modal over the widget: an unhandled exception here would take the whole
    /// process down rather than just failing the sign-in, so the last resort is to report it
    /// in the dialog and leave the app standing.
    /// </summary>
    private async void RunSafely(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // Window closed mid-exchange; nothing to report to a dialog that is gone.
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
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
            // Headless or restricted desktop: the dialog still explains the step, and the
            // user can reach the page from another device.
        }
    }
}
