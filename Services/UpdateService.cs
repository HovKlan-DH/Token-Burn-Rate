using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace TokenBurnRate.Services;

/// <summary>
/// Checks GitHub Releases for a newer Velopack-packaged build and, if one exists,
/// downloads and applies it, restarting into the new version.
///
/// Runs once per launch in the background, the same shape as <see cref="CheckInService"/>:
/// a failure here (offline, rate-limited, running unpackaged under `dotnet run`) must never
/// delay startup or surface an error the user cannot act on, and one that never reached
/// GitHub is retried by the window's <see cref="StartupRetry"/> schedule. There is no
/// user-facing prompt - the widget restarts into the new version on its own.
/// </summary>
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/HovKlan-DH/Token-Burn-Rate";

    /// <summary>
    /// The repo and Velopack packId used before the rename to "Token-Burn-Rate".
    ///
    /// Velopack identifies an installed app by the packId baked into its own package
    /// manifest, and matches feed entries against it. A build installed before the rename
    /// therefore reports <c>TokenBurnRate</c> forever - nothing in an update rewrites it -
    /// and would never match a release packed under the new id, so the app would sit on its
    /// installed version silently and permanently. GitHub redirects the renamed repo's URL,
    /// so the old feed is still reachable and is where those installs are served from until
    /// the user reinstalls under the new id. New installs never take this path.
    /// </summary>
    private const string LegacyAppId = "TokenBurnRate";
    private const string LegacyRepoUrl = "https://github.com/HovKlan-DH/TokenBurnRate";

    /// <summary>
    /// The three release tiers CI ever tags, in ascending stability - see
    /// determine-version's is_prerelease step and prerelease_check's stage labels in
    /// build-and-release.yml. There is deliberately no "rc" tier: alpha, beta, and a bare
    /// release are the only stages this project uses.
    /// </summary>
    private enum Tier { Release, Beta, Alpha }

    /// <summary>
    /// Opt-in switches widening what counts as an update, set from the context menu's
    /// Advanced submenu (see MainViewModel.UpdateIncludeAlpha/UpdateIncludeBeta) and
    /// persisted to AppState rather than passed on the command line. Without either, only
    /// real (non-pre-release) versions are offered, so a user on a stable build stays on
    /// stable builds. The two checkboxes are independent in the UI, but alpha is still the
    /// least stable tier: requesting it here widens the ceiling to also admit beta and
    /// release, whether or not the beta checkbox itself is on.
    /// </summary>
    private static Tier MaxTierRequested(bool includeAlpha, bool includeBeta)
    {
        if (includeAlpha) return Tier.Alpha;
        if (includeBeta) return Tier.Beta;
        return Tier.Release;
    }

    /// <summary>
    /// Where a candidate version's release label places it. Matches the tags CI ever
    /// produces (-alpha.N, -beta.N, or none) - anything else unrecognised is treated as the
    /// least trusted tier rather than silently accepted.
    /// </summary>
    private static Tier TierOf(Velopack.SemanticVersion version)
    {
        if (!version.IsPrerelease) return Tier.Release;
        var label = version.ReleaseLabels.FirstOrDefault() ?? "";
        return label.Equals("beta", StringComparison.OrdinalIgnoreCase) ? Tier.Beta : Tier.Alpha;
    }

    private static UpdateManager NewManager(string repoUrl) =>
        new(new GithubSource(repoUrl, accessToken: null, prerelease: true));

    /// <summary>How often a held restart looks again at whatever is holding it.</summary>
    private static readonly TimeSpan RestartHoldPoll = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Checks for, downloads and applies an update, and reports whether the attempt is
    /// settled - true covers "found nothing to update to", "update applied" (which restarts
    /// the process before returning anyway) and any failure not worth retrying; false means
    /// it never reached GitHub (see <see cref="StartupRetry.IsTransportFailure"/>), so the
    /// caller should try again. Never throws.
    ///
    /// A retry can run minutes into the session rather than at launch, so both callbacks
    /// are asked on the UI thread at the moments that matter, not only when the check began:
    /// <paramref name="stillWanted"/> (auto-update is still switched on) before downloading
    /// and again before restarting - false abandons the update, since with it off the widget
    /// must never restart itself - and <paramref name="busy"/> (a sign-in or dialog is open)
    /// before restarting - true holds the restart until it clears, because restarting would
    /// throw away whatever the user was halfway through, such as a Claude sign-in waiting
    /// for its pasted code.
    ///
    /// <paramref name="shutdown"/> is the app's shutdown token: cancelling it abandons an
    /// in-flight download or held restart and reports the attempt settled, so an exit is not
    /// mistaken for a network failure worth retrying into a closing process.
    /// </summary>
    public static async Task<bool> CheckOnLaunchAsync(bool includeAlpha, bool includeBeta,
        Func<bool> stillWanted, Func<bool> busy, CancellationToken shutdown)
    {
        try
        {
            var maxTier = MaxTierRequested(includeAlpha, includeBeta);

            DiagLog($"start: maxTier={maxTier}");

            // Ask the source for the widest pool (every tier CI ever tags) and then filter
            // by parsed version label ourselves - GithubSource's own "prerelease" switch is
            // a single bool and cannot distinguish alpha from beta, but every release CI
            // makes still has a plain semver label to read that distinction back out of.
            var manager = NewManager(RepoUrl);

            DiagLog($"AppId={manager.AppId}, IsInstalled={manager.IsInstalled}, CurrentVersion={manager.CurrentVersion}");

            // Throws when running from a build vpk never packaged (e.g. `dotnet run`, or a
            // manually-copied publish folder) - exactly the case where there is nothing
            // sensible to update, so it is treated the same as "no update found". Checked
            // before the AppId below, which has nothing to report on an unpackaged build.
            if (!manager.IsInstalled)
            {
                DiagLog("stopping: not installed");
                return true;
            }

            // A pre-rename install cannot match the new packId - see LegacyAppId - so it is
            // pointed back at the feed that still carries its own id.
            if (string.Equals(manager.AppId, LegacyAppId, StringComparison.OrdinalIgnoreCase))
            {
                DiagLog($"legacy appId {manager.AppId}: using {LegacyRepoUrl}");
                manager = NewManager(LegacyRepoUrl);
            }

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                DiagLog("stopping: CheckForUpdatesAsync returned null (no update found)");
                return true;
            }

            var candidateTier = TierOf(update.TargetFullRelease.Version);
            if (candidateTier > maxTier)
            {
                // A newer build exists but is a less stable tier than requested - e.g. the
                // latest published release is an alpha and neither flag was passed. Superseded
                // releases are deleted by the workflow, so there is no older, allowed release
                // to fall back to instead; this is simply "nothing to update to right now".
                DiagLog($"stopping: candidate {update.TargetFullRelease.Version} is tier {candidateTier}, above requested max {maxTier}");
                return true;
            }

            DiagLog($"update found: {update.TargetFullRelease.Version} (tier {candidateTier})");

            if (!await OnUiThread(stillWanted))
            {
                DiagLog("stopping: auto-update was turned off before downloading");
                return true;
            }

            await manager.DownloadUpdatesAsync(update, cancelToken: shutdown).ConfigureAwait(false);
            DiagLog("download complete");

            var held = false;
            while (true)
            {
                if (!await OnUiThread(stillWanted))
                {
                    DiagLog("stopping: auto-update was turned off before restarting");
                    return true;
                }

                if (!await OnUiThread(busy)) break;

                if (!held)
                {
                    DiagLog("restart held: a sign-in or dialog is open");
                    held = true;
                }

                await Task.Delay(RestartHoldPoll, shutdown).ConfigureAwait(false);
            }

            DiagLog("applying and restarting");

            // TrayNotifier keeps unsynchronised static Win32 handles and is otherwise only
            // ever called from the UI thread (minimise-to-tray); the awaits above left this
            // on a thread-pool thread, so hop back rather than race it. Awaited so the
            // balloon is actually queued with the shell before the restart below kills the
            // process out from under it.
            if (TrayNotifier.IsSupported)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    TrayNotifier.Show("Token Burn Rate update installed",
                                      "Restarting to finish updating..."));
            }

            manager.ApplyUpdatesAndRestart(update);
            return true;    // unreached in practice - the call above ends the process
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // The user exited mid-download. An orderly shutdown, not a failure: kept out of
            // CrashLog, which exists for things worth investigating, and reported settled so
            // no retry is armed against a process that is going away.
            DiagLog("stopping: shutdown cancelled the check");
            return true;
        }
        catch (Exception ex)
        {
            // Offline, GitHub rate limit, unpackaged dev build: none of it should affect the
            // widget, and there is nothing actionable to tell the user. Recorded rather than
            // silently dropped so a "never updates" report has something to go on.
            //
            // Only a transport-shaped failure (no route yet, DNS not resolving, connect
            // timeout) is worth retrying - see StartupRetry. A GitHub rate limit or a
            // genuinely unpackaged dev build would fail the same way again immediately.
            var transport = StartupRetry.IsTransportFailure(ex, shutdown);

            // That same split decides CrashLog. A transport failure is expected while a VPN
            // comes up and is retried, and CrashLog keeps only a handful of files per run -
            // a slow boot's failures would use them all up, leaving no file for a crash
            // later in the session that is actually worth investigating. The shared log's
            // one line is enough for those.
            if (transport)
            {
                AppLog.Warn($"Update: check could not reach GitHub, will retry - {ex.GetType().Name}: {ex.Message}");
            }
            else
            {
                AppLog.Error("Update: check failed", ex);
                CrashLog.Record(ex, "update check");
            }

            return !transport;
        }
    }

    /// <summary>Asks a window-state question on the UI thread, where that state lives.</summary>
    private static async Task<bool> OnUiThread(Func<bool> question) =>
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(question);

    /// <summary>
    /// Routes the update check's running commentary into the same log everything else uses
    /// (see <see cref="AppLog"/>).
    ///
    /// This used to write its own "update-diag" side-file, added while chasing why the Linux
    /// AppImage never updated. It stays - but in the shared log rather than a file of its
    /// own, because a silent self-update that restarts the app is the single likeliest cause
    /// of the "it went blank / it restarted itself" report the shared log exists to answer,
    /// and a user sending that log in should not have to know to attach a second one.
    ///
    /// Written unconditionally, not just on error, so a silent early return is visible too.
    /// </summary>
    private static void DiagLog(string message) => AppLog.Info($"Update: {message}");
}
