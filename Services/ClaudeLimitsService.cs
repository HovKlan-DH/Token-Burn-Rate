using System;
using System.Collections.Generic;
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
/// The token comes from this app's own Claude sign-in (<see cref="ClaudeOAuth"/>) and from
/// nowhere else. The widget is its own OAuth client: it signs in once per machine and
/// refreshes the token itself from then on, so it works whether or not the `claude` CLI is
/// installed, and there is a single path to reason about rather than two.
///
/// It deliberately no longer reads Claude Code's credentials file. Borrowing that token
/// only ever worked on a machine where the CLI was installed, signed in, and used often
/// enough to keep it fresh - the widget could not refresh it, because Claude Code owns that
/// file and writing to it risked corrupting a running CLI's credentials. Signing in here
/// removes that dependency entirely.
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

    public async Task<ClaudeLimitsStatus> GetLimitsAsync(CancellationToken ct = default)
    {
        var (tokens, refreshFailed) = await ResolveTokenAsync(ct).ConfigureAwait(false);
        if (tokens is null)
        {
            // Signed in, but the refresh could not be completed - the network is down, not
            // the grant. Offering a sign-in button here would tell an offline user to
            // re-authenticate over the connection they do not have, so this reports a
            // transient failure and the next poll tries again.
            if (refreshFailed)
            {
                AppLog.Warn("Claude: token refresh unreachable - reporting transient failure, stored sign-in kept");
                return ClaudeLimitsStatus.Unavailable("Claude unreachable", transient: true);
            }

            // No sign-in on this machine yet, or the grant was refused outright. Either way
            // the fix is the same and the panel offers it.
            AppLog.Change("claude.state", "Claude: not signed in - offering sign-in");
            return ClaudeLimitsStatus.Unavailable("not signed in", canSignIn: true);
        }

        // Two attempts at most: the second only ever happens after a 401 forced a refresh,
        // so a token that has just been renewed still gets to answer the question before the
        // panel reports a failure.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
                req.Headers.Add("anthropic-beta", "oauth-2025-04-20");

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    // A 401 here does not prove the grant is dead, so the tokens are not
                    // thrown away on the strength of it. The access token may simply have
                    // aged out between the expiry check and this request, and a 401 can
                    // equally mean the usage endpoint stopped accepting this token's scope
                    // or the beta header - neither of which a new sign-in would fix.
                    // Clearing on sight turned that into a loop: wipe, sign in, get the same
                    // 401, wipe again, for as long as the user kept trying.
                    //
                    // So spend exactly one forced refresh on it. If that is refused,
                    // RefreshAsync has already cleared the store and the grant really is
                    // gone. If it succeeds and the retry still 401s, the token was never the
                    // problem, and that is reported as transient rather than as a sign-out.
                    if (attempt > 0)
                    {
                        AppLog.Warn("Claude: usage API still 401 after a forced refresh - reporting transient, not signing out");
                        return ClaudeLimitsStatus.Unavailable("Claude rejected the session", transient: true);
                    }

                    AppLog.Info("Claude: usage API returned 401 - forcing one token refresh before giving up");
                    var (renewed, renewFailed) = await ForceRefreshAsync(ct).ConfigureAwait(false);
                    if (renewed is null)
                    {
                        // Could not reach the endpoint to find out: the grant is untouched
                        // and still on disk, so this is a blip, not a sign-out.
                        if (renewFailed)
                        {
                            AppLog.Warn("Claude: forced refresh unreachable after a 401 - reporting transient failure");
                            return ClaudeLimitsStatus.Unavailable("Claude unreachable", transient: true);
                        }

                        AppLog.Warn("Claude: forced refresh was refused - grant is dead, offering sign-in");
                        return ClaudeLimitsStatus.Unavailable("Sign-in expired", canSignIn: true);
                    }

                    tokens = renewed;
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    AppLog.Warn($"Claude: usage API returned {(int)resp.StatusCode}");
                    return ClaudeLimitsStatus.Unavailable($"usage API returned {(int)resp.StatusCode}", transient: true);
                }

                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var status = Parse(json);
                status.Plan = tokens.SubscriptionType ?? status.Plan;

                // Change-only, and deliberately without the plan name. The plan never varies
                // between polls, so repeating it every cadence tick is pure noise - and the
                // user may well send this file in, where an account tier (on the work
                // machine, an org-assigned seat) is more than a bug report needs.
                AppLog.Change("claude.state", $"Claude: poll ok - {status.Limits.Count} limit(s) reported");
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
                AppLog.Error("Claude: usage poll failed", ex);
                return ClaudeLimitsStatus.Unavailable(ex.Message, transient: true);
            }
        }
    }

    /// <summary>
    /// Refreshes regardless of the stored expiry, for the one case the expiry cannot speak
    /// to: the server rejected an access token this app still believed was valid.
    ///
    /// Shares <see cref="ResolveTokenAsync"/>'s gate, because it spends the same rotating
    /// refresh token and two concurrent refreshes would retire each other's.
    /// </summary>
    private async Task<(ClaudeTokens? Tokens, bool RefreshFailed)> ForceRefreshAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stored = ClaudeTokenStore.Load();
            if (stored is null) return (null, false);

            var renewed = await (_oauth ??= new ClaudeOAuth())
                .RefreshAsync(stored.RefreshToken, ct).ConfigureAwait(false);
            return (renewed, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Could not reach the endpoint. The tokens stay put - see ResolveTokenAsync -
            // and the caller reports this as a failure to renew rather than a sign-out.
            AppLog.Warn($"Claude: forced token refresh transport failure - {ex.GetType().Name}: {ex.Message}");
            return (null, true);
        }
        finally
        {
            _refreshGate.Release();
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
            // Settled before any row is named, since the all-models week reads "Week" or
            // "Week (Total)" depending on rows that may come after it in the array.
            var split = false;
            foreach (var l in limits.EnumerateArray())
                if (Str(l, "kind") is { } k && ClaudeLimitKind.IsPerModelWeek(k)) split = true;

            foreach (var l in limits.EnumerateArray())
            {
                var kind = Str(l, "kind");
                if (string.IsNullOrEmpty(kind)) continue;

                status.Limits.Add(new ClaudeLimit
                {
                    Kind = kind,
                    Name = NameFor(kind, ScopeName(l), split),
                    Percent = Dbl(l, "percent"),
                    ResetsAt = Time(l, "resets_at"),
                    IsActive = l.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True,
                });
            }

            if (split) PutPerModelWeeksAboveTotal(status.Limits);
        }

        if (status.Limits.Count == 0)
        {
            AddFlat(status, root, "five_hour", "Session");
            AddFlat(status, root, "seven_day", "Week");
        }

        return status;
    }

    /// <summary>
    /// Moves the per-model weeks to just above the all-models week. The server lists the
    /// total first (claude.ai shows "This week" above "Fable this week"), but here the total
    /// reads as the last line of the week, under the pools it sums, the way a total does.
    /// Several per-model rows keep the server's order among themselves.
    /// </summary>
    private static void PutPerModelWeeksAboveTotal(List<ClaudeLimit> limits)
    {
        if (limits.FindIndex(IsTotal) < 0) return;      // nothing to sit above

        var perModel = limits.FindAll(IsPerModel);
        limits.RemoveAll(IsPerModel);
        limits.InsertRange(limits.FindIndex(IsTotal), perModel);

        static bool IsTotal(ClaudeLimit l) => ClaudeLimitKind.IsTotalWeek(l.Kind);
        static bool IsPerModel(ClaudeLimit l) => ClaudeLimitKind.IsPerModelWeek(l.Kind);
    }

    private static void AddFlat(ClaudeLimitsStatus status, JsonElement root, string key, string name)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object) return;
        if (!el.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) return;

        status.Limits.Add(new ClaudeLimit
        {
            Kind = key,
            Name = name,
            Percent = u.GetDouble(),
            ResetsAt = Time(el, "resets_at"),
            IsActive = true,
        });
    }

    /// <summary>
    /// Maps API limit kinds to short display names, passing unknown kinds through. Written
    /// in ordinary case: the panel's all-caps label is derived from this (ClaudeLimit.Label),
    /// while the tray tooltip uses it as it stands.
    ///
    /// "weekly_scoped" is one kind for every per-model (or per-surface) weekly pool, so the
    /// kind alone cannot say which pool a row is - the server names it in the row's scope,
    /// the same name claude.ai prints ("Fable this week"). The name is built from that
    /// rather than a model name written in here, so it follows the scope when Anthropic
    /// points it at a different model.
    ///
    /// <paramref name="split"/> says a per-model week is on screen beside the all-models
    /// one, which is then "Week (Total)" so the two cannot be mistaken for each other. On a
    /// plan with only the one week it stays plain "Week": there is nothing to tell apart,
    /// and "(Total)" would imply a breakdown that is not there.
    /// </summary>
    private static string NameFor(string kind, string? scope, bool split) => kind switch
    {
        _ when ClaudeLimitKind.IsSession(kind) => "Session",
        _ when ClaudeLimitKind.IsTotalWeek(kind) => split ? "Week (Total)" : "Week",
        "weekly_opus" or "seven_day_opus" => "Week (Opus)",
        "weekly_sonnet" or "seven_day_sonnet" => "Week (Sonnet)",
        "weekly_scoped" => $"Week ({scope ?? "Scoped"})",
        _ => char.ToUpperInvariant(kind[0]) + kind[1..].Replace('_', ' '),
    };

    /// <summary>
    /// The server's display name for what a scoped limit covers: <c>scope.model</c> or
    /// <c>scope.surface</c>, each carrying a <c>display_name</c>. Null for an unscoped row.
    /// </summary>
    private static string? ScopeName(JsonElement limit)
    {
        if (!limit.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in new[] { "model", "surface" })
        {
            if (scope.TryGetProperty(key, out var s) && s.ValueKind == JsonValueKind.Object
                && Str(s, "display_name") is { } name && !string.IsNullOrWhiteSpace(name))
                return name.Trim();
        }

        return null;
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Dbl(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static DateTimeOffset? Time(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(v.GetString(), out var t) ? t : null;

    /// <summary>
    /// Returns the stored tokens, refreshing the access token first if it has expired.
    ///
    /// These are this app's own to maintain, which is what lets the widget keep working on
    /// a machine where nothing else would ever refresh them. The refresh is serialised: the
    /// poll timer and a manual refresh can both land here at once, and because each refresh
    /// invalidates the previous refresh token, two concurrent attempts would race to spend
    /// the same one and leave the loser holding a token the server has already retired.
    ///
    /// Returns a null token with <c>RefreshFailed</c> false when there is simply no sign-in
    /// to use, or when the grant was refused outright - both mean the panel should offer a
    /// sign-in. <c>RefreshFailed</c> is true instead when a sign-in exists but could not be
    /// renewed right now, which is a transient condition and must not be reported as being
    /// signed out.
    /// </summary>
    private async Task<(ClaudeTokens? Tokens, bool RefreshFailed)> ResolveTokenAsync(CancellationToken ct)
    {
        var tokens = ClaudeTokenStore.Load();
        if (tokens is null) return (null, false);
        if (!tokens.Expired) return (tokens, false);

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read inside the gate: whoever held it may have just refreshed, in which
            // case the stored set is already current and spending the refresh token again
            // would retire the one that write just saved.
            tokens = ClaudeTokenStore.Load();
            if (tokens is null) return (null, false);
            if (!tokens.Expired) return (tokens, false);

            // A null here is a refusal - RefreshAsync has already cleared the dead grant -
            // so the panel should offer a sign-in rather than treat it as transient.
            var refreshed = await (_oauth ??= new ClaudeOAuth())
                .RefreshAsync(tokens.RefreshToken, ct).ConfigureAwait(false);
            if (refreshed is null)
                AppLog.Warn("Claude: scheduled token refresh was refused - clearing stored sign-in");
            return (refreshed, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A transport failure says nothing about the grant - an offline laptop must not
            // be signed out by it, and must not be shown a sign-in button either. The stored
            // tokens stay put and the caller reports a transient failure, so the next poll
            // simply tries again.
            AppLog.Warn($"Claude: scheduled token refresh transport failure - {ex.GetType().Name}: {ex.Message}");
            return (null, true);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private ClaudeOAuth? _oauth;

}
