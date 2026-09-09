using System;
using System.IO;
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
    /// Opt-in flag: without it, only real (non-pre-release) versions are offered, so a user
    /// on a stable build stays on stable builds. Read directly from
    /// <see cref="Environment.GetCommandLineArgs"/> rather than threaded in from
    /// <c>Main(string[] args)</c>, since <see cref="CheckOnLaunch"/> is called parameterless
    /// from MainWindow's Opened handler.
    /// </summary>
    private static bool PrereleaseRequested =>
        Array.Exists(Environment.GetCommandLineArgs(),
            a => string.Equals(a, "--update-prerelease", StringComparison.OrdinalIgnoreCase));

    public static void CheckOnLaunch()
    {
        _ = CheckOnLaunchAsync();
    }

    private static async Task CheckOnLaunchAsync()
    {
        try
        {
            // TEMPORARY diagnostics while chasing why Linux/AppImage never updates - remove
            // once that's root-caused. Writes unconditionally (not just on error) so a
            // silent early-return is visible too.
            DiagLog($"start: prerelease={PrereleaseRequested}, argv={string.Join(' ', Environment.GetCommandLineArgs())}");

            // Pre-releases only count as updates when --update-prerelease is passed; every
            // release so far being an alpha means the default (stable-only) has nothing to
            // update to until the first bare X.Y.Z ships, which is expected.
            var manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: PrereleaseRequested));

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
            DiagLog(update is null
                ? "stopping: CheckForUpdatesAsync returned null (no update found)"
                : $"update found: {update.TargetFullRelease.Version}");
            if (update is null) return;

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
