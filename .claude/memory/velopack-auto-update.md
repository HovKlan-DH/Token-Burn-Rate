---
name: velopack-auto-update
description: "TokenBurnRate uses Velopack for auto-update, mirroring Classic Repair Toolbox - packaging shape and tradeoffs"
metadata:
  type: project
---

TokenBurnRate switched from raw self-contained single-file exes to Velopack-packaged
installers (one per OS), added 2026-09-09, mirroring the sibling Classic-Repair-Toolbox
project's setup (Velopack 1.2.0, `GithubSource` against `HovKlan-DH/TokenBurnRate`).

**The packaging shape changed, not just added a feature.** Velopack's `vpk pack` takes an
*unpacked* publish folder as input and produces the single downloadable file itself
(`TokenBurnRate-win-Setup.exe`, `TokenBurnRate.AppImage`, `TokenBurnRate-osx-<arch>-Setup.pkg`).
So `PublishSingleFile`/`IncludeNativeLibrariesForSelfExtract`/`EnableCompressionInSingleFile`
were removed from the csproj - they're incompatible with what vpk needs as input. The "one
file only" promise moved from "the installed binary is one file" to "the thing you download
and run once is one file" - the user explicitly chose this tradeoff over staying
single-file-forever with no auto-update.

**Update flow is silent/automatic, not notify-and-prompt.** `Services/UpdateService.cs`
checks on every launch (hooked into `MainWindow.axaml.cs`'s `Opened` handler, alongside
`CheckInService.PingHome()`), and if found, downloads and calls
`ApplyUpdatesAndRestart` immediately - no dialog, no menu interaction. A Windows-only
`TrayNotifier` balloon announces the restart where supported. This was a deliberate choice
over a "click to install" tray-menu flow - the user picked "set and forget" explicitly when
asked.

**Why:** the user wanted GitHub-release-based auto-update (like Classic Repair Toolbox) while
keeping distribution as close to "one file" as this app's four-OS matrix allows, and
preferred zero-click updates for a background widget over an interactive prompt.

**Two things the versioned install folder breaks, both since fixed.** A Velopack install runs
the exe from a versioned `app-x.y.z` folder that every update replaces wholesale, so anything
keyed off `Environment.ProcessPath` silently loses its target on each update:

- `Services/AppState.cs` would have written the state file beside that exe and lost all
  pacing history per update — it now detects the install (a `.velopack` directory one level
  up) and uses `%APPDATA%` instead.
- `Services/AutostartService.cs` registered the versioned exe path, which would stop
  resolving after an update — it now registers Velopack's stable stub one level up.

Any future code that resolves a path from `Environment.ProcessPath` needs the same care.

**`GithubSource` must be constructed with `prerelease: true`.** Every release this project
has shipped is an alpha, and the workflow deletes superseded pre-releases, so the newest
pre-release *is* the newest build. With `prerelease: false` the update check silently finds
nothing, forever, until a bare `X.Y.Z` ships.

**How to apply:** Any future packaging change must keep `vpk pack`'s per-OS invocation
in [.github/workflows/build-and-release.yml](../../.github/workflows/build-and-release.yml)
in sync with the csproj's publish properties - the folder vpk consumes must stay unpacked.
The macOS `.icns` icon is generated at CI time via `iconutil` from the existing PNG set
(`Assets/icon-*.png`) rather than committed as a new binary asset - regenerate via
`Assets/render-icon.py` if the icon design changes, no separate `.icns` source to maintain.
Local dev builds (`build-all.sh`, `dotnet run`) are unaffected by Velopack - `VelopackApp.Build().Run()`
in [Program.cs](../../Program.cs) is a no-op restart-trigger when the app isn't running from an
installed Velopack location, so `dotnet run` still works exactly as before.
