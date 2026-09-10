using System;
using System.IO;

namespace TokenBurnRate.Services;

/// <summary>
/// Carries files over from the paths this app used before it was renamed from
/// "TokenBurnRate" to "Token-Burn-Rate".
///
/// The rename moved the per-user folder (%LOCALAPPDATA%\TokenBurnRate ->
/// ...\Token-Burn-Rate) and the state file inside it. Without this, an existing install
/// silently starts from a blank state: window position, colours, hidden panels, font scale
/// and refresh interval all revert, and - worse - the pacing opening balances are re-anchored
/// to the current balance, zeroing today's and this week's usage partway through the period.
/// Those balances are recorded nowhere else, so they cannot be reconstructed afterwards.
/// The GitHub token moved with the same folder, and losing it forces a fresh device-flow
/// sign-in while leaving a live token behind on disk.
///
/// This is a one-time, best-effort step per file: the legacy copy is moved rather than
/// copied, so it neither runs again nor leaves a stale token readable. A failure leaves the
/// app on a blank state, which is what would have happened anyway.
/// </summary>
public static class LegacyPaths
{
    /// <summary>The pre-rename per-user folder, alongside the current one.</summary>
    public static string LocalAppDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TokenBurnRate");

    /// <summary>
    /// Moves <paramref name="legacy"/> to <paramref name="current"/> when the new location
    /// has no file yet. An existing file at the destination always wins - it is the newer
    /// state, and a half-migrated install must never be overwritten by the stale copy.
    /// </summary>
    public static void Adopt(string current, string legacy)
    {
        try
        {
            if (File.Exists(current) || !File.Exists(legacy)) return;

            var dir = Path.GetDirectoryName(current);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            File.Move(legacy, current);
        }
        catch (Exception)
        {
            // Best effort: a locked or unreadable legacy file costs the carried-over
            // settings, never the running app.
        }
    }
}
