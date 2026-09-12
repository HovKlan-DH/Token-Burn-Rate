using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TokenBurnRate.Services;

/// <summary>
/// Signs in to Claude with the OAuth authorization-code + PKCE flow, so the widget can show
/// Claude usage on a machine where the `claude` CLI is not installed at all.
///
/// This is the only way the widget obtains a Claude token: it never reads Claude Code's
/// credentials file. Borrowing that token only worked where the CLI was installed and used
/// often enough to keep it fresh, and this app could not refresh it without risking a
/// running CLI's credentials - see ClaudeLimitsService.ResolveTokenAsync.
///
/// Why not a device flow, which is how <see cref="GitHubDeviceAuth"/> solves the same
/// problem for Copilot: Anthropic exposes no device-code grant for this client, so the only
/// available shape is authorization-code + PKCE against a fixed, hosted redirect URI that
/// displays the code for the user to copy back. That makes the paste box below the flow's
/// UI rather than a design choice - there is no loopback listener to redirect to, because
/// the redirect URI is not ours to choose.
///
/// Security notes, since the pasted-code shape removes the protections a redirect gives:
///
/// - PKCE (S256) is mandatory here, not optional. The client id is public and shared by
///   every Claude Code install, so the verifier is the only thing binding the code being
///   redeemed to the process that asked for it. It is generated from
///   <see cref="RandomNumberGenerator"/> and never leaves this process except as its SHA-256
///   hash in the authorize URL.
/// - The authorization server returns the code as "CODE#STATE". The state half is compared
///   against the value this process generated, in constant time, and a mismatch aborts the
///   exchange. With a hosted redirect there is no browser origin to trust, so this check is
///   the whole defence against a user being talked into pasting an attacker's code - which
///   would otherwise attach the attacker's Claude account to this widget, or, with a code
///   the attacker later redeems, hand them a session. A pasted code that does not carry a
///   state is rejected outright rather than exchanged hopefully.
/// - One verifier is used once. <see cref="Begin"/> replaces any pending attempt, and the
///   pending attempt is cleared the moment a code is redeemed, so a verifier cannot be
///   replayed against a second code.
/// - Tokens are written only through <see cref="ClaudeTokenStore"/>, which encrypts at rest
///   where the OS offers it.
/// </summary>
public sealed class ClaudeOAuth
{
    // Claude Code's own public OAuth client id. Like the gh client id in GitHubDeviceAuth
    // this is an identifier rather than a secret - it is embedded in every Claude Code
    // install - and the user still has to approve the grant interactively in their browser.
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";

    /// <summary>
    /// Anthropic's hosted callback, which renders the code for the user to copy. It is not
    /// a URL this app serves or could substitute: the client id is fixed and shared, so the
    /// authorization server only accepts redirect URIs already registered against it.
    /// </summary>
    private const string RedirectUri = "https://platform.claude.com/oauth/code/callback";

    /// <summary>
    /// Token exchange and refresh, newest host first.
    ///
    /// Anthropic moved these from console.anthropic.com to platform.claude.com, and the old
    /// host now answers the token path with 404. Both are tried in order so the widget keeps
    /// working across that migration in either direction - a 404, a retryable status or a
    /// transport failure on the first falls through to the second, while a real OAuth
    /// refusal (an invalid_grant, say) is returned as-is rather than being retried against a
    /// host that would only give the same answer. See PostAsync for which is which.
    /// </summary>
    private static readonly string[] TokenUrls =
    {
        "https://platform.claude.com/v1/oauth/token",
        "https://console.anthropic.com/v1/oauth/token",
    };

    /// <summary>
    /// Least privilege: this app reads a usage figure and nothing else, so it asks only for
    /// the profile scope the usage endpoint authorizes against. Deliberately not requested:
    /// "user:inference" (the right to spend the user's tokens on model calls - which this
    /// app must never do, and which would make a leaked token far more damaging) and
    /// "org:create_api_key" (the right to mint durable API keys). A token this app stores
    /// should not be able to do anything this app does not do.
    /// </summary>
    private const string Scope = "user:profile";

    /// <summary>
    /// The exchange is slow at the far end - this endpoint routinely takes the better part
    /// of a minute under load - and a timeout here costs the user the whole paste-and-approve
    /// round trip, so it is far more generous than the polling timeouts elsewhere.
    /// </summary>
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;

    public ClaudeOAuth(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = ExchangeTimeout };
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("Token-Burn-Rate/1.0");
    }

    /// <summary>
    /// One in-flight sign-in attempt: the URL to send the user to, and the PKCE verifier and
    /// state that the pasted code must be redeemed against.
    ///
    /// The two secrets stay in memory for the life of the attempt only - they are never
    /// persisted, since a verifier on disk outliving the attempt would be a credential with
    /// no purpose left to serve.
    /// </summary>
    public sealed record Attempt(string AuthorizeUrl, string Verifier, string State);

    private Attempt? _pending;

    /// <summary>
    /// Starts a sign-in: generates PKCE material and returns the URL to open in a browser.
    /// Replaces any attempt already pending, so the code the user eventually pastes can only
    /// ever be redeemed against the most recent request they actually saw.
    /// </summary>
    public Attempt Begin()
    {
        var verifier = RandomUrlSafe(64);
        var state = RandomUrlSafe(32);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var query = new Dictionary<string, string>
        {
            ["code"] = "true",
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };

        var url = AuthorizeUrl + "?" + string.Join("&", Build(query));
        var attempt = new Attempt(url, verifier, state);
        _pending = attempt;
        return attempt;

        static IEnumerable<string> Build(Dictionary<string, string> q)
        {
            foreach (var kv in q)
                yield return Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value);
        }
    }

    /// <summary>Whether a sign-in is waiting for a code to be pasted.</summary>
    public bool HasPendingAttempt => _pending is not null;

    /// <summary>Abandons a pending attempt, discarding its verifier.</summary>
    public void Cancel() => _pending = null;

    /// <summary>The outcome of redeeming a pasted code.</summary>
    public sealed record Result(bool Success, string? Error = null);

    /// <summary>
    /// Redeems the code the user pasted and, on success, stores the resulting tokens.
    ///
    /// <paramref name="pasted"/> is whatever came out of the text box: the callback page
    /// shows "CODE#STATE", and users paste it with stray whitespace, a trailing newline, or
    /// occasionally the whole URL. It is normalised here rather than at the call site so the
    /// state check cannot be bypassed by an input shape the UI failed to anticipate.
    /// </summary>
    public async Task<Result> RedeemAsync(string pasted, CancellationToken ct = default)
    {
        if (_pending is not { } attempt)
            return new Result(false, "sign-in was not started - click sign in again");

        if (!TrySplit(pasted, out var code, out var state))
            return new Result(false, "that does not look like a Claude authorization code");

        // The one CSRF check this flow has. Constant-time, so a mismatch cannot be
        // narrowed down a character at a time by timing repeated pastes.
        if (!FixedTimeEquals(state, attempt.State))
            return new Result(false, "this code was issued for a different sign-in - start again");

        // Spent either way: a verifier must never be reusable against a second code, and a
        // failed exchange is restarted from Begin() rather than retried against this one.
        _pending = null;

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = attempt.Verifier,
            ["state"] = state,
        };

        try
        {
            var tokens = await PostAsync(form, ct).ConfigureAwait(false);
            if (tokens is null) return new Result(false, "Claude rejected the sign-in - try again");

            ClaudeTokenStore.Save(tokens);
            return new Result(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message);
        }
    }

    /// <summary>
    /// Exchanges the stored refresh token for a fresh access token, replacing what is on
    /// disk with the whole new set.
    ///
    /// Refresh tokens rotate: each refresh returns a new one and invalidates the one used,
    /// so a response that is fetched but not persisted leaves the stored token already dead
    /// and the user silently signed out. That is why the write happens here, immediately,
    /// rather than being left to the caller.
    ///
    /// Returns null when the refresh was refused - a revoked or expired grant, or a password
    /// change - which is unrecoverable without the user signing in again. The stored tokens
    /// are cleared in that case so the UI can offer a sign-in rather than retrying a grant
    /// that will never succeed again. A transport failure is thrown instead, so a flight-mode
    /// laptop does not get signed out by a failure that says nothing about the grant.
    /// </summary>
    public async Task<ClaudeTokens?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        };

        var tokens = await PostAsync(form, ct).ConfigureAwait(false);
        if (tokens is null)
        {
            ClaudeTokenStore.Clear();
            return null;
        }

        // Anthropic has been observed to omit refresh_token on a refresh response. Carrying
        // the old one forward is correct there - it was not rotated if it was not reissued -
        // and dropping it would sign the user out at the next expiry for no reason.
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            tokens = tokens with { RefreshToken = refreshToken };

        ClaudeTokenStore.Save(tokens);
        return tokens;
    }

    /// <summary>
    /// Posts a token request, trying each host in <see cref="TokenUrls"/> until one gives a
    /// real answer.
    ///
    /// Returns null only when the server actually refused the grant, and throws for anything
    /// retryable. That distinction is the whole contract: <see cref="RefreshAsync"/> deletes
    /// the stored tokens on a null, so treating a server-side hiccup as a refusal would sign
    /// the user out over a 503 and force a fresh sign-in for a fault that had nothing to do
    /// with their grant. Only the statuses OAuth uses to reject a grant (400 invalid_grant,
    /// 401, 403) count as a refusal; 408, 429 and every 5xx are transient and throw, as does
    /// a 404 that has run out of hosts to try - a moved endpoint is a routing problem, not a
    /// revoked token.
    /// </summary>
    private async Task<ClaudeTokens?> PostAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        Exception? lastTransport = null;

        for (int i = 0; i < TokenUrls.Length; i++)
        {
            var url = TokenUrls[i];
            try
            {
                using var content = new FormUrlEncodedContent(form);
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

                // A host that no longer serves this path is the migration case, not a
                // refusal: fall through and let the next host answer.
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound && i < TokenUrls.Length - 1)
                    continue;

                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    var parsed = ClaudeTokens.Parse(json);
                    if (parsed is not null) return parsed;

                    // 2xx that did not yield a token: a truncated body, an HTML error page
                    // from a proxy, a shape change. Not a refusal - the grant was never
                    // rejected - so this must not reach the caller as one.
                    lastTransport = new HttpRequestException(
                        "Claude token endpoint returned an unreadable response");
                    continue;
                }

                if (IsGrantRefusal(resp.StatusCode))
                    return null;

                // Retryable: surfaced as an exception so the caller reports a transient
                // failure and keeps the tokens. Recorded rather than thrown immediately so a
                // failing first host still gives the second one its turn.
                lastTransport = new HttpRequestException(
                    $"Claude token endpoint returned {(int)resp.StatusCode}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                lastTransport = ex;
            }
        }

        throw lastTransport ?? new HttpRequestException("no Claude token endpoint could be reached");
    }

    /// <summary>
    /// Whether a status means the authorization server rejected the grant itself, as opposed
    /// to failing to answer. Only these justify deleting the stored refresh token.
    /// </summary>
    private static bool IsGrantRefusal(System.Net.HttpStatusCode status) => status is
        System.Net.HttpStatusCode.BadRequest or        // invalid_grant / invalid_request
        System.Net.HttpStatusCode.Unauthorized or
        System.Net.HttpStatusCode.Forbidden;

    /// <summary>
    /// Pulls "CODE#STATE" out of whatever the user pasted.
    ///
    /// Accepts the bare pair and the full callback URL (some browsers copy the address
    /// rather than the code shown on the page), and tolerates surrounding whitespace. A
    /// paste with no state half is rejected rather than exchanged: without it the CSRF
    /// check above has nothing to compare, and an unverified code is exactly the thing
    /// that check exists to refuse.
    /// </summary>
    internal static bool TrySplit(string? pasted, out string code, out string state)
    {
        code = "";
        state = "";
        if (string.IsNullOrWhiteSpace(pasted)) return false;

        var text = pasted.Trim();

        // A pasted URL carries the pair in ?code=, with the state after the same '#'.
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Split the query into real parameters and match the name exactly. A plain
                // IndexOf("code=") would also match the tail of another parameter's name -
                // "redirect_code=" or "auth_code=" - and take that one's value instead.
                var value = (string?)null;
                foreach (var pair in new Uri(text).Query.TrimStart('?').Split('&'))
                {
                    var eq = pair.IndexOf('=');
                    if (eq < 0) continue;
                    if (!pair.AsSpan(0, eq).Equals("code", StringComparison.OrdinalIgnoreCase)) continue;

                    value = pair[(eq + 1)..];
                    break;
                }

                if (value is null) return false;
                text = Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        var hash = text.IndexOf('#');
        if (hash <= 0 || hash == text.Length - 1) return false;

        code = text[..hash].Trim();
        state = text[(hash + 1)..].Trim();

        // Codes and states are URL-safe base64-ish; anything with whitespace or a control
        // character in it is a mangled paste rather than a credential, and sending it on
        // would only produce a confusing server-side error.
        return IsPlausible(code) && IsPlausible(state);

        static bool IsPlausible(string s)
        {
            if (s.Length is 0 or > 512) return false;
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c)) continue;
                if (c is '-' or '_' or '.' or '~' or '+' or '/' or '=') continue;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Compares two ASCII secrets without leaking where they first differ.
    ///
    /// Length is compared up front and separately, which is unavoidable for variable-length
    /// strings and harmless here: both values are fixed-length by construction, so the only
    /// thing a length difference reveals is that the paste was not ours at all.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    /// <summary>
    /// A URL-safe random string of <paramref name="bytes"/> entropy, from the OS CSPRNG.
    /// Never System.Random: these values are the whole of the flow's security.
    /// </summary>
    private static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// One set of Claude OAuth tokens, as returned by the token endpoint.
///
/// <see cref="ExpiresAt"/> is absolute rather than the server's relative expires_in, so that
/// a set restored from disk hours later still knows when it goes stale.
/// </summary>
public sealed record ClaudeTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string? SubscriptionType)
{
    /// <summary>
    /// Whether the access token is past use, with a 30-second skew allowance: a token this
    /// close to expiry would fail the request anyway, and refreshing early costs nothing.
    /// </summary>
    public bool Expired => ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30);

    public static ClaudeTokens? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var access = Str(root, "access_token");
            if (string.IsNullOrWhiteSpace(access)) return null;

            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number
                ? e.GetInt64()
                : 8 * 60 * 60;          // the documented 8-hour default, if it is ever omitted

            // The plan name is not a top-level field on every response shape; where it is
            // absent the panel simply shows no plan rather than a wrong one.
            var plan = Str(root, "subscription_type")
                       ?? (root.TryGetProperty("account", out var acct) && acct.ValueKind == JsonValueKind.Object
                           ? Str(acct, "subscription_type")
                           : null);

            return new ClaudeTokens(
                access,
                Str(root, "refresh_token") ?? "",
                DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                plan);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Stores the Claude tokens this app obtained itself, in its own per-user file. Claude
/// Code's credentials file is never read or written - this app keeps its own sign-in.
///
/// Two layers, because this is a longer-lived and more powerful secret than the GitHub
/// token beside it: a refresh token renews itself indefinitely, where the device-flow token
/// is a single opaque string the user can revoke on a web page they already know about.
///
/// - At rest, the payload is encrypted with DPAPI (CurrentUser scope) on Windows, so the
///   file is unreadable even to a process running as another user with administrative
///   rights to the disk, and worthless if the profile is copied off the machine. macOS and
///   Linux have no equivalent that works without a desktop keyring daemon this app cannot
///   assume is running - a headless or minimal session has none - so there the file falls
///   back to plaintext with owner-only permissions, which is the same protection Claude
///   Code itself relies on there.
/// - The file is permission-locked to the current user on every write, the same way
///   GitHubDeviceAuth does it.
///
/// A file that cannot be decrypted - copied from another machine or another user - is
/// treated as absent rather than as an error, which degrades to "sign in again" instead of
/// wedging the panel.
/// </summary>
public static class ClaudeTokenStore
{
    private const string FileName = "claude.json";

    /// <summary>Marks a payload this build encrypted, so a plaintext file still reads.</summary>
    private const string DpapiPrefix = "dpapi:";

    private static string TokenPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppState.AppFolderName);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, FileName);
        }
    }

    /// <summary>Whether this machine has a Claude sign-in of this app's own.</summary>
    public static bool HasTokens => Load() is not null;

    public static ClaudeTokens? Load()
    {
        try
        {
            var path = TokenPath;
            if (!File.Exists(path)) return null;

            var raw = File.ReadAllText(path);
            var json = Unprotect(raw);
            if (json is null) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var access = Str(root, "access_token");
            var refresh = Str(root, "refresh_token");
            if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh)) return null;

            var expiresAt = root.TryGetProperty("expires_at", out var e) && e.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                // Unknown expiry - a truncated or hand-edited file. Treated as stale so the
                // next poll refreshes rather than sending a token that may already be dead.
                // Safe now that only a genuine refusal clears the store (see
                // ClaudeOAuth.PostAsync): a refresh that cannot reach the endpoint leaves
                // this file exactly as it is rather than discarding a working grant.
                : DateTimeOffset.MinValue;

            return new ClaudeTokens(access, refresh, expiresAt, Str(root, "subscription_type"));
        }
        catch (Exception)
        {
            // Corrupt, truncated, or encrypted for a different user: indistinguishable from
            // never having signed in, and recoverable the same way.
            return null;
        }
    }

    public static void Save(ClaudeTokens tokens)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                access_token = tokens.AccessToken,
                refresh_token = tokens.RefreshToken,
                expires_at = tokens.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
                subscription_type = tokens.SubscriptionType,
            });

            var path = TokenPath;

            // Created restricted, then written: a plain WriteAllText would leave the token
            // readable for the instant between the file appearing and the ACL being applied.
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                RestrictToCurrentUser(path);
                var bytes = Encoding.UTF8.GetBytes(Protect(json));
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception)
        {
            // An unwritable profile costs the stored sign-in, never the running app: the
            // in-memory token still serves this session.
        }
    }

    /// <summary>
    /// Deletes the stored tokens. Returns whether the file is actually gone afterwards, so
    /// an interactive sign-out can tell the user the truth rather than reporting success
    /// over a file that is locked or read-only and still holds a live refresh token.
    /// </summary>
    public static bool Clear()
    {
        try
        {
            var path = TokenPath;
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Encrypts with DPAPI where available. The result is prefixed so <see cref="Unprotect"/>
    /// can tell an encrypted payload from a plaintext one written on another OS.
    /// </summary>
    private static string Protect(string json)
    {
        if (!OperatingSystem.IsWindows()) return json;

        try
        {
            var cipher = System.Security.Cryptography.ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json),
                optionalEntropy: null,
                scope: System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(cipher);
        }
        catch (Exception)
        {
            // DPAPI is unavailable in a few sandboxed or service contexts. The ACL below is
            // then the only protection, which is still what the GitHub token beside it gets.
            return json;
        }
    }

    private static string? Unprotect(string raw)
    {
        if (!raw.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return raw;
        if (!OperatingSystem.IsWindows()) return null;      // written on Windows, read elsewhere

        try
        {
            var cipher = Convert.FromBase64String(raw[DpapiPrefix.Length..]);
            var plain = System.Security.Cryptography.ProtectedData.Unprotect(
                cipher,
                optionalEntropy: null,
                scope: System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            return null;        // another user's or another machine's file
        }
    }

    /// <summary>
    /// Strips inherited permissions so only the current user can read the file. Windows-only;
    /// on Unix the file is created with the process umask, as GitHubDeviceAuth notes.
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

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
