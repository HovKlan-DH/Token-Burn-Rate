using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TokenBurnRate.Services;

/// <summary>
/// Writes unhandled exceptions to a file beside the executable, named
/// <c>{exename}.crash.{yyyyMMddHHmmss}.log</c>, so a crash can be sent on afterwards
/// rather than vanishing with the process.
///
/// One file per crash: a timestamped name never collides, keeps the reports in order, and
/// avoids the append-to-a-growing-file problem where the interesting one is buried.
///
/// Note on StackOverflowException: the CLR terminates the process immediately and runs no
/// handler, by design, so that one crash cannot be logged from inside the app. Every other
/// unhandled exception - including those on background threads and in unobserved tasks -
/// reaches one of the hooks installed by <see cref="Install"/>.
/// </summary>
public static class CrashLog
{
    private static int _written;

    /// <summary>
    /// Subscribes to every source of unhandled exceptions the runtime offers. Call once,
    /// as early as possible - before the UI exists, so a failure during startup is caught
    /// too.
    /// </summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.ExceptionObject as Exception, e.IsTerminating ? "unhandled" : "unhandled (non-fatal)");

        // A faulted Task nobody awaited. Marked observed afterwards so the default policy
        // does not then tear the process down over an exception already recorded.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception, "unobserved task");
            e.SetObserved();
        };

        // Not hooked: AppDomain.CurrentDomain.FirstChanceException. It is the only hook that
        // sees an exception the UI framework catches and swallows, but it fires for every
        // exception in the process - including the many thrown by design here, from
        // AppState's writability probe to every ConvertBack - so a handler would be invoked
        // constantly to do nothing. Add it here temporarily, filtered, if a specific
        // swallowed exception ever needs chasing.
    }

    /// <summary>
    /// Records an exception explicitly - for a catch block that handles a failure but still
    /// wants it on record.
    /// </summary>
    public static void Record(Exception? ex, string context) => Write(ex, context);

    private static void Write(Exception? ex, string context)
    {
        if (ex is null) return;

        // A crash often cascades: the first exception is the useful one, and writing a file
        // per follow-up would bury it. Cap at a handful per run.
        if (Interlocked.Increment(ref _written) > 5) return;

        try
        {
            var path = BuildPath();
            File.WriteAllText(path, Compose(ex, context), new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // Logging a crash must never cause one. If the file cannot be written there is
            // nowhere left to report that, so it is dropped.
        }
    }

    /// <summary>
    /// <c>{exename}.crash.{yyyyMMddHHmmss}.log</c> beside the executable, falling back to
    /// the same %LOCALAPPDATA% folder the state file uses when that directory is read-only.
    /// </summary>
    private static string BuildPath()
    {
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var exe = Environment.ProcessPath;
        var name = string.IsNullOrWhiteSpace(exe)
            ? "Token-Burn-Rate"
            : Path.GetFileNameWithoutExtension(exe);

        var fileName = $"{name}.crash.{stamp}.log";

        // Sit beside the state file: that resolution already handles a read-only program
        // folder, so both land in the same place and are found together.
        var dir = Path.GetDirectoryName(AppState.Path);
        return string.IsNullOrWhiteSpace(dir) ? fileName : Path.Combine(dir, fileName);
    }

    private static string Compose(Exception ex, string context)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Token Burn Rate crash report");
        sb.AppendLine("==========================");
        sb.AppendLine($"When      : {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local) / {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} (UTC)");
        sb.AppendLine($"Context   : {context}");
        sb.AppendLine($"Version   : {typeof(CrashLog).Assembly.GetName().Version}");
        sb.AppendLine($"Executable: {Environment.ProcessPath}");
        sb.AppendLine($"OS        : {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")})");
        sb.AppendLine($"Runtime   : {Environment.Version}");
        sb.AppendLine($"CLR       : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine();

        // The whole chain: an AggregateException or a wrapped failure keeps its real cause
        // in InnerException, which is usually the part worth reading.
        var depth = 0;
        for (var current = ex; current is not null; current = current.InnerException, depth++)
        {
            sb.AppendLine(depth == 0 ? "Exception" : $"Inner exception ({depth})");
            sb.AppendLine("--------------------------");
            sb.AppendLine($"Type   : {current.GetType().FullName}");
            sb.AppendLine($"Message: {current.Message}");
            sb.AppendLine($"HResult: 0x{current.HResult:X8}");
            if (current.TargetSite is { } site)
                sb.AppendLine($"Site   : {site.DeclaringType?.FullName}.{site.Name}");
            sb.AppendLine("Stack  :");
            sb.AppendLine(current.StackTrace ?? "  (no stack trace)");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
