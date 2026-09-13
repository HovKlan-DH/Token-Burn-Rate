using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TokenBurnRate.Services;

/// <summary>
/// A plain-text log the user can send in for debugging, covering both accounts (Claude and
/// GitHub Copilot) plus whatever errors either one hits.
///
/// One file per process run, named <c>{exename}.{yyyyMMddHHmmss}-{pid}.log</c> - the same
/// shape <see cref="CrashLog"/> uses for its own <c>{exename}.crash.{timestamp}.log</c>
/// files, so anyone looking at the folder recognises both as the same family of report. A
/// fresh timestamp per run also means a log can never straddle two sessions, which matters
/// once old ones are pruned by age (see <see cref="PruneOldLogs"/>): a long-lived single
/// file would keep getting its clock reset by its own most recent line and never qualify
/// for deletion.
///
/// The pid suffix is not decoration. A silent auto-update restarts the app
/// (see Services/UpdateService.cs), and the outgoing and incoming processes routinely
/// overlap inside the same second - without it both would resolve the same name and the new
/// one's banner write would truncate whatever the old one had logged, losing exactly the
/// window a restart report needs.
///
/// Deliberately separate from <see cref="CrashLog"/>: that one exists to catch an unhandled
/// exception on its way down, and fires rarely by design. This one records ordinary
/// operation - each poll's outcome, sign-in/sign-out, recoverable failures - so a report of
/// "the Claude panel went blank yesterday" has something to look at even though nothing
/// crashed.
///
/// Lives beside the state file - same folder <see cref="AppState.Path"/> resolves to, which
/// already handles a read-only program folder and a Velopack install - so both settle in one
/// place together.
///
/// Every entry point is safe to call from anywhere, <see cref="FilePath"/> included: a
/// logging failure must never surface to the caller or take down the poll it was describing.
/// </summary>
public static class AppLog
{
    /// <summary>
    /// Logs from more than this long ago are deleted the first time this process writes a
    /// log line. 48 hours keeps "yesterday's" report available for exactly the case this log
    /// exists for, without letting one-file-per-run history pile up indefinitely.
    /// </summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(48);

    /// <summary>
    /// Ceiling on a single run's file. Pruning is by age and runs only at startup, so on a
    /// tray widget deliberately left running for weeks - with auto-update off, nothing ever
    /// restarts it - age alone would never bound the file this process is still appending
    /// to. Past this, writes stop and one final line says so, rather than silently filling
    /// a disk or handing the user a log too large to attach.
    /// </summary>
    private const long MaxBytes = 16 * 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _path;
    private static long _written;
    private static bool _capped;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>Records an error with its exception's message and type - never the full
    /// stack trace, which belongs in <see cref="CrashLog"/> for an actual unhandled failure.</summary>
    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>
    /// Logs only when <paramref name="message"/> differs from the last one logged under the
    /// same <paramref name="key"/> - for conditions that are re-evaluated every poll but
    /// change rarely, like which token source answered or which plan came back.
    ///
    /// Without this, a steady state writes the same line every cadence tick: 480 a day at the
    /// default 180s interval, 17,280 at the 5s floor the state file allows, each one a
    /// blocking disk write on the UI thread (the services are awaited with
    /// ConfigureAwait(true) from a dispatcher timer). Logging the transition instead keeps
    /// the file readable and the widget smooth, and a transition is the only part that
    /// carries information anyway.
    /// </summary>
    public static void Change(string key, string message)
    {
        lock (Gate)
        {
            if (_lastByKey.TryGetValue(key, out var previous) && previous == message) return;
            _lastByKey[key] = message;
        }

        Write("INFO", message);
    }

    private static readonly Dictionary<string, string> _lastByKey = new(StringComparer.Ordinal);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (_capped) return;

                var path = _path ??= StartNewFile();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";

                if (_written + line.Length > MaxBytes)
                {
                    _capped = true;
                    File.AppendAllText(
                        path,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [WARN] log reached {MaxBytes / (1024 * 1024)} MB - "
                        + $"no further entries will be written until the app restarts{Environment.NewLine}",
                        new UTF8Encoding(false));
                    return;
                }

                File.AppendAllText(path, line, new UTF8Encoding(false));
                _written += line.Length;
            }
        }
        catch (Exception)
        {
            // Logging must never cause the failure it was trying to record. If the file
            // cannot be written there is nowhere left to report that, so it is dropped.
        }
    }

    /// <summary>
    /// Picks this run's file name, prunes anything older than <see cref="MaxAge"/>, and
    /// writes the opening banner - all once per process, the first time anything is logged.
    /// </summary>
    private static string StartNewFile()
    {
        var dir = Path.GetDirectoryName(AppState.Path);
        var fileName = $"{BaseName}.{DateTime.Now:yyyyMMddHHmmss}-{Environment.ProcessId}.log";
        var path = string.IsNullOrWhiteSpace(dir) ? fileName : Path.Combine(dir, fileName);

        if (!string.IsNullOrWhiteSpace(dir)) PruneOldLogs(dir);
        WriteBanner(path);
        return path;
    }

    /// <summary>
    /// The executable's own name, which is what <see cref="CrashLog"/> builds its file names
    /// from too. Derived rather than hardcoded so the two stay in lockstep if the exe is ever
    /// renamed - otherwise this class's prune glob would stop matching CrashLog's output and
    /// the ".crash." exclusion below would be protecting nothing.
    /// </summary>
    private static string BaseName
    {
        get
        {
            var exe = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(exe)
                ? "Token-Burn-Rate"
                : Path.GetFileNameWithoutExtension(exe);
        }
    }

    /// <summary>
    /// Deletes this app's own log files older than <see cref="MaxAge"/>.
    ///
    /// The glob alone would also match <see cref="CrashLog"/>'s
    /// "{exename}.crash.{timestamp}.log" files - the "*" in a filesystem glob happily spans
    /// the ".crash." segment too - so those are filtered back out explicitly. A crash report
    /// is worth keeping longer than routine poll activity, and pruning it out from under a
    /// user who just crashed and is about to be asked for it would be exactly wrong.
    ///
    /// Times are compared in UTC. Local wall-clock repeats an hour every autumn and jumps
    /// whenever a machine with a dead CMOS battery finally reaches an NTP server, either of
    /// which can make a file written minutes ago look two days old.
    /// </summary>
    private static void PruneOldLogs(string dir)
    {
        try
        {
            var cutoff = DateTime.UtcNow - MaxAge;
            foreach (var file in Directory.EnumerateFiles(dir, $"{BaseName}.*.log"))
            {
                if (Path.GetFileName(file).Contains(".crash.", StringComparison.Ordinal)) continue;

                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (Exception)
                {
                    // One locked or already-gone file must not stop the rest from being swept.
                }
            }
        }
        catch (Exception)
        {
            // Directory listing failed - nothing to prune, and the log itself still works.
        }
    }

    /// <summary>
    /// The banner identifying the build and machine, the same facts <see cref="CrashLog"/>
    /// stamps on a crash report - so a log sent in stands on its own without a separate
    /// "what version were you running" round trip.
    /// </summary>
    private static void WriteBanner(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Token Burn Rate started {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
        sb.AppendLine($"Version : {typeof(AppLog).Assembly.GetName().Version}");
        sb.AppendLine($"OS      : {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")})");
        sb.AppendLine($"Runtime : {Environment.Version}");
        sb.AppendLine("==========================================================");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// The file path this run is writing to, so the context menu can offer to open it.
    ///
    /// Never throws. Creating the file is real I/O that can fail on a read-only folder or a
    /// share that went away, and this is reached from a menu click handler - letting it throw
    /// would turn "show me the log" into an unhandled exception on the UI thread and take the
    /// widget down over the very failure the log exists to diagnose. Returns null when there
    /// is nothing openable, and the caller simply does nothing.
    /// </summary>
    public static string? FilePath
    {
        get
        {
            try
            {
                lock (Gate)
                {
                    return _path ??= StartNewFile();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
