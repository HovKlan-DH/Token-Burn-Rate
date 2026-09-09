using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TokenBurnRate.Services;

/// <summary>
/// Pings the project's own backend once per launch, purely so the maintainer can see how
/// many machines are running which version - nothing here reads or reports usage data.
///
/// The endpoint (mailscan.dk/app-checkin) is intentionally undocumented in the UI: it is
/// not a telemetry opt-in dialog, just a fire-and-forget POST that mirrors what the PHP
/// side already expects (see its source for the exact contract). Failures - offline, DNS,
/// timeout, server error - are swallowed, since a check-in must never delay startup or
/// surface an error for something the user cannot act on.
/// </summary>
public static class CheckInService
{
    private const string Endpoint = "https://mailscan.dk/app-checkin/";

    /// <summary>
    /// The server keys the "is this our app" check off a User-Agent containing
    /// "TokenBurnRate " and matching its own whitelist regex
    /// (<c>^[a-zA-Z0-9 ,.#()*\[\]!:/-]+$</c>), so the version string must stick to that set.
    /// </summary>
    private static string UserAgent =>
        $"TokenBurnRate {AppVersion}";

    private static string AppVersion =>
        typeof(CheckInService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Windows, macOS or Linux - the PHP side stores this as a coarse OS bucket.</summary>
    private static string OsHighLevel =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsMacOS() ? "macOS" :
        OperatingSystem.IsLinux() ? "Linux" : "Other";

    private static string OsVersion => RuntimeInformation.OSDescription;

    /// <summary>
    /// Fires the check-in in the background. Callers should not await this on the UI
    /// thread's startup path; call it and move on.
    /// </summary>
    public static void PingHome()
    {
        _ = PingHomeAsync();
    }

    private static async Task PingHomeAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["osHighlevel"] = OsHighLevel,
                ["osVersion"] = OsVersion,
                ["control"] = "TokenBurnRate",
            });

            using var resp = await http.PostAsync(Endpoint, content).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Offline, DNS failure, server hiccup - none of it should affect the widget.
        }
    }
}
