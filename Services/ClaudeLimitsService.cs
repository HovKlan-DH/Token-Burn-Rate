using System;
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
            // A refresh that could not complete still leaves the stored sign-in in place,
            // so the sign-out menu item stays available; a refused grant has already been
            // cleared, so it does not.
            if (refreshFailed)
                return ClaudeLimitsStatus.Unavailable("Claude unreachable", transient: true, hasStoredSignIn: true);

            // No sign-in on this machine yet, or the grant was refused outright. Either way
            // the fix is the same and the panel offers it.
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
                        return ClaudeLimitsStatus.Unavailable("Claude rejected the session", transient: true, hasStoredSignIn: true);

                    var (renewed, renewFailed) = await ForceRefreshAsync(ct).ConfigureAwait(false);
                    if (renewed is null)
                    {
                        // Could not reach the endpoint to find out: the grant is untouched
                        // and still on disk, so this is a blip, not a sign-out.
                        return renewFailed
                            ? ClaudeLimitsStatus.Unavailable("Claude unreachable", transient: true, hasStoredSignIn: true)
                            : ClaudeLimitsStatus.Unavailable("Sign-in expired", canSignIn: true);
                    }

                    tokens = renewed;
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                    return ClaudeLimitsStatus.Unavailable($"usage API returned {(int)resp.StatusCode}", transient: true, hasStoredSignIn: true);

                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var status = Parse(json);
                status.Plan = tokens.SubscriptionType ?? status.Plan;
                status.HasStoredSignIn = true;      // a token answered, so one is stored
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
                return ClaudeLimitsStatus.Unavailable(ex.Message, transient: true, hasStoredSignIn: true);
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
        catch (Exception)
        {
            // Could not reach the endpoint. The tokens stay put - see ResolveTokenAsync -
            // and the caller reports this as a failure to renew rather than a sign-out.
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
            return (refreshed, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A transport failure says nothing about the grant - an offline laptop must not
            // be signed out by it, and must not be shown a sign-in button either. The stored
            // tokens stay put and the caller reports a transient failure, so the next poll
            // simply tries again.
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
