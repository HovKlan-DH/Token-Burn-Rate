# TokenBurnRate

A small always-on-top desktop widget showing live AI usage for **Claude Code** and
**GitHub Copilot**, as a single portable executable for Windows, Linux and macOS.

![widget](docs/screenshot.png)

## What it shows

### Claude — session / week

Read from Anthropic's own usage endpoint using the OAuth token that Claude Code already
stores, so the percentages match the Usage screen on claude.ai exactly. Each bar shows real
utilization and a real reset countdown ("resets in 4h 3m").

Whichever limits your plan has are rendered, so a Max plan showing separate weekly Opus and
Sonnet limits gets extra bars with no code change.

The absolute token figure beside the plan name still comes from the local transcripts
(`~/.claude/projects/**/*.jsonl`), which is the only place per-message token counts exist.
It is informational: it counts `input + output + cache-creation` over a rolling five hours,
and deliberately excludes cache *reads*, which are billed at a fraction of the input rate
and run ~100x larger than everything else.

> **Why not compute the bars from transcripts?**
> An earlier version did, and it disagreed with claude.ai. Two reasons, both fatal:
> Anthropic publishes no token ceiling, so the denominator had to be invented (the user's
> own historical peak); and the real limits reset at fixed times, whereas a transcript
> calculation can only measure a rolling lookback from now. A rolling window still counts
> usage that a reset has already discarded. Limits can also be temporarily boosted, which
> no local calculation can know about.

### GitHub Copilot — completions / chat / premium
Read live from GitHub's `copilot_internal/user` endpoint, authenticated with the token from
your existing `gh` CLI login. These are **monthly** quotas that reset on a fixed date.

Copilot deliberately does *not* get today/week/month bars: the endpoint returns only a
point-in-time snapshot, and nothing on disk records history, so shorter windows cannot be
derived. A quota not included in your plan (e.g. premium interactions on the free tier) is
shown dimmed rather than as a misleading empty bar, and an unlimited bucket shows as ∞.

The panel adapts to whichever account the machine is signed in to, with no configuration:

| | Personal account | Org-assigned (business / enterprise) |
| --- | --- | --- |
| Subtitle | plan tier | `org-name · plan tier` |
| Completions / Chat | quota bars | often unlimited, shown as ∞ |
| Premium interactions | dimmed, not in plan | real bar (e.g. 177/300) |

### GitHub Copilot : My Pace — day / week / month

A second Copilot panel, below the first, that reframes the same credit balance as "how much
can I spend today". GitHub reports only a point-in-time balance, so daily spend is
reconstructed: the balance at the **first launch of the day** is recorded, and today's usage
is the drop from it.

```
allowance per day = credits remaining / business days remaining in the period
used today        = balance at first launch today - balance now
```

With 1000 credits and 10 business days left, the allowance is 100/day; spend 60 and the bar
reads 60%. A business day is Monday-Friday; public holidays are not modelled, which only
makes the allowance slightly conservative.

The allowance is **recalculated daily from what is actually left**, so it self-corrects:
underspend today and tomorrow's rises, overspend and it falls. Spending past the allowance
shows above 100% — the bar stops at full but the number keeps climbing.

| Bar | Used | Budget |
| --- | --- | --- |
| Day | since first launch today | remaining / business days left |
| Week | since Monday | daily allowance x business days left this week, plus what the week already spent |
| Month | the period total from GitHub | the full entitlement |

Only the current day and week are tracked (see [State file](#state-file)), so a day the app
is never opened simply starts fresh at the next launch. The panel picks whichever
quota actually meters spend — premium interactions on a paid org seat, completions on a
personal plan — and hides itself when a plan meters nothing.

### Why there is no Microsoft 365 Copilot panel

M365 Copilot (Word, Excel, Outlook, Teams) is a **different product** from GitHub Copilot
with a **separate pool** — prompts spent in Office do not touch the GitHub Copilot quota.
It is deliberately not shown here, because its usage is not readable from a user's machine:

- The only programmatic source is Microsoft Graph's `getMicrosoft365CopilotUsageUserDetail`,
  which requires `Reports.Read.All` **plus a tenant admin role** (Reports Reader, AI
  Administrator, Company Administrator, …). It returns tenant-wide data about every user,
  which is why it is gated.
- It reports **no quota at all** — only per-app last-activity dates, and prompt counts in
  v2. There is no entitlement or remaining balance to draw a bar against.
- It is a daily-refreshed adoption report, not a live counter.

Enterprise M365 Copilot is also typically a flat licensed seat rather than a metered pool,
so for most work tenants there is no "remaining" figure that would even be meaningful.

## Bar denominators

Every bar is filled against a figure published by the service itself — Anthropic's
utilization percentage, GitHub's entitlement. Nothing is inferred locally, so the widget
cannot drift from what the vendor dashboards report.

## Requirements

- **Claude panel** — Claude Code installed and signed in. The OAuth token is read from
  `~/.claude/.credentials.json` (honours `CLAUDE_CONFIG_DIR`) and **never written back**:
  Claude Code owns that file and refreshes the token itself roughly every 8 hours. If the
  token has expired the panel says so; run `claude` once to refresh it.
- **Copilot panel** — a GitHub sign-in. Nothing needs installing: click **Sign in to
  GitHub** in the panel and enter the code it shows (OAuth device flow). If the
  [GitHub CLI](https://cli.github.com/) is already authenticated, or `GH_TOKEN` /
  `GITHUB_TOKEN` is set, those are used first and there is nothing to do. Works with
  personal and org-assigned seats alike; the same binary adapts to whichever it finds.

Each panel degrades independently: if one source is unavailable the other still works. A
service that is missing entirely has its panel hidden and the window shrinks to fit.

## Build

**Windows, portable single file:**

```powershell
.\build-windows.ps1
```

or directly:

```bash
dotnet publish TokenBurnRate.csproj -c Release -r win-x64 -o publish/win-x64
```

Either produces one file — `publish\win-x64\TokenBurnRate.exe` (~46 MB). Copy just that
file to the target machine; it needs nothing beside it and no .NET install.

All four platforms at once:

```bash
./build-all.sh
```

Targets: `win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`. Output is one self-contained
executable per target, needing no .NET install on the target machine.

Trimming is intentionally disabled — Avalonia resolves XAML types by reflection, and
trimming breaks that only in published builds, where it is hardest to diagnose.

### Icon

The app icon is a usage bar burning down like a fuse. [Assets/icon.svg](Assets/icon.svg)
is the editable source; [Assets/icon.ico](Assets/icon.ico) is what actually ships, wired
up twice because they are separate mechanisms — `ApplicationIcon` stamps the `.exe`, and
`Window.Icon` loads the embedded `AvaloniaResource` at runtime.

Regenerate the `.ico` and the preview PNGs after editing the SVG:

```bash
python Assets/render-icon.py Assets
```

The renderer is stdlib-only — no ImageMagick or Inkscape needed. It duplicates the SVG
geometry rather than parsing it, so edit both together. Sizes below 40px drop the
hot-core highlight, which turns to mush at that scale.

### macOS / Linux notes

The published binary needs the executable bit after transfer:

```bash
chmod +x TokenBurnRate
```

macOS builds are unsigned, so Gatekeeper quarantines them on first run:

```bash
xattr -d com.apple.quarantine TokenBurnRate
```

## Running it on a work machine

The executable is unsigned, so **SmartScreen will warn on first run** ("Windows protected
your PC"). Choose *More info* → *Run anyway*. If it was downloaded or copied from a network
share, Windows may also mark it blocked — clear that with:

```powershell
Unblock-File .\TokenBurnRate.exe
```

It needs no installer and no admin rights. See [State file](#state-file) for what it
writes.

What each panel needs, and what happens when it is missing:

| Panel | Needs | If unavailable |
| --- | --- | --- |
| Claude | Claude Code signed in on that machine | the whole panel is hidden |
| Copilot | a GitHub sign-in (see below) | panel offers a **Sign in to GitHub** button |

A service that is not present on the machine has its panel hidden entirely and the window
shrinks, rather than showing a column of `n/a`. On a machine with only Copilot, that is all
you see.

**Signing in to Copilot without the GitHub CLI.** The widget resolves a token in this order:

1. `GH_TOKEN` / `GITHUB_TOKEN` environment variable
2. the GitHub CLI, if it happens to be installed (`gh auth token`)
3. a token the widget obtained itself

Nothing needs installing for (3): click **Sign in to GitHub**, and the widget shows a short
code and opens `github.com/login/device`. Enter the code once and it stores the token in
`%APPDATA%\TokenBurnRate\github.json`, readable only by your user. This is GitHub's standard
OAuth device flow — no admin rights, no CLI, no client secret. The requested scope is
`read:user`, the least privilege that still answers the Copilot quota endpoint.

VS Code's own Copilot sign-in is deliberately *not* reused: it is encrypted with the OS
credential store, and reaching into another application's secrets would be both fragile and
inappropriate. That is why VS Code can show Copilot working while the widget still asks you
to sign in.

> **Corporate network note:** the app reads your own usage from `api.anthropic.com` and
> `api.github.com` over HTTPS. If that traffic is proxied or blocked, the affected panel
> shows the error rather than failing silently.

## State file

The app keeps its state in a single JSON file **next to the executable, named after it** -
`TokenBurnRate.exe` writes `TokenBurnRate.json` - so a portable copy carries its spending
history with it:

```json
{
  "window": { "x": 192, "y": 192 },
  "pacing": {
    "day": "2026-09-08",
    "dayOpening": 1961,
    "weekStart": "2026-09-07",
    "weekOpening": 1961
  }
}
```

`pacing` holds the credit balance recorded at the first launch of the day and of the week,
which is what makes the Day and Week bars measurable at all. Deleting the file re-anchors
both to the current balance at the next launch; nothing else is lost.

If the executable's own folder cannot be written - a read-only network share, or
`Program Files` - the file falls back to `%APPDATA%\TokenBurnRate\TokenBurnRate.json`, so
tracking keeps working rather than silently losing each day's opening balance.

The GitHub token is deliberately **not** kept here. It stays in
`%APPDATA%\TokenBurnRate\github.json` with owner-only permissions, because a portable
folder may well be a USB stick or a shared drive.

## Crash reports

An unhandled exception is written to `TokenBurnRate.crash.20261231235959.log` - the
executable's name, then the timestamp - in the same folder as the state file, so a crash
can be sent on rather than lost with the process. Each crash gets its own file, holding
the time, app version, OS and runtime, and the full exception chain including inner
exceptions.

Nothing is written during normal operation, and the app never deletes these files - clear
them out yourself once they are no longer wanted.

> One exception cannot be logged: a `StackOverflowException` terminates the process
> immediately by design, and .NET runs no handler for it. The tray-quit crash that
> produced one has been fixed.

## Staying on top

The widget floats above other windows by default - being visible while you work in an
editor is the point of it. The **pin** button beside the close button toggles that: dimmed
means it behaves like any ordinary window and whatever you launch next will cover it. The
choice is remembered in the state file.

## Closing to the tray

The close button minimises to the notification area rather than exiting, so the widget
keeps tracking in the background. Left-click the tray icon to bring it back; its
right-click menu has **Show** and **Quit**, and Quit is what actually ends the process.

The first time the window disappears this way, a notification says the app is still
running - once only, recorded as `trayNoticeShown` in the state file. Delete that key to
see it again.

Untick **Close button minimises to tray** in the widget's right-click menu to have the
close button exit instead. On a desktop with no system tray the option is hidden entirely
and close always exits, so the button can never become a dead end.

## Starting with the computer

The widget registers itself to launch at login the first time it runs, and the right-click
menu has a **Start with the computer** toggle. Turning it off is remembered - it is not
re-enabled at the next launch.

There is no cross-platform autostart standard, so each OS uses its own conventional
per-user mechanism. **None of them requires administrator rights**:

| OS | Where the entry goes |
| --- | --- |
| Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |
| macOS | `~/Library/LaunchAgents/com.tokenburnrate.plist` |
| Linux | `~/.config/autostart/TokenBurnRate.desktop` (XDG spec, honours `XDG_CONFIG_HOME`) |

`HKCU` is the current user's own registry hive; only the machine-wide `HKLM` equivalent
needs elevation. Removing the entry is all it takes to undo, and turning the toggle off
does exactly that.

Autostart is offered only for a published build. Under `dotnet run` the running executable
is the .NET host rather than the widget, so registering it would launch the wrong thing.

## Usage

Drag the header to move; position is remembered between runs. `⟳` refreshes immediately,
`✕` closes. It refreshes on its own every 60 seconds.

Claude's transcripts total hundreds of MB, but only ~0.1% changes per day, so files are
parsed incrementally from a byte offset: the first read takes ~2.8s and every refresh after
that ~13ms.
