# Token Burn Rate

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
- **Copilot panel** — [GitHub CLI](https://cli.github.com/) installed and authenticated
  (`gh auth login`). `GH_TOKEN` / `GITHUB_TOKEN` are used first if set. Works with personal
  and org-assigned seats alike; the same binary adapts to whichever it finds.

Each panel degrades independently: if one source is unavailable the other still works, and
the reason is shown in place of the bars.

## Build

**Windows, portable single file:**

```powershell
.\build-windows.ps1
```

or directly:

```bash
dotnet publish Token-Burn-Rate.csproj -c Release -r win-x64 -o publish/win-x64
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

It needs no installer and no admin rights, and writes only
`%APPDATA%\TokenBurnRate\window.json` (the saved window position).

What each panel needs, and what happens when it is missing:

| Panel | Needs | If unavailable |
| --- | --- | --- |
| Claude | Claude Code signed in on that machine | panel states why, e.g. "not signed in" |
| Copilot | `gh auth login` on that machine | panel states "Not signed in. Run: gh auth login" |

The panels are independent, so if only one of the two AIs is set up at work the other simply
reports why and the app still runs. Both read credentials that already exist on the machine;
the app stores none of its own and writes to neither.

> **Corporate network note:** the app reads your own usage from `api.anthropic.com` and
> `api.github.com` over HTTPS. If that traffic is proxied or blocked, the affected panel
> shows the error rather than failing silently.

## Usage

Drag the header to move; position is remembered between runs. `⟳` refreshes immediately,
`✕` closes. It refreshes on its own every 60 seconds.

Claude's transcripts total hundreds of MB, but only ~0.1% changes per day, so files are
parsed incrementally from a byte offset: the first read takes ~2.8s and every refresh after
that ~13ms.
