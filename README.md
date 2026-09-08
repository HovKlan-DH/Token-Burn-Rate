# Token Burn Rate

A small always-on-top desktop widget showing live AI usage for **Claude Code** and
**GitHub Copilot**, as a single portable executable for Windows, Linux and macOS.

![widget](docs/screenshot.png)

## What it shows

### Claude — 5 hour / week / month
Read from Claude Code's local transcripts (`~/.claude/projects/**/*.jsonl`). Every
assistant message records exact token usage, so these are real numbers, computed offline
with no API call and no credentials.

The **5 hour** window is first on purpose: Claude Code enforces a rolling 5-hour session
limit, so that is the bar that predicts an actual cutoff. A calendar "today" bar would not.

Bars show `input + output + cache-creation` tokens. Cache *reads* are excluded from the
headline figure — they are billed at a fraction of the input rate and run ~100x larger than
everything else, so including them would flatten the bars into noise.

### GitHub Copilot — completions / chat / premium
Read live from GitHub's `copilot_internal/user` endpoint, authenticated with the token from
your existing `gh` CLI login. These are **monthly** quotas that reset on a fixed date.

Copilot deliberately does *not* get today/week/month bars: the endpoint returns only a
point-in-time snapshot, and nothing on disk records history, so shorter windows cannot be
derived. A quota not included in your plan (e.g. premium interactions on the free tier) is
shown dimmed rather than as a misleading empty bar.

## Bar denominators

Anthropic publishes no per-plan token ceiling, so Claude's bars auto-calibrate: the heaviest
window of that size ever seen in your history becomes 100%. Each bar reads "how heavy is
this window against my own record", and adjusts as your habits change. Copilot's bars use
the real entitlement reported by GitHub.

## Requirements

- **Claude panel** — Claude Code installed, with transcripts in `~/.claude/projects`.
  Honours `CLAUDE_CONFIG_DIR`.
- **Copilot panel** — [GitHub CLI](https://cli.github.com/) installed and authenticated
  (`gh auth login`). `GH_TOKEN` / `GITHUB_TOKEN` are used first if set.

Each panel degrades independently: if one source is unavailable the other still works, and
the reason is shown in place of the bars.

## Build

```bash
./build-all.sh            # all four targets
```

or one platform:

```bash
dotnet publish Token-Burn-Rate.csproj -c Release -r win-x64 -o publish/win-x64
```

Targets: `win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`. Output is one self-contained
executable (~48 MB) needing no .NET install on the target machine.

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

## Usage

Drag the header to move; position is remembered between runs. `⟳` refreshes immediately,
`✕` closes. It refreshes on its own every 60 seconds.

Claude's transcripts total hundreds of MB, but only ~0.1% changes per day, so files are
parsed incrementally from a byte offset: the first read takes ~2.8s and every refresh after
that ~13ms.
