using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace TokenBurnRate.Services;

/// <summary>
/// Checks GitHub Releases for a newer Velopack-packaged build and, if one exists,
/// downloads and applies it, restarting into the new version.
///
/// Runs in the background at launch, the same shape as <see cref="CheckInService"/>, then
/// again every <see cref="RecheckInterval"/> for as long as the widget runs, and soon after
/// the user widens or narrows what an update may be: a failure here (offline, rate-limited,
/// running unpackaged under `dotnet run`) must never delay startup or surface an error the
/// user cannot act on, and one that never reached GitHub is retried by the window's
/// <see cref="CheckSchedule"/>. There is no user-facing prompt - the widget restarts into
/// the new version on its own.
/// </summary>
public static class UpdateService
{
    /// <summary>
    /// How long after one check settles the next one runs. The widget is left running for
    /// days or weeks, and a check made only at launch never saw a release published after
    /// it - so a running copy sat on its old version until something happened to restart it.
    ///
    /// Not shorter, because anonymous GitHub API calls are limited to 60 an hour per IP
    /// address, and behind a corporate VPN one egress address is shared by every user on it.
    /// A check spends one of them, on the release list; the feed files it then reads come
    /// through ordinary download links, which that limit does not count. A few hours is
    /// still well inside "updates the same day it is released".
    ///
    /// The menu's tooltip is built from this value (MainViewModel.AutoUpdateTooltip), so a
    /// change here needs no second edit there.
    /// </summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(4);

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
    internal enum Tier { Release, Beta, Alpha }

    /// <summary>
    /// The update settings as the context menu holds them, read on the UI thread whenever a
    /// check needs them - see <see cref="CheckAsync"/>.
    /// </summary>
    public readonly record struct Settings(bool AutoUpdate, bool IncludeAlpha, bool IncludeBeta);

    /// <summary>What one UI-thread turn of the restart loop decided - see CheckAsync.</summary>
    private enum Verdict { Hold, Abandon, Shutdown, Restarted }

    /// <summary>
    /// Opt-in switches widening what counts as an update, set from the context menu's
    /// Advanced submenu (see MainViewModel.UpdateIncludeAlpha/UpdateIncludeBeta) and
    /// persisted to AppState rather than passed on the command line. Without either, only
    /// real (non-pre-release) versions are offered, so a user on a stable build stays on
    /// stable builds. The two checkboxes are independent in the UI, but alpha is still the
    /// least stable tier: requesting it here widens the ceiling to also admit beta and
    /// release, whether or not the beta checkbox itself is on.
    ///
    /// Internal so the window can tell a checkbox change that moves this ceiling from one
    /// that does not (ticking BETA while ALPHA is on) - only the first is worth a check.
    /// </summary>
    internal static Tier MaxTierRequested(bool includeAlpha, bool includeBeta)
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

    /// <summary>
    /// A GitHub source whose feed holds only the tiers the settings allow.
    ///
    /// GithubSource reads the 10 newest releases and joins the feed files of all of them,
    /// and UpdateManager then takes the highest version in that joined feed. Its own
    /// "prerelease" switch is a single bool, so it cannot keep an alpha out while letting a
    /// beta in: with only BETA ticked, a newer alpha would top the feed, be refused, and hide
    /// the beta or full release beneath it - on every check, until something newer than the
    /// alpha shipped. Filtering the joined feed by each entry's own version label instead
    /// makes the highest allowed version win. The entries are passed on as they came, since
    /// downloading one needs the GitHub release it carries.
    /// </summary>
    private sealed class TieredGithubSource(string repoUrl, Tier maxTier)
        : GithubSource(repoUrl, accessToken: null, prerelease: true)
    {
        public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger,
            string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            var feed = await base.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease)
                .ConfigureAwait(false);

            return new VelopackAssetFeed
            {
                Assets = feed.Assets.Where(a => TierOf(a.Version) <= maxTier).ToArray(),
            };
        }
    }

    private static UpdateManager NewManager(string repoUrl, Tier maxTier) =>
        new(new TieredGithubSource(repoUrl, maxTier));

    /// <summary>How often a held restart looks again at whatever is holding it.</summary>
    private static readonly TimeSpan RestartHoldPoll = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Failures already written to CrashLog this run, by exception type and message. A check
    /// repeats every RecheckInterval and after settings changes, and CrashLog keeps only a
    /// handful of files per run: one failing the same way each time (a proxy's block page,
    /// a rate limit) would use them all up in one long session and leave no file for a
    /// crash later on that is actually worth investigating. A failure of a new kind still
    /// gets its own file, and the shared log gets a line for every one either way. Locked
    /// because the catch that reads it runs on a thread-pool thread.
    /// </summary>
    private static readonly HashSet<string> CrashLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// Checks for, downloads and applies an update, and reports whether the attempt is
    /// settled - true covers "found nothing to update to", "update applied" (which ends the
    /// process before returning anyway), "abandoned because the settings changed" and any
    /// failure not worth retrying; false means it never reached GitHub (see
    /// <see cref="CheckSchedule.IsTransportFailure"/>), so the caller should try again. Never
    /// throws.
    ///
    /// A check can run hours into the session rather than at launch, so the settings are
    /// not taken once at the start: <paramref name="settings"/> is asked on the UI thread at
    /// each point that acts on them - whether to contact GitHub at all and which tiers to
    /// accept, whether the candidate found is still wanted before downloading it, and again
    /// before restarting. A change in between abandons the check: with auto-update off the
    /// widget must never restart itself, and a tier unticked mid-download must not be
    /// installed. The change itself asks for a fresh check where one is due (see the
    /// window's update settings handler).
    ///
    /// <paramref name="busy"/> is asked before restarting - true holds the restart until it
    /// clears, because restarting would throw away whatever the user was halfway through,
    /// such as a Claude sign-in waiting for its pasted code.
    ///
    /// <paramref name="prepareRestart"/> runs on the UI thread just before the restart, for
    /// the window to save what it otherwise saves only when it closes. The restart ends the
    /// process outright (Velopack exits it), so the window's own close never runs - and an
    /// update can land hours into a session, after the widget has been moved.
    ///
    /// <paramref name="shutdown"/> is the app's shutdown token: cancelling it abandons an
    /// in-flight download or held restart and reports the attempt settled, so an exit is not
    /// mistaken for a network failure worth retrying into a closing process.
    /// </summary>
    public static async Task<bool> CheckAsync(Func<Settings> settings, Func<bool> busy,
        Action prepareRestart, CancellationToken shutdown)
    {
        try
        {
            var initial = await OnUiThread(settings);
            if (!initial.AutoUpdate)
            {
                // Claimed while it was on, but turned off before this first read. With it off
                // the widget never contacts GitHub Releases - not even for the release list.
                DiagLog("stopping: auto-update was turned off before the check started");
                return true;
            }

            var maxTier = MaxTierRequested(initial.IncludeAlpha, initial.IncludeBeta);

            DiagLog($"start: maxTier={maxTier}");

            var manager = NewManager(RepoUrl, maxTier);

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
                manager = NewManager(LegacyRepoUrl, maxTier);
            }

            // The feed only offers tiers up to maxTier (see TieredGithubSource), so null here
            // also covers "the only newer builds are of a tier the settings do not allow".
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                DiagLog($"stopping: no update found up to tier {maxTier}");
                return true;
            }

            var candidate = update.TargetFullRelease.Version;
            var candidateTier = TierOf(candidate);

            // Whether the candidate is still wanted under the settings as they stand at the
            // moment of asking - each gate below asks afresh rather than trusting the
            // settings the check began with.
            bool Wanted(Settings s) =>
                s.AutoUpdate && candidateTier <= MaxTierRequested(s.IncludeAlpha, s.IncludeBeta);

            if (!Wanted(await OnUiThread(settings)))
            {
                DiagLog($"stopping: {candidate} is no longer wanted - auto-update was turned off or its tier unticked during the check");
                return true;
            }

            DiagLog($"update found: {candidate} (tier {candidateTier})");

            await manager.DownloadUpdatesAsync(update, cancelToken: shutdown).ConfigureAwait(false);
            DiagLog("download complete");

            var held = false;
            while (true)
            {
                // One UI-thread turn both decides and, when the answer is go, restarts. Exit
                // (OnClosing, which cancels shutdown) and the settings handlers run on the UI
                // thread too, so neither can land between the decision and the restart -
                // deciding in one hop and restarting after another left exactly that gap.
                var verdict = await OnUiThread(() =>
                {
                    if (shutdown.IsCancellationRequested) return Verdict.Shutdown;
                    if (!Wanted(settings())) return Verdict.Abandon;
                    if (busy()) return Verdict.Hold;

                    prepareRestart();

                    // TrayNotifier keeps unsynchronised static Win32 handles and is otherwise
                    // only ever called from the UI thread (minimise-to-tray), which this is.
                    // Shown before the restart so the balloon is queued with the shell before
                    // the process goes away under it.
                    if (TrayNotifier.IsSupported)
                    {
                        TrayNotifier.Show("Token Burn Rate update installed",
                                          "Restarting to finish updating...");
                    }

                    DiagLog("applying and restarting");
                    manager.ApplyUpdatesAndRestart(update);
                    return Verdict.Restarted;   // unreached in practice - the call above ends the process
                });

                switch (verdict)
                {
                    case Verdict.Restarted:
                        return true;

                    case Verdict.Shutdown:
                        // The download is kept on purpose: the user wanted this update and only
                        // exited before it could restart, so Velopack installs it at the next
                        // launch, before the UI starts (see Program.cs).
                        DiagLog("stopping: shutting down before restarting - the update installs at the next launch");
                        return true;

                    case Verdict.Abandon:
                        DiagLog("stopping: auto-update was turned off or its tier unticked before restarting");
                        DiscardDownload(update.TargetFullRelease);
                        return true;
                }

                if (!held)
                {
                    DiagLog("restart held: a sign-in, dialog or menu is open");
                    held = true;
                }

                await Task.Delay(RestartHoldPoll, shutdown).ConfigureAwait(false);
            }
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
            // timeout) is worth retrying - see CheckSchedule. A GitHub rate limit or a
            // genuinely unpackaged dev build would fail the same way again immediately.
            var transport = CheckSchedule.IsTransportFailure(ex, shutdown);

            // That same split decides CrashLog. A transport failure is expected while a VPN
            // comes up and is retried, and CrashLog keeps only a handful of files per run -
            // a slow boot's failures would use them all up, leaving no file for a crash
            // later in the session that is actually worth investigating. The shared log's
            // one line is enough for those - and for a repeat of a failure already on file,
            // see CrashLogged.
            if (transport)
            {
                AppLog.Warn($"Update: check could not reach GitHub, will retry - {ex.GetType().Name}: {ex.Message}");
            }
            else
            {
                AppLog.Error("Update: check failed", ex);

                bool firstOfItsKind;
                lock (CrashLogged) firstOfItsKind = CrashLogged.Add($"{ex.GetType().FullName}: {ex.Message}");
                if (firstOfItsKind) CrashLog.Record(ex, "update check");
            }

            return !transport;
        }
    }

    /// <summary>
    /// Deletes the package of an update that was downloaded and then abandoned. Left in
    /// Velopack's packages folder it would be installed at the next launch: VelopackApp's
    /// Run() applies any newer package it finds waiting there before the UI starts. That is
    /// what a wanted update relies on when the user exits while its restart is held, so the
    /// behaviour stays on and the unwanted package is removed instead. Best effort: a failure
    /// is logged, and costs at worst the one install it was meant to prevent.
    /// </summary>
    private static void DiscardDownload(VelopackAsset asset)
    {
        try
        {
            // The path Velopack downloads to: the packages folder plus the file name. Its own
            // helper for this (GetLocalPackagePath) is internal, so it is rebuilt here; that
            // helper only replaces characters a file name cannot hold, which CI's package
            // names never contain. Checked rather than assumed, because a miss would leave
            // the package to be installed at the next launch.
            var dir = VelopackLocator.Current.PackagesDir;
            var path = dir is null ? null : Path.Combine(dir, Path.GetFileName(asset.FileName));

            if (path is null || !File.Exists(path))
            {
                AppLog.Warn($"Update: abandoned download {asset.FileName} not found in '{dir}' - it may still be installed at the next launch");
                return;
            }

            File.Delete(path);
            DiagLog($"deleted the abandoned download {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Update: could not delete the abandoned download - {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Asks a window-state question on the UI thread, where that state lives.</summary>
    private static async Task<T> OnUiThread<T>(Func<T> question) =>
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
