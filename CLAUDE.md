# Token Burn Rate

Avalonia always-on-top widget showing live Claude Code and GitHub Copilot usage,
published as one self-contained executable per OS (win-x64, linux-x64, osx-arm64, osx-x64).

## Project notes

Durable findings live in [.claude/memory/](.claude/memory/) — read
[MEMORY.md](.claude/memory/MEMORY.md) there for the index. Two that shape the design:

- The app runs on **two machines** (home: free personal Copilot plan; work: org-assigned
  seat). One binary adapts at runtime — never generalise the account shape from whichever
  machine is in front of you.
- **M365 Copilot is deliberately not shown.** Separate product and separate pool from
  GitHub Copilot, but no readable per-user quota exists. Do not re-research this.

## Gotchas

- **Claude transcripts repeat each message.** A streamed assistant message is appended up
  to 4x, so records must be de-duplicated by `message.id`. Summing naively roughly doubles
  every total.
- **Parse incrementally.** The transcript corpus is ~280MB but only ~0.1% changes per day;
  files resume from a byte offset. Cold ~2.8s, warm ~13ms.
- **Cache reads are excluded** from the headline token figure — they run ~100x larger than
  other token classes and would flatten the bars.
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
