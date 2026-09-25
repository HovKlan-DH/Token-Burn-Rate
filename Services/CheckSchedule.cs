using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TokenBurnRate.Services;

/// <summary>
/// When the window's two background calls run - <see cref="CheckInService"/>'s ping-home
/// and <see cref="UpdateService"/>'s GitHub check. Both start at launch, which is exactly
/// when a VPN client (ZScaler and similar) may still be coming up, so a call can lose its
/// shot to a network that is not there yet.
///
/// A real failure (a rejected request, a server error) settles an attempt, the same as a
/// success. "The network is not routable yet" does not: it clears itself, often within
/// seconds, but sometimes only once the user signs in to the VPN by hand - minutes or hours
/// later. So a transport failure is retried quickly at first and then at a slow fallback
/// cadence with no end, and a change in the machine's network addresses (the VPN adapter
/// coming up) pulls the next attempt forward - see <see cref="NetworkChanged"/> - so the
/// call lands soon after the network does instead of on whatever this schedule guessed.
///
/// A schedule built with a recheck interval does not stop once an attempt settles: it waits
/// that long and runs again, and runs sooner when asked to - see
/// <see cref="RequestAttempt"/>. The update check needs both: the widget is left running
/// for days or weeks, so a check made only at launch never sees a release published after
/// it, and a changed update setting makes the last answer stale. The check-in must have
/// neither: it counts launches, and a repeat would count one twice.
///
/// Single-threaded by design: every member runs on the UI thread. The window claims
/// attempts from its retry tick, <see cref="RecordAttempt"/> runs in the continuation of an
/// await started there (which resumes on the UI thread), and the window marshals the OS's
/// network-change event onto the UI thread before calling <see cref="NetworkChanged"/>.
///
/// Retry deadlines are kept on <see cref="Environment.TickCount64"/>, not the wall clock.
/// At boot the clock is often corrected by time sync at the very moment the network comes
/// up, which is exactly when the retries run: a wall-clock deadline would then be pushed an
/// hour out, or fire at once. The recheck deadline is kept on both clocks - see
/// <see cref="_recheckDueUtc"/>.
/// </summary>
public sealed class CheckSchedule
{
    /// <summary>
    /// Delay before each retry after a transport failure, in order - not a formula, so the
    /// schedule reads directly as the sequence it runs. Index 0 is the wait after the first
    /// (launch) attempt fails. Past the end, every further retry waits <see cref="SlowRetry"/>.
    /// </summary>
    private static readonly TimeSpan[] Delays =
    {
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// The fallback once <see cref="Delays"/> has run out, for as long as the failures stay
    /// transport-shaped. A safety net under <see cref="NetworkChanged"/>, which is what
    /// normally ends the wait. Cheap: an attempt that fails this way never reached a server,
    /// so it spends no one's rate limit.
    /// </summary>
    private static readonly TimeSpan SlowRetry = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How soon after a network change the next attempt runs. Not immediately: a VPN
    /// adapter's address appears a moment before its routes and DNS are usable.
    /// </summary>
    private static readonly TimeSpan AfterNetworkChange = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The least time between one attempt ending and the next starting, whatever pulls it
    /// forward. A flapping adapter raises bursts of change events, and each one must not
    /// turn into an attempt.
    /// </summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long after the last change to a setting the attempt it asked for runs. A user
    /// moving from release-only to BETA who misclicks ALPHA first should get one attempt with
    /// the settings they ended on, not one per click - each attempt spends a GitHub API call,
    /// and one started on the in-between state could download a build they never meant to
    /// allow. The context menu closes after every click, so correcting a misclick means
    /// opening it again and finding the Advanced submenu: this allows for that, where a few
    /// seconds would not. The window also holds an update restart while the menu is open
    /// (see MainWindow.BusyForRestart), which covers a correction slower than this.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(15);

    private enum State { Idle, Waiting, InFlight, Settled }

    /// <summary>How long after a settled attempt the next one runs, or null for a schedule
    /// that is done once an attempt settles.</summary>
    private readonly TimeSpan? _recheckAfter;

    private State _state = State.Idle;
    private int _failures;
    private long _dueAt;
    private long _lastEndedAt;

    /// <summary>
    /// The recheck's deadline on the wall clock, set alongside <see cref="_dueAt"/>'s tick
    /// deadline while a recheck is waiting and null otherwise; the attempt is due when either
    /// has passed. TickCount64 does not advance through sleep on Linux or macOS (.NET reads
    /// CLOCK_MONOTONIC and CLOCK_UPTIME_RAW there), so on a laptop awake an hour a day the
    /// tick deadline alone would turn a four-hour recheck into a four-day one; Windows counts
    /// sleep and would not. The wall clock covers that, and the tick deadline still covers a
    /// wall clock stepped backwards. A wall clock stepped forwards fires the recheck early,
    /// which at this spacing costs one extra attempt and nothing else - unlike the retries,
    /// where the same step at boot would matter, which is why they stay on ticks alone.
    /// </summary>
    private DateTime? _recheckDueUtc;

    /// <summary>Set when the network changed while an attempt was running, so a failure of
    /// that attempt - probably started on the old network - retries soon rather than on
    /// the regular schedule.</summary>
    private bool _changedWhileInFlight;

    /// <summary>Set when <see cref="RequestAttempt"/> was called while an attempt was running.
    /// That attempt was started before whatever the request was about, so the next one
    /// follows it soon rather than on the regular schedule.</summary>
    private bool _requestedWhileInFlight;

    /// <param name="recheckAfter">Null (the default) for a call made once per launch;
    /// otherwise how long after each settled attempt the next one is due.</param>
    public CheckSchedule(TimeSpan? recheckAfter = null) => _recheckAfter = recheckAfter;

    /// <summary>
    /// Arms the schedule with its first attempt due immediately. Until this is called the
    /// schedule is idle and never hands out an attempt.
    /// </summary>
    public void Start()
    {
        if (_state != State.Idle) return;
        _state = State.Waiting;
        _dueAt = Now;
        _recheckDueUtc = null;
    }

    /// <summary>
    /// Claims the next attempt if one is due and none is running. A true answer obliges the
    /// caller to start the work and to report it through <see cref="RecordAttempt"/> however
    /// it ends; until then no further attempt is handed out.
    ///
    /// The in-flight state is what keeps the once-a-second poll from starting a second
    /// attempt on top of a slow one: without it a download that takes a minute would be
    /// started afresh on every tick for that whole minute - dozens of concurrent update
    /// downloads racing to restart the process, or dozens of duplicate check-in posts.
    /// </summary>
    public bool TryClaimAttempt()
    {
        if (_state != State.Waiting || !Due) return false;

        _state = State.InFlight;
        _changedWhileInFlight = false;
        _requestedWhileInFlight = false;
        return true;
    }

    private bool Due =>
        Now >= _dueAt || (_recheckDueUtc is { } utc && DateTime.UtcNow >= utc);

    /// <summary>
    /// Asks for an attempt soon rather than whenever the schedule would next run one - the
    /// user changed what the call does (the update settings), so the last attempt's answer
    /// no longer holds. A waiting schedule brings its next attempt to <see cref="SettleDelay"/>
    /// from now, but never sooner than <see cref="MinGap"/> after the last attempt ended, so
    /// clicking a setting back and forth cannot turn into a burst of attempts. If an attempt
    /// is running, the request is remembered and the next attempt follows that same gap
    /// after it ends.
    ///
    /// Only meaningful for a schedule with a recheck interval. One without is finished once
    /// an attempt settles, and an idle schedule has nothing armed yet to bring forward.
    /// </summary>
    public void RequestAttempt()
    {
        switch (_state)
        {
            case State.Waiting:
                // Set outright rather than only if sooner: every further request moves the
                // attempt to SettleDelay after itself, so a run of clicks is acted on once,
                // with the settings it ended on. A retry that was due sooner is moved by at
                // most that delay, or to MinGap after the last attempt ended.
                _dueAt = NotBefore(Now + (long)SettleDelay.TotalMilliseconds);
                _recheckDueUtc = null;
                break;

            case State.InFlight:
                _requestedWhileInFlight = true;
                break;
        }
    }

    /// <summary>
    /// Call after every claimed attempt. <paramref name="settled"/> is the bool
    /// CheckInService.PingHomeAsync and UpdateService.CheckAsync return: true for a success
    /// or a failure not worth retrying, false only for a transport-shaped failure. A settled
    /// attempt ends the schedule, or with a recheck interval sets the next one that far out
    /// - or <see cref="MinGap"/> out, if an attempt was requested while this one ran.
    /// </summary>
    public void RecordAttempt(bool settled)
    {
        if (_state != State.InFlight) return;

        _lastEndedAt = Now;
        _recheckDueUtc = null;

        if (settled && _recheckAfter is { } recheck)
        {
            // A fresh start rather than a continuation of the backoff: a failure on the next
            // round is a new outage, which deserves the quick retries again. Clearing the
            // count also keeps NetworkChanged from pulling this wait forward - it is a
            // regular recheck, not a retry waiting on the network.
            _failures = 0;
            _state = State.Waiting;

            if (_requestedWhileInFlight)
            {
                _dueAt = _lastEndedAt + (long)MinGap.TotalMilliseconds;
            }
            else
            {
                _dueAt = _lastEndedAt + (long)recheck.TotalMilliseconds;
                _recheckDueUtc = DateTime.UtcNow + recheck;
            }

            return;
        }

        if (settled)
        {
            _state = State.Settled;
            return;
        }

        // Every entry in Delays is used once, in order, before the fallback takes over -
        // indexed by failures already counted, so the fifth failure waits the fifth delay.
        var delay = _failures < Delays.Length ? Delays[_failures] : SlowRetry;
        _failures++;

        if ((_changedWhileInFlight || _requestedWhileInFlight) && MinGap < delay) delay = MinGap;

        _state = State.Waiting;
        _dueAt = _lastEndedAt + (long)delay.TotalMilliseconds;
    }

    /// <summary>
    /// The machine's network addresses changed - most usefully, a VPN adapter came up. A
    /// schedule still waiting on a transport failure brings its next attempt forward to
    /// <see cref="AfterNetworkChange"/> from now (never sooner than <see cref="MinGap"/> after
    /// the last attempt ended); one with an attempt running remembers it, so a failure of
    /// that attempt retries soon. An idle or settled schedule has nothing to bring forward,
    /// and neither has one waiting on a recheck - that is not waiting on the network.
    /// </summary>
    public void NetworkChanged()
    {
        switch (_state)
        {
            case State.Waiting when _failures > 0:
                var soon = NotBefore(Now + (long)AfterNetworkChange.TotalMilliseconds);
                if (soon < _dueAt) _dueAt = soon;
                break;

            case State.InFlight:
                _changedWhileInFlight = true;
                break;
        }
    }

    /// <summary>
    /// <paramref name="earliest"/>, or <see cref="MinGap"/> after the last attempt ended if
    /// that is later - the floor under everything that brings an attempt forward.
    /// </summary>
    private long NotBefore(long earliest) =>
        Math.Max(earliest, _lastEndedAt + (long)MinGap.TotalMilliseconds);

    private static long Now => Environment.TickCount64;

    /// <summary>
    /// For a request that is safe to repeat (the update check's GETs): a failure that never
    /// got an answer from a server - no route yet, DNS not resolving, a connect or request
    /// timeout - as opposed to a request that reached one and got a real answer. Only the
    /// former is worth retrying: an answer, error or not, would come back the same way.
    ///
    /// A server's answer includes an HTTP error status thrown as an exception:
    /// GetStringAsync and EnsureSuccessStatusCode, which Velopack's downloader uses, report
    /// a 403 rate limit or a 404 as an HttpRequestException carrying that StatusCode, which
    /// is why only one without a status counts. Retrying a rate limit would only spend more
    /// of the same quota.
    ///
    /// <paramref name="shutdown"/> is the app's shutdown token, and settles the one case the
    /// exception type cannot: a <see cref="TaskCanceledException"/> is HttpClient's own
    /// request timeout - retryable - unless shutdown asked for the cancellation, in which
    /// case there is nothing left to retry into.
    /// </summary>
    public static bool IsTransportFailure(Exception ex, CancellationToken shutdown) =>
        ex switch
        {
            OperationCanceledException when shutdown.IsCancellationRequested => false,
            HttpRequestException { StatusCode: not null } => false,
            HttpRequestException => true,
            SocketException => true,
            TaskCanceledException => true,     // HttpClient's own timeout, not user cancellation
            _ => ex.InnerException is not null && IsTransportFailure(ex.InnerException, shutdown),
        };

    /// <summary>
    /// Stricter twin of <see cref="IsTransportFailure"/> for a request that must not be sent
    /// twice (the check-in's POST): true only when the request failed before any of it could
    /// have left the machine - DNS, the TCP connect, the TLS handshake, the proxy tunnel.
    ///
    /// A timeout is deliberately not in that set. It can fire after the post was delivered
    /// and recorded, while the reply was still on its way, and a retry would then count the
    /// launch twice. CheckInService gives the TCP connect its own shorter limit, which fails
    /// as a ConnectionError, so a connection that hangs because the VPN is not up yet still
    /// qualifies - only a timeout after connecting is written off.
    /// </summary>
    public static bool FailedBeforeSending(Exception ex) =>
        ex switch
        {
            HttpRequestException
            {
                StatusCode: null,
                HttpRequestError: HttpRequestError.NameResolutionError
                    or HttpRequestError.ConnectionError
                    or HttpRequestError.SecureConnectionError
                    or HttpRequestError.ProxyTunnelError,
            } => true,
            HttpRequestException => false,
            OperationCanceledException => false,
            _ => ex.InnerException is not null && FailedBeforeSending(ex.InnerException),
        };
}
