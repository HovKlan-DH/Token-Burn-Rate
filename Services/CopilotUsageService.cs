using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Token_Burn_Rate.Models;

namespace Token_Burn_Rate.Services;

/// <summary>
/// Reads live Copilot quota from GitHub's copilot_internal/user endpoint.
///
/// Unlike Claude, nothing on disk records Copilot history: the endpoint returns a
/// point-in-time snapshot of monthly quota. That is why Copilot is presented as its three
/// real quota buckets (completions / chat / premium interactions) rather than
/// today/week/month, which cannot be derived.
///
/// The token is taken from the GitHub CLI, which is already authenticated on a developer
/// machine, so the app needs no credentials of its own.
/// </summary>
public sealed class CopilotUsageService
{
    private const string Endpoint = "https://api.github.com/copilot_internal/user";
    private readonly HttpClient _http;
    private string? _cachedToken;

    public CopilotUsageService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TokenBurnRate/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<CopilotStatus> GetStatusAsync(CancellationToken ct = default)
    {
        string? token;
        try
        {
            token = await GetTokenAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new CopilotStatus { Error = "gh CLI unavailable: " + ex.Message };
        }

        if (string.IsNullOrWhiteSpace(token))
            return new CopilotStatus { Error = "Not signed in. Run: gh auth login" };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("token", token);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _cachedToken = null; // force a refresh next time
                return new CopilotStatus { Error = $"GitHub returned {(int)resp.StatusCode}" };
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CopilotStatus { Error = ex.Message };
        }
    }

    internal static CopilotStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var orgs = new List<string>();
        if (root.TryGetProperty("organization_login_list", out var orgEl)
            && orgEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in orgEl.EnumerateArray())
                if (o.GetString() is { } s) orgs.Add(s);
        }

        DateTimeOffset? reset = null;
        if (root.TryGetProperty("quota_reset_date_utc", out var rEl)
            && DateTimeOffset.TryParse(rEl.GetString(), out var parsed))
            reset = parsed;

        var status = new CopilotStatus
        {
            Plan = root.TryGetProperty("copilot_plan", out var p) ? p.GetString() ?? "unknown" : "unknown",
            Sku = root.TryGetProperty("access_type_sku", out var s2) ? s2.GetString() ?? "" : "",
            Organizations = orgs,
            ResetDate = reset,
        };

        if (root.TryGetProperty("quota_snapshots", out var snaps))
        {
            AddQuota(status, snaps, "completions", "COMPLETIONS");
            AddQuota(status, snaps, "chat", "CHAT");
            AddQuota(status, snaps, "premium_interactions", "PREMIUM");
        }

        return status;
    }

    private static void AddQuota(CopilotStatus status, JsonElement snaps, string key, string label)
    {
        if (!snaps.TryGetProperty(key, out var q)) return;

        status.Quotas.Add(new CopilotQuota
        {
            Label = label,
            Entitlement = Dbl(q, "entitlement"),
            Remaining = Dbl(q, "quota_remaining"),
            CreditsUsed = Dbl(q, "credits_used"),
            Unlimited = q.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True,
            HasQuota = q.TryGetProperty("has_quota", out var h) && h.ValueKind == JsonValueKind.True,
        });
    }

    private static double Dbl(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedToken)) return _cachedToken;

        // Explicit env vars win, matching gh's own precedence.
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v)) return _cachedToken = v.Trim();
        }

        _cachedToken = await RunGhAuthTokenAsync(ct).ConfigureAwait(false);
        return _cachedToken;
    }

    private static async Task<string?> RunGhAuthTokenAsync(CancellationToken ct)
    {
        var exe = OperatingSystem.IsWindows() ? "gh.exe" : "gh";
        var psi = new ProcessStartInfo(exe, "auth token")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode != 0) return null;
            var token = stdout.Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // gh not on PATH
        }
    }
}
