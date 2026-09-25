using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TokenBurnRate.Services;

/// <summary>
/// Pings the project's own backend once per launch, purely so the maintainer can see how
/// many machines are running which version - nothing here reads or reports usage data.
///
/// The endpoint (mailscan.dk/app-checkin) is intentionally undocumented in the UI: it is
/// not a telemetry opt-in dialog, just a background POST that mirrors what the PHP side
/// already expects (see its source for the exact contract). Failures - offline, DNS,
/// timeout, server error - are swallowed, since a check-in must never delay startup or
/// surface an error for something the user cannot act on. One that failed before anything
/// was sent is retried by the window's <see cref="CheckSchedule"/> schedule.
/// </summary>
public static class CheckInService
{
    private const string Endpoint = "https://mailscan.dk/app-checkin/";

    /// <summary>
    /// Limit on the TCP connect alone, kept well inside <see cref="RequestTimeout"/> so it
    /// always fires first. A connect that hangs - traffic black-holed while a VPN is still
    /// coming up - then fails as a ConnectionError, which is known to have sent nothing and
    /// is safe to retry. HttpClient's own ConnectTimeout is not used: it fails as a plain
    /// TaskCanceledException, indistinguishable from the request timeout except by its
    /// (localised) message.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    /// <summary>The whole request. Expiring after the connect succeeded is the one ambiguous
    /// case - the post may have been recorded - and is not retried.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The server keys the "is this our app" check off a User-Agent starting "TBR " and
    /// matching its own whitelist regex (<c>^[a-zA-Z0-9 ,.#()*\[\]!:/-]+$</c>), so the
    /// version string must stick to that set.
    ///
    /// "TBR" is the server's whitelisted string, not an abbreviation of the app name that
    /// follows a rename: changing it here requires the same change on the PHP side, or
    /// check-ins are rejected and - since the response status is discarded - silently lost.
    /// The same string is sent again as the "control" field below.
    /// </summary>
    private static string UserAgent =>
        $"TBR {AppVersion}";

    private static string AppVersion =>
        typeof(CheckInService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Windows, macOS or Linux - the PHP side stores this as a coarse OS bucket.</summary>
    private static string OsHighLevel =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsMacOS() ? "macOS" :
        OperatingSystem.IsLinux() ? "Linux" : "Other";

    private static string OsVersion => RuntimeInformation.OSDescription;

    /// <summary>
    /// Posts the check-in and reports whether the attempt is settled: true for a successful
    /// post or a failure not worth retrying, false when it failed before anything was sent
    /// (see <see cref="CheckSchedule.FailedBeforeSending"/>) - the network simply is not up
    /// yet, and the caller should try again rather than write this launch off. Never throws.
    ///
    /// <paramref name="shutdown"/> is the app's shutdown token: cancelling it abandons an
    /// in-flight post and reports the attempt settled, so an exit does not look like a
    /// network failure worth retrying into a closing process.
    /// </summary>
    public static async Task<bool> PingHomeAsync(CancellationToken shutdown)
    {
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = ConnectAsync })
            {
                Timeout = RequestTimeout,
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["osHighlevel"] = OsHighLevel,
                ["osVersion"] = OsVersion,
                ["control"] = "TBR",
            });

            using var resp = await http.PostAsync(Endpoint, content, shutdown).ConfigureAwait(false);
            AppLog.Info($"Check-in: posted, server returned {(int)resp.StatusCode}");
            return true;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // The user exited while the post was in flight. Not a connectivity problem, so it
            // is not logged as one, and it is reported settled rather than arming a retry
            // against a process that is going away.
            return true;
        }
        catch (Exception ex)
        {
            // Offline, DNS failure, server hiccup - none of it should affect the widget.
            // Logged anyway: it is the earliest outbound request the app makes, so a failure
            // here is usually the first sign that this machine has no connectivity at all,
            // which is worth seeing above a run of Claude/Copilot timeouts.
            AppLog.Warn($"Check-in: failed - {ex.GetType().Name}: {ex.Message}");

            // Only a failure before anything was sent (no route yet, DNS not resolving, the
            // connect hanging) is retried - exactly what a VPN client still spinning up
            // produces. A server error would fail identically on retry, and a timeout after
            // connecting may already have been recorded, so a retry would count this launch
            // twice.
            return !CheckSchedule.FailedBeforeSending(ex);
        }
    }

    /// <summary>
    /// Opens the TCP connection under <see cref="ConnectTimeout"/>, reporting expiry as a
    /// timed-out socket - which the handler surfaces as a ConnectionError, the "nothing was
    /// sent" shape CheckSchedule.FailedBeforeSending recognises.
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context,
                                                        CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ConnectTimeout);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, limit.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new SocketException((int)SocketError.TimedOut);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
