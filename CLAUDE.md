# TokenBurnRate

Avalonia always-on-top widget showing live Claude Code and GitHub Copilot usage,
published as one self-contained executable per OS (win-x64, linux-x64, osx-arm64, osx-x64).

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
  (Bearer token from `~/.claude/.credentials.json`, header `anthropic-beta: oauth-2025-04-20`)
  returns the same utilization claude.ai shows. Transcripts cannot reproduce it: the ceiling
  is unpublished and the limits reset at fixed times rather than rolling. Never write to the
  credentials file — Claude Code owns and refreshes it.
- **Trimming must stay off.** Avalonia resolves XAML by reflection; trimming breaks it only
  in published builds, where it is hardest to diagnose.
- `Grid.ColumnDefinitions` cannot be bound in Avalonia — the bars are a custom-drawn
  `UsageBar` control ([Views/UsageBar.cs](Views/UsageBar.cs)) that derives fill from its own
  measured width.

## Build

```bash
./build-all.sh    # all four targets
dotnet build      # local debug
```
