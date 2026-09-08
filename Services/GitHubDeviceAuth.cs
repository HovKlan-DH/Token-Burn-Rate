using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TokenBurnRate.Services;

/// <summary>
/// Signs in to GitHub with the OAuth device flow, so the widget works on a machine that
/// has no GitHub CLI installed.
///
/// The device flow is the standard mechanism for a desktop app with no browser redirect:
/// the app asks GitHub for a short user code, the user enters it once at
/// github.com/login/device, and GitHub hands back a token. It needs no client secret and
/// no admin rights, which matters on a locked-down work machine.
///
/// VS Code's own Copilot session is deliberately not reused: it is encrypted with the OS
/// credential store (DPAPI on Windows), and prising open another application's secrets
/// would be both fragile and inappropriate.
/// </summary>
public sealed class GitHubDeviceAuth
{
    // The GitHub CLI's public client id. Device-flow client ids are not secrets - they
    // identify the app, and the user still has to approve the grant interactively.
    private const string ClientId = "178c6fc778ccc68e1d6a";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";

    // read:user is the least privilege that still lets copilot_internal/user answer.
    private const string Scope = "read:user";

    private readonly HttpClient _http;

    public GitHubDeviceAuth(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("TokenBurnRate/1.0"))
        {
            // A shared client may already carry a user agent; that is fine.
        }
    }

    public sealed record DeviceCode(string DeviceCode_, string UserCode, string VerificationUri, int Interval, int ExpiresIn);

    /// <summary>Step one: ask GitHub for a code the user types into the browser.</summary>
    public async Task<DeviceCode> RequestCodeAsync(CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl) { Content = content };
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new DeviceCode(
            root.GetProperty("device_code").GetString() ?? "",
            root.GetProperty("user_code").GetString() ?? "",
            root.GetProperty("verification_uri").GetString() ?? "https://github.com/login/device",
            root.TryGetProperty("interval", out var i) ? i.GetInt32() : 5,
            root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 900);
    }

    /// <summary>
    /// Step two: poll until the user approves, or the code expires. Returns the access
    /// token, or null if the attempt expired or was denied.
    /// </summary>
    public async Task<string?> PollForTokenAsync(DeviceCode code, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(1, code.Interval));
        var transientFailures = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = code.DeviceCode_,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = content };
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var tok))
                return tok.GetString();

            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            switch (error)
            {
                case "authorization_pending":
                    transientFailures = 0;
                    continue;                       // user has not finished yet
                case "slow_down":
                    // GitHub rejects polling that is too eager; back off as instructed.
                    transientFailures = 0;
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "expired_token":
                case "access_denied":
                case "unsupported_grant_type":
                case "incorrect_client_credentials":
                    return null;                    // unrecoverable, stop asking
                default:
                    // Anything unrecognised may be transient (a blip, a proxy, an
                    // undocumented code). Retry a few times before giving up, so a single
                    // odd response does not abandon a sign-in the user is midway through.
                    if (++transientFailures > 5) return null;
                    continue;
            }
        }
        return null;
    }

    // ---- token storage -----------------------------------------------------------------

    private static string TokenPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TokenBurnRate");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "github.json");
        }
    }

    public static string? LoadToken()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(TokenPath));
            var t = doc.RootElement.TryGetProperty("access_token", out var v) ? v.GetString() : null;
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void SaveToken(string token)
    {
        var json = JsonSerializer.Serialize(new { access_token = token });
        File.WriteAllText(TokenPath, json);
        RestrictToCurrentUser(TokenPath);
    }

    public static void ClearToken()
    {
        try { if (File.Exists(TokenPath)) File.Delete(TokenPath); }
        catch (Exception) { }
    }

    /// <summary>
    /// Strips inherited permissions so only the current user can read the stored token.
    /// Windows-only; on Unix the file is created with the process umask.
    /// </summary>
    private static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (user is not null)
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    user,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
            }
            info.SetAccessControl(security);
        }
        catch (Exception)
        {
            // Best effort: the token still lives under the per-user AppData profile.
        }
    }
}
