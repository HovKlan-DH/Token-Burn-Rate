using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TokenBurnRate.Models;

namespace TokenBurnRate.Services;

/// <summary>
/// Reads the authoritative plan limits that Claude.ai itself displays.
///
/// This is the same data behind the Usage screen: a utilization percentage and a real
/// reset timestamp per limit. It supersedes deriving percentages from transcripts, which
/// could only ever guess at a denominator Anthropic does not publish, and which measured
/// rolling lookbacks rather than the fixed reset windows the limits actually use.
///
/// The OAuth access token is read from Claude Code's credential file and never written
/// back: Claude Code owns that file and refreshes the token itself, roughly every eight
/// hours. If the token is expired or missing we report that rather than attempting a
/// refresh, so there is no chance of corrupting the credentials of a running CLI.
/// </summary>
public sealed class ClaudeLimitsService
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private readonly HttpClient _http;

    public ClaudeLimitsService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Token-Burn-Rate/1.0");
    }

    public static string CredentialsPath
    {
        get
        {
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(configDir))
                return Path.Combine(configDir, ".credentials.json");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".claude", ".credentials.json");
        }
    }

    public async Task<ClaudeLimitsStatus> GetLimitsAsync(CancellationToken ct = default)
    {
        string token;
        string? plan;
        try
        {
            var cred = ReadCredentials();
            if (cred is null)
                return ClaudeLimitsStatus.Unavailable("not signed in to Claude Code");
            if (cred.Value.Expired)
                return ClaudeLimitsStatus.Unavailable("session expired - run claude to refresh");

            token = cred.Value.AccessToken;
            plan = cred.Value.SubscriptionType;
        }
        catch (Exception ex)
        {
            // A file that failed to read - as opposed to one that is absent or well-formed
            // but signed out - is as likely to be Claude Code mid-rewrite as it is to be
            // genuinely corrupt, so it is worth the same fast retry as a network hiccup.
            return ClaudeLimitsStatus.Unavailable("credentials unreadable: " + ex.Message, transient: true);
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return ClaudeLimitsStatus.Unavailable("session expired - run claude to refresh");
            if (!resp.IsSuccessStatusCode)
                return ClaudeLimitsStatus.Unavailable($"usage API returned {(int)resp.StatusCode}", transient: true);

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = Parse(json);
            status.Plan = plan ?? status.Plan;
            return status;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Network unreachable, DNS not resolved yet, TLS handshake failed, timed out -
            // the boot-time case this whole flag exists for: the connection itself never
            // completed, which a login could not have prevented and a few seconds usually
            // fixes on its own.
            return ClaudeLimitsStatus.Unavailable(ex.Message, transient: true);
        }
    }

    internal static ClaudeLimitsStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = new ClaudeLimitsStatus();

        // Prefer the "limits" array: it enumerates whatever limits currently apply, so new
        // limit kinds appear without a code change. Fall back to the flat fields if absent.
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in limits.EnumerateArray())
            {
                var kind = Str(l, "kind");
                if (kind is null) continue;

                status.Limits.Add(new ClaudeLimit
                {
                    Kind = kind,
                    Label = LabelFor(kind),
                    Percent = Dbl(l, "percent"),
                    ResetsAt = Time(l, "resets_at"),
                    IsActive = l.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True,
                });
            }
        }

        if (status.Limits.Count == 0)
        {
            AddFlat(status, root, "five_hour", "SESSION");
            AddFlat(status, root, "seven_day", "WEEK");
        }

        return status;
    }

    private static void AddFlat(ClaudeLimitsStatus status, JsonElement root, string key, string label)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object) return;
        if (!el.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) return;

        status.Limits.Add(new ClaudeLimit
        {
            Kind = key,
            Label = label,
            Percent = u.GetDouble(),
            ResetsAt = Time(el, "resets_at"),
            IsActive = true,
        });
    }

    /// <summary>Maps API limit kinds to short display labels, passing unknown kinds through.</summary>
    private static string LabelFor(string kind) => kind switch
    {
        "session" or "five_hour" => "SESSION",
        "weekly_all" or "seven_day" => "WEEK",
        "weekly_opus" or "seven_day_opus" => "WEEK - OPUS",
        "weekly_sonnet" or "seven_day_sonnet" => "WEEK - SONNET",
        _ => kind.Replace('_', ' ').ToUpperInvariant(),
    };

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Dbl(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static DateTimeOffset? Time(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(v.GetString(), out var t) ? t : null;

    private readonly record struct Credentials(string AccessToken, string? SubscriptionType, bool Expired);

    private static Credentials? ReadCredentials()
    {
        var path = CredentialsPath;
        if (!File.Exists(path)) return null;

        // Claude Code rewrites this file on refresh, so tolerate a concurrent write.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(fs);

        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
        var token = Str(oauth, "accessToken");
        if (string.IsNullOrWhiteSpace(token)) return null;

        var expired = false;
        if (oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number)
        {
            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64());
            // Small skew allowance; the request would fail anyway and we surface the same message.
            expired = expiresAt <= DateTimeOffset.UtcNow.AddSeconds(30);
        }

        return new Credentials(token, Str(oauth, "subscriptionType"), expired);
    }
}
