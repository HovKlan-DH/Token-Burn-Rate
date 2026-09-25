# Token Burn Rate

Avalonia always-on-top widget showing live Claude Code and GitHub Copilot usage,
packaged with Velopack as one installer per OS (win-x64, linux-x64, osx-arm64, osx-x64)
that self-updates — see [Update mechanism](#update-mechanism).

## Working agreement

**Never commit.** Make the changes and stop, leaving them in the working tree — the user runs
`git commit` themselves and will ask on the occasions they want it done. Don't stage with
`git add` unprompted either.

## Project notes

Durable findings live in [.claude/memory/](.claude/memory/) — read
[MEMORY.md](.claude/memory/MEMORY.md) there for the index. Two that shape the design:

- The app runs on **two machines** (home: free personal Copilot plan; work: org-assigned
  seat). One binary adapts at runtime — never generalise the account shape from whichever
  machine is in front of you.
- **M365 Copilot is deliberately not shown.** Separate product and separate pool from
  GitHub Copilot, but no readable per-user quota exists. Do not re-research this.

## Gotchas

- **Claude bars must come from the API, not transcripts.** `api.anthropic.com/api/oauth/usage`
  (Bearer token, header `anthropic-beta: oauth-2025-04-20`) returns the same utilization
  claude.ai shows. Transcripts cannot reproduce it: the ceiling is unpublished and the limits
  reset at fixed times rather than rolling. The token comes from the widget's own sign-in —
  see [Claude sign-in](#claude-sign-in).
- **Never read or write `~/.claude/.credentials.json`.** The widget used to borrow Claude
  Code's token from it; that is gone deliberately. It only ever worked where the CLI was
  installed and used often enough to keep the token fresh, and the widget could not refresh
  it itself without risking a running CLI's credentials.
- **Trimming must stay off.** Avalonia resolves XAML by reflection; trimming breaks it only
  in published builds, where it is hardest to diagnose.
- `Grid.ColumnDefinitions` cannot be bound in Avalonia — the bars are a custom-drawn
  `UsageBar` control ([Views/UsageBar.cs](Views/UsageBar.cs)) that derives fill from its own
  measured width.
- **`vpk pack` needs an unpacked publish folder, not a single-file exe.** That's why the
  csproj carries no `PublishSingleFile` — see [Update mechanism](#update-mechanism) for what
  replaced it. Don't re-add single-file publishing without also removing Velopack.

## Claude sign-in

The widget is its own OAuth client ([Services/ClaudeOAuth.cs](Services/ClaudeOAuth.cs)) and
this is the **only** way it gets a Claude token. The user signs in once per machine; the
widget refreshes the token itself from then on, so it works identically whether or not
Claude Code is installed. There is one code path, not two.

- Authorization-code + **PKCE**, not a device flow: Anthropic exposes no device-code grant
  for this client. The redirect is Anthropic's own hosted callback page, which displays a
  code for the user to paste back — so the paste box in
  [Views/ClaudeSignInWindow.axaml](Views/ClaudeSignInWindow.axaml) is the flow, not a
  shortcut. A localhost listener is not an option: the client_id is fixed and shared, so
  only already-registered redirect URIs are accepted.
- **The `state` check is the only CSRF defence** this shape has, since there is no browser
  redirect to trust. The pasted code arrives as `CODE#STATE`; the state half is compared in
  constant time and a paste carrying no state is refused outright rather than exchanged.
  Don't "simplify" that away.
- **Scope is deliberately narrower than Claude Code's**: only `user:profile`. Not
  `user:inference` (would let a leaked token spend the user's quota on model calls) and not
  `org:create_api_key` (would let it mint durable keys). A token this app stores must not be
  able to do more than this app does.
- Tokens live in `%LocalAppData%/<AppFolder>/claude.json`, **DPAPI-encrypted at rest** on
  Windows and owner-only on every OS. macOS/Linux fall back to plaintext-with-permissions,
  as Claude Code itself does there — a keyring daemon cannot be assumed on a headless
  session.
- **Refresh tokens rotate**: each refresh invalidates the one it used, so the new set is
  written to disk inside `RefreshAsync` rather than by its caller, and refreshes are
  serialised behind a semaphore. Two concurrent refreshes would race to spend the same
  token and sign the user out.
- Token endpoints are tried newest-host-first (`platform.claude.com`, then
  `console.anthropic.com`) because Anthropic migrated them mid-life; a 404 falls through,
  a real OAuth refusal does not.
- A refused grant clears the stored tokens so the panel offers a sign-in; a *transport*
  failure deliberately does not, so an offline laptop is never signed out or told to
  re-authenticate over a connection it does not have.

## Update mechanism

Packaging switched from a raw self-contained single-file exe per OS to a
[Velopack](https://velopack.io)-packaged installer per OS, so the app can auto-update from
GitHub Releases. Full rationale and the tradeoffs behind it:
[.claude/memory/velopack-auto-update.md](.claude/memory/velopack-auto-update.md).

- `Program.cs` runs `VelopackApp.Build().Run()` before anything else — required even in
  dev builds (`dotnet run`), where it's a no-op since Velopack finds no installed location.
- `Services/UpdateService.cs` checks GitHub Releases at launch and then every
  `UpdateService.RecheckInterval` (currently 4 hours - the menu tooltip is built from the
  same constant, so change it in one place) for as long as the application runs and, if a
  newer version exists, downloads and applies it **silently**, then restarts - no dialog,
  no menu interaction. This was a deliberate choice over a "click to install" flow. The
  recheck matters because the application is left running for days or weeks: every
  published release up to and including 1.1.0 checked only at launch, so a running copy
  never saw a release published after it started, and needs one manual restart to reach the
  first build that rechecks. Don't shorten the interval casually - anonymous GitHub API
  calls are capped at 60/hour per IP, shared by everyone behind the same VPN egress
  address. A check spends one (the release list); the feed files come through plain
  download links, which that cap does not count.
- `GithubSource` reads the 10 newest releases and joins all their feed files, and Velopack
  takes the highest version in that joined feed. `UpdateService.TieredGithubSource` filters
  the joined feed down to the tiers the settings allow, so the highest *allowed* version
  wins - otherwise, with only BETA ticked, a newer alpha would top the feed, be refused, and
  hide the beta or full release beneath it on every check.
- A check that never reached its server (typically a VPN such as ZScaler still coming up)
  is retried by `Services/CheckSchedule.cs`, for both the update check and the check-in:
  quickly at first, then every 30 minutes, and soon after any network address change. The
  recheck's deadline is kept on the wall clock as well as `TickCount64`, which does not
  advance during sleep on Linux and macOS. Because a retry or recheck can land mid-session,
  the restart into an update is held while a sign-in, dialog or the context menu is open,
  and the decision to restart and the restart itself happen in one UI-thread turn, so an
  Exit click cannot land between them.
- Velopack's auto-apply on startup stays on (`VelopackApp.Build().Run()` in `Program.cs`):
  an update downloaded but still held when the user exits is installed at the next launch,
  before the UI. A download the check decides against (a setting changed mid-download) is
  deleted instead (`UpdateService.DiscardDownload`), so it is never installed that way.
  Velopack's own path helper for it is internal, so the path is rebuilt as the packages
  folder plus the file name.
- The context menu's Advanced submenu has an "Auto-update to newest version" toggle,
  checked by default, backed by `AppState.AutoUpdate`/`MainViewModel.AutoUpdate`. Unchecked,
  `MainWindow` never starts, retries or rechecks `UpdateService.CheckAsync`, and a check
  already running re-reads the settings at each of its gates - the application never checks
  GitHub Releases and never restarts itself. Turning it on, or a BETA/ALPHA change that
  moves the tier ceiling while it is on, runs a fresh check shortly after
  (`CheckSchedule.RequestAttempt`: 15 s after the last click - the menu closes on every
  click, so undoing a misclick takes a moment - and never sooner than 15 s after the
  previous check ended). A request made while a check runs is queued behind it. A check
  already running when a setting changes stops at its next gate if the candidate is no
  longer allowed, deleting any download - that, plus the restart hold while the menu is
  open, is what keeps an alpha unticked mid-download from being installed. This is separate
  from `Services/CheckInService.cs`'s mailscan.dk "ping home", which stays mandatory
  regardless of this setting since it's telemetry, not an update check.
- By default only real (bare `X.Y.Z`) versions are offered. The context menu's Advanced
  submenu has two unchecked-by-default toggles, each an independent gate — "Allow updates
  to newer BETA version" accepts `-beta.N` builds, "Allow updates to newer ALPHA version"
  accepts `-alpha.N` builds (there is deliberately no `-rc` tier — CI's version scheme only
  ever produces alpha, beta, or a bare release). Checking one does not check the other, but
  since alpha is the less stable tier, checking ALPHA alone still widens the update ceiling
  to admit alpha, beta, and release builds — see `UpdateService.MaxTierRequested`. Both
  persist to the state file (`AppState.UpdateIncludeAlpha/Beta`) and are read by
  `MainViewModel`; `MainWindow` hands all three settings to `UpdateService.CheckAsync` as a
  snapshot it re-reads on the UI thread at each gate. Bare releases exist from 1.0.0 on, so
  a default launch does have something to update to.
- CI ([.github/workflows/build-and-release.yml](.github/workflows/build-and-release.yml))
  runs `dotnet publish` into an unpacked folder per RID, then `vpk pack` turns that into
  the single downloadable file users actually get (`Setup.exe` / `.AppImage` / `Setup.pkg`),
  plus the `.nupkg` and `releases.*.json`/`assets.*.json` feed files the update check reads.
  The macOS `.icns` icon is generated at CI time via `iconutil` from the existing
  `Assets/icon-*.png` set — there's no separate `.icns` source file to maintain.
- **Windows signing goes through `vpk pack --signTemplate`** (jsign with the YubiKey on the
  self-hosted runner, PIN read by jsign from `env:YUBIKEY_PIN`). vpk signs every `.exe`/`.dll`
  not already validly signed - the application's own `Token-Burn-Rate.dll`, the third-party
  libraries, its own `Squirrel.exe` updater and launcher stub - then `Setup.exe`. Don't go back
  to jsign steps before/after `vpk pack`: that left 30 of 223 program files unsigned, and
  nothing inside the package can be signed after packing because its SHA-256 is in the
  update feed. macOS and Linux files are unsigned (macOS would need an Apple Developer ID).
- **The release workflow refuses a version that is already tagged** (`determine-version`).
  Re-releasing a number would overwrite the published files, and installs already on that
  version would never be offered the rebuild - an update must be a higher version.
- **VirusTotal scans every downloadable file between the builds and the release**
  (`virustotal-scan` job, rules in [.github/scripts/virustotal-scan.sh](.github/scripts/virustotal-scan.sh)),
  using the `VT_API_KEY` repository secret (a free VirusTotal account's key). The workflow's
  `virustotal` input picks `report` (list results, never block), `gate` (stop the release at
  3+ malicious verdicts or any from a major engine) or `skip`. The job is skipped step by step,
  not with a job-level `if`, because a skipped job would skip `create-release` with it. The free
  API allows 4 calls a minute and 500 a day, and every file is over the 32 MB plain-upload limit,
  so the script uses the large-file upload URL and paces its calls.
  [.github/workflows/virustotal-scan.yml](.github/workflows/virustotal-scan.yml) runs the same
  script by hand against an already published release.

## Build

```bash
./build-all.sh    # publishes the unpacked folder per OS - vpk pack's input, not a final exe
dotnet build      # local debug
```

`build-all.sh`'s output is not what end users download; the CI workflow above is what
produces the actual installer via `vpk pack`. There is no local one-liner for that step —
run it by hand (`vpk pack --packId Token-Burn-Rate ...`) against a `publish/<rid>` folder if
you need to test packaging locally.
