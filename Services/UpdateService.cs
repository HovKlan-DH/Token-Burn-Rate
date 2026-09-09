using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace TokenBurnRate.Services;

/// <summary>
/// Checks GitHub Releases for a newer Velopack-packaged build and, if one exists,
/// downloads and applies it, restarting into the new version.
///
/// Runs once per launch, fire-and-forget, the same shape as <see cref="CheckInService"/>:
/// a failure here (offline, rate-limited, running unpackaged under `dotnet run`) must never
/// delay startup or surface an error the user cannot act on. There is no user-facing
/// prompt - the update simply appears the next time the widget starts.
/// </summary>
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/HovKlan-DH/TokenBurnRate";

    /// <summary>
    /// The three release tiers CI ever tags, in ascending stability - see
    /// determine-version's is_prerelease step and prerelease_check's stage labels in
    /// build-and-release.yml. There is deliberately no "rc" tier: alpha, beta, and a bare
    /// release are the only stages this project uses.
    /// </summary>
    private enum Tier { Release, Beta, Alpha }

    /// <summary>
    /// Opt-in flags widening what counts as an update, read from the given argv rather
    /// than threaded in from <c>Main(string[] args)</c>, since <see cref="CheckOnLaunch"/>
    /// is called parameterless from MainWindow's Opened handler. Without either, only real
    /// (non-pre-release) versions are offered, so a user on a stable build stays on stable
    /// builds. --update-include-alpha implies beta too: alpha is the least stable tier, so
    /// wanting it means wanting anything at least as stable as well.
    /// </summary>
    private static Tier MaxTierRequested(string[] args)
    {
        if (args.Contains("--update-include-alpha", StringComparer.OrdinalIgnoreCase)) return Tier.Alpha;
        if (args.Contains("--update-include-beta", StringComparer.OrdinalIgnoreCase)) return Tier.Beta;
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

    public static void CheckOnLaunch()
    {
        _ = CheckOnLaunchAsync();
    }

    private static async Task CheckOnLaunchAsync()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            var maxTier = MaxTierRequested(args);

            // TEMPORARY diagnostics while chasing why Linux/AppImage never updates - remove
            // once that's root-caused. Writes unconditionally (not just on error) so a
            // silent early-return is visible too.
            DiagLog($"start: maxTier={maxTier}, argv={string.Join(' ', args)}");

            // Ask the source for the widest pool (every tier CI ever tags) and then filter
            // by parsed version label ourselves - GithubSource's own "prerelease" switch is
            // a single bool and cannot distinguish alpha from beta, but every release CI
            // makes still has a plain semver label to read that distinction back out of.
            var manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: true));

            DiagLog($"IsInstalled={manager.IsInstalled}, CurrentVersion={manager.CurrentVersion}");

            // Throws when running from a build vpk never packaged (e.g. `dotnet run`, or a
            // manually-copied publish folder) - exactly the case where there is nothing
            // sensible to update, so it is treated the same as "no update found".
            if (!manager.IsInstalled)
            {
                DiagLog("stopping: not installed");
                return;
            }

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                DiagLog("stopping: CheckForUpdatesAsync returned null (no update found)");
                return;
            }

            var candidateTier = TierOf(update.TargetFullRelease.Version);
            if (candidateTier > maxTier)
            {
                // A newer build exists but is a less stable tier than requested - e.g. the
                // latest published release is an alpha and neither flag was passed. Superseded
                // releases are deleted by the workflow, so there is no older, allowed release
                // to fall back to instead; this is simply "nothing to update to right now".
                DiagLog($"stopping: candidate {update.TargetFullRelease.Version} is tier {candidateTier}, above requested max {maxTier}");
                return;
            }

            DiagLog($"update found: {update.TargetFullRelease.Version} (tier {candidateTier})");

            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            DiagLog("download complete, applying and restarting");

            // TrayNotifier keeps unsynchronised static Win32 handles and is otherwise only
            // ever called from the UI thread (minimise-to-tray); the awaits above left this
            // on a thread-pool thread, so hop back rather than race it. Awaited so the
            // balloon is actually queued with the shell before the restart below kills the
            // process out from under it.
            if (TrayNotifier.IsSupported)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    TrayNotifier.Show("TokenBurnRate update installed",
                                      "Restarting to finish updating..."));
            }

            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            DiagLog($"exception: {ex}");

            // Offline, GitHub rate limit, unpackaged dev build: none of it should affect the
            // widget, and there is nothing actionable to tell the user. Recorded rather than
            // silently dropped so a "never updates" report has something to go on.
            CrashLog.Record(ex, "update check");
        }
    }

    // TEMPORARY: mirrors CrashLog's beside-the-exe/%LOCALAPPDATA% fallback so this shows up next
    // to the crash logs the user already knows to look for. Remove alongside the DiagLog
    // calls above once the Linux/AppImage no-update issue is root-caused.
    private static void DiagLog(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(AppState.Path);
            var path = string.IsNullOrWhiteSpace(dir)
                ? "TokenBurnRate.update-diag.log"
                : Path.Combine(dir, "TokenBurnRate.update-diag.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never themselves crash the update check.
        }
    }
}
