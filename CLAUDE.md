# TokenBurnRate

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
  (Bearer token from `~/.claude/.credentials.json`, header `anthropic-beta: oauth-2025-04-20`)
  returns the same utilization claude.ai shows. Transcripts cannot reproduce it: the ceiling
  is unpublished and the limits reset at fixed times rather than rolling. Never write to the
  credentials file — Claude Code owns and refreshes it.
- **Trimming must stay off.** Avalonia resolves XAML by reflection; trimming breaks it only
  in published builds, where it is hardest to diagnose.
- `Grid.ColumnDefinitions` cannot be bound in Avalonia — the bars are a custom-drawn
  `UsageBar` control ([Views/UsageBar.cs](Views/UsageBar.cs)) that derives fill from its own
  measured width.
- **`vpk pack` needs an unpacked publish folder, not a single-file exe.** That's why the
  csproj carries no `PublishSingleFile` — see [Update mechanism](#update-mechanism) for what
  replaced it. Don't re-add single-file publishing without also removing Velopack.

## Update mechanism

Packaging switched from a raw self-contained single-file exe per OS to a
[Velopack](https://velopack.io)-packaged installer per OS, so the app can auto-update from
GitHub Releases. Full rationale and the tradeoffs behind it:
[.claude/memory/velopack-auto-update.md](.claude/memory/velopack-auto-update.md).

- `Program.cs` runs `VelopackApp.Build().Run()` before anything else — required even in
  dev builds (`dotnet run`), where it's a no-op since Velopack finds no installed location.
- `Services/UpdateService.cs` checks GitHub Releases on every launch and, if a newer
  version exists, downloads and applies it **silently**, then restarts — no dialog, no
  menu interaction. This was a deliberate choice over a "click to install" flow.
- By default only real (bare `X.Y.Z`) versions are offered. `--update-include-beta` widens
  that to also accept `-beta.N` builds; `--update-include-alpha` widens it further to also
  accept `-alpha.N` (there is deliberately no `-rc` tier — CI's version scheme only ever
  produces alpha, beta, or a bare release). Each flag includes everything at least as
  stable as it names — alpha implies beta implies release. Since every release so far is an
  alpha, a default launch currently has nothing to update to until the first bare X.Y.Z
  ships — that's expected, not a bug.
- CI ([.github/workflows/build-and-release.yml](.github/workflows/build-and-release.yml))
  runs `dotnet publish` into an unpacked folder per RID, then `vpk pack` turns that into
  the single downloadable file users actually get (`Setup.exe` / `.AppImage` / `Setup.pkg`),
  plus the `.nupkg` and `releases.*.json`/`assets.*.json` feed files the update check reads.
  The macOS `.icns` icon is generated at CI time via `iconutil` from the existing
  `Assets/icon-*.png` set — there's no separate `.icns` source file to maintain.

## Build

```bash
./build-all.sh    # publishes the unpacked folder per OS - vpk pack's input, not a final exe
dotnet build      # local debug
```

`build-all.sh`'s output is not what end users download; the CI workflow above is what
produces the actual installer via `vpk pack`. There is no local one-liner for that step —
run it by hand (`vpk pack --packId TokenBurnRate ...`) against a `publish/<rid>` folder if
you need to test packaging locally.
