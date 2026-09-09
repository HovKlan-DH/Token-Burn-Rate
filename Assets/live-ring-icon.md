# Live ring tray icon — implementation spec

Draw the TokenBurnRate tray icon at runtime as a "quota ring": a filled arc showing one
bar's utilisation, in that panel's own accent colour, redrawn when the figure changes.

The application icon itself does **not** change. `ApplicationIcon` stamps the exe and
`Window.Icon` loads the embedded `AvaloniaResource`, both fixed at build time
(`README.md` → *Icon*). Only `TrayIcon.Icon` is assignable at runtime, so this feature is
scoped to the notification-area icon.

---

## 1. Geometry

All values in a 256×256 design space, scaled down per output size. This matches the
concept board and the existing icon's palette (`Assets/icon.svg`).

| Element | Value |
| --- | --- |
| Tile | rounded rect, 0,0,256,256, corner radius 56, fill `#1A1B1E` |
| Track ring | circle centre 128,128, radius 86, stroke width 26, stroke `#3A3D44` |
| Used arc | same circle, stroke width 26, round cap, starts at 12 o'clock, sweeps clockwise `fraction × 360°` |
| Flame (centre) | path below, scaled 0.5 and translated (64, 67) |

Flame path (256-space, before the 0.5 scale):

```
M128 22
C 152 74, 188 96, 188 142
C 188 184, 161 214, 128 214
C 95 214, 68 184, 68 142
C 68 110, 92 98, 102 64
C 112 88, 122 78, 128 22 Z
```

### Small-size cut

The flame turns to mush below ~24 px — the same limit `Assets/render-icon.py` already
notes for the hot core. For 16 px and 20 px, render the tray cut instead:

| Element | Value |
| --- | --- |
| Track ring | radius 88, stroke width 34, stroke `#3A3D44` |
| Used arc | radius 88, stroke width 34, **square** cap (a round cap swallows small arcs) |
| Centre | solid circle, radius 34, in the arc colour — replaces the flame |

Sizes to render: 16, 20, 24, 32, 40, 48 (Windows picks from the set; macOS/Linux take the
largest). 16/20 use the tray cut, 24 and up use the flame.

### Colour

Colour comes from the bar being tracked, not from a table in the renderer. `BarViewModel`
already resolves it:

```csharp
public string FillColour => _isOverBudget ? OverColour : Accent;
```

so `FillColour` gives the panel accent normally and `OverColour` (`#F85149`) past 100%,
with no extra logic here. The accents it draws from, for reference
(`ViewModels/MainViewModel.cs`):

| Constant | Value | Panel |
| --- | --- | --- |
| `ClaudeAccent` | `#D97757` | Claude |
| `CopilotAccent` | `#58A6FF` | GitHub Copilot |
| `PacingAccent` | `#3FB950` | Copilot : My Pace |
| `OverColour` | `#F85149` | any bar at or past 100% |
| `MutedColour` | `#6B7079` | source unavailable (see §5) |

Tile `#1A1B1E` and track `#3A3D44` stay constant in every variant, so the icon still reads
as the same app whatever it is tracking. Both the arc and the flame/centre dot take the
colour.

---

## 2. Rendering

No new dependency. `Views/UsageBar.cs` already draws its own fill by overriding
`Render(DrawingContext)` and calling `context.DrawRectangle(brush, null, new RoundedRect(…))`;
this is the same drawing API, into an offscreen target.

```csharp
// Services/TrayIconRenderer.cs (new)
private static Bitmap Render(int size, double fraction, string colourHex)
{
    var pixel = new PixelSize(size, size);
    var dpi = new Vector(96, 96);
    using var target = new RenderTargetBitmap(pixel, dpi);

    using (var ctx = target.CreateDrawingContext())
    {
        var s = size / 256.0;                       // design space → output
        var accent = Color.Parse(colourHex);

        // tile
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#1A1B1E")), null,
            new RoundedRect(new Rect(0, 0, size, size), 56 * s));

        var big = size >= 24;
        var radius = (big ? 86 : 88) * s;
        var width  = (big ? 26 : 34) * s;
        var centre = new Point(size / 2.0, size / 2.0);

        // track
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#3A3D44")), width),
            centre, radius, radius);

        // used arc: an ArcSegment in a StreamGeometry, 12 o'clock clockwise
        if (fraction > 0)
        {
            var pen = new Pen(new SolidColorBrush(accent), width)
            {
                LineCap = big ? PenLineCap.Round : PenLineCap.Square,
            };
            ctx.DrawGeometry(null, pen, ArcGeometry(centre, radius, fraction));
        }

        // centre: flame above 24px, solid dot below
        if (big) ctx.DrawGeometry(new SolidColorBrush(accent), null, FlameGeometry(s));
        else ctx.DrawEllipse(new SolidColorBrush(accent), null, centre, 34 * s, 34 * s);
    }

    // RenderTargetBitmap is not a Bitmap the TrayIcon can keep; round-trip it.
    using var stream = new MemoryStream();
    target.Save(stream);
    stream.Position = 0;
    return new Bitmap(stream);
}
```

Notes for whoever writes this:

- A full ring at `fraction >= 1` should be drawn as a plain `DrawEllipse` stroke, not an
  arc — a 360° `ArcSegment` degenerates to nothing.
- Clamp `fraction` to 0..1 for the geometry (`UsageBar.Render` does the same), but keep the
  unclamped percentage for the over-budget colour and the tooltip.
- Cache the `SolidColorBrush`/`Pen` per colour; this runs on the UI thread.
- Rendering must happen on the UI thread (`Dispatcher.UIThread`), like all Avalonia drawing.

### Handing it to the tray

`MainWindow.SetUpTray()` currently does:

```csharp
_tray = new TrayIcon
{
    Icon = Icon,
    ToolTipText = "TokenBurnRate",
    IsVisible = true,
    Menu = new NativeMenu { toggle, exit },
};
```

Add an update method called after each refresh:

```csharp
private int _lastIconPercent = -1;
private string? _lastIconColour;

private void UpdateTrayIcon()
{
    if (_tray is null) return;

    var source = _vm.ResolveIconState().Source;      // see §3, may be null
    if (source is null) { _tray.Icon = Icon; _tray.ToolTipText = "TokenBurnRate"; return; }

    var percent = (int)Math.Round(source.Percent, MidpointRounding.AwayFromZero);
    if (percent == _lastIconPercent && source.FillColour == _lastIconColour) return;

    _lastIconPercent = percent;
    _lastIconColour = source.FillColour;

    _tray.Icon = new WindowIcon(Services.TrayIconRenderer.Render(
        Math.Clamp(source.Fraction, 0, 1), source.FillColour));
    _tray.ToolTipText = $"TokenBurnRate — {source.Label} {percent}%";
}
```

- Call it from the same paths that update the bars: the `Opened` initial refresh, the
  `_timer` tick, and the refresh button — all three already funnel through
  `RunSafely(() => _vm.RefreshAsync(_cts.Token), …)`, so the cleanest hook is one
  continuation there, or a `_vm.PropertyChanged` subscription on the source bar.
- Guard on the rounded percentage **and** the colour, as above: a 60-second poll must not
  repaint the shell when nothing visible changed.
- Keep updating while the window is hidden. `HideToTray` deliberately leaves `_timer`
  running ("the poll cadence belongs to the app, not to whether anyone is looking"), and
  the tray icon is the only thing on screen at that point — this is the feature's main use.
- Dispose the previous `Bitmap` after assigning the new `WindowIcon`, or the handles
  accumulate one per change.
- `SetUpTray` is inside a `try/catch` that sets `_vm.TraySupported = false` on desktops
  with no tray. `UpdateTrayIcon` must no-op in that case (`_tray is null`), which the guard
  above already does.

---

## 3. Configuration

The source and the colour are user choices. Follow the pattern `refreshSeconds` already
uses in `Services/AppState.cs`: a key in the state file, written back with its default so
it is visible to edit, with no UI.

```json
{
  "icon": {
    "source": "max",
    "colour": "accent"
  }
}
```

```csharp
// Services/AppState.cs
[JsonPropertyName("icon")]
public IconState? Icon { get; set; }

public sealed class IconState
{
    /// <summary>Which bar the tray ring follows; "max" for whichever is highest.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "max";

    /// <summary>"accent" to take the source panel's colour, or an explicit #RRGGBB.</summary>
    [JsonPropertyName("colour")] public string Colour { get; set; } = "accent";
}
```

`source` values, resolved against the collections the view model already publishes
(`ClaudeBars`, `CopilotBars`, `PacingBars` — all `ObservableCollection<BarViewModel>`):

| Value | Bar |
| --- | --- |
| `max` (default) | highest `Fraction` among all visible, enabled bars |
| `claude.session` / `claude.week` | the matching Claude bar |
| `copilot.completions` / `copilot.chat` / `copilot.premium` | the matching Copilot quota bar |
| `pacing.day` / `pacing.week` / `pacing.month` | `PacingBars[0..2]` |

Expose the resolution as one method, so the renderer never walks the collections itself.
It also has to answer which named sources currently resolve (for the tray menu), so
resolve both in one pass rather than re-running the same scan per question:

```csharp
public (IReadOnlyDictionary<string, bool> Available, BarViewModel? Source) ResolveIconState()
```

`colour`: `"accent"` (default) means use the source bar's `FillColour`, which already
handles the over-budget flip. An explicit `#RRGGBB` overrides the accent but **not** the
over-budget colour — past 100% the ring goes `#F85149` whatever the setting says. Reject
anything that is not `accent` or a valid hex and fall back to `accent`, the way
`refreshSeconds` clamps and writes back its corrected value.

Claude's plan decides which limits exist ("whichever limits your plan has are rendered"),
so a named Claude bar may not exist on a given machine — that is the fallback case, not an
error. Consider mirroring the setting in the right-click menu later; the state file alone
is enough for the first cut.

---

## 4. Which percentage, by default

`max` — the highest utilisation across whatever panels are live. It is the number that
answers "am I about to run out", it is defined on every machine whichever panel is hidden
or absent, and the tooltip disambiguates it (`Claude week 40%`). The alternative default,
`pacing.day`, answers a different question ("can I spend more today") and only exists when
a plan meters spend at all — the My Pace panel "hides itself when a plan meters nothing".

---

## 5. Edge cases

| Case | Behaviour |
| --- | --- |
| Named source bar absent (no Claude sign-in, quota not in plan) | fall back to `max`; never blank the icon |
| No bars at all (both panels unavailable) | keep the static `Window.Icon` — do not draw an empty ring |
| Source bar present but `IsEnabled == false` (dimmed, not in plan) | skip it in `max`; if named explicitly, fall back to `max` |
| Unlimited quota (`∞`) | not a percentage — exclude from `max`, and fall back if named |
| Over 100% | ring full, colour `#F85149`, tooltip keeps the real number (it can exceed 100) |
| `fraction` NaN/∞ | treat as 0, as `UsageBar.Render` does |
| No tray on this desktop | `_tray` is null; do nothing |
| macOS menu bar / Linux panel | same `WindowIcon` works, but sizes differ and a monochrome menu bar flattens the colour. Ship the static `.ico` as the app identity; treat the live ring as a tray enhancement |
| Crash while rendering | wrap the update in the existing `try/catch` + `Services.CrashLog.Record` style; a failed icon must never take down a monitor |

---

## 6. Static icon

The exe/window icon stays a fixed, representative fill. If you want it to match the new
mark, update `Assets/icon.svg` and the duplicated geometry in `Assets/render-icon.py`
together (the renderer "duplicates the SVG geometry rather than parsing it"), then
regenerate:

```bash
python Assets/render-icon.py Assets
```

Suggested static fill: 58% in `ClaudeAccent`, which reads as "in use" without looking
alarming.

---

## 7. Acceptance checklist

- [ ] Tray icon shows a ring matching the configured bar's percentage, within one poll.
- [ ] Ring colour matches that panel's accent; goes `#F85149` past 100%.
- [ ] Tooltip names the bar and its percentage.
- [ ] Icon repaints only when the rounded percentage or the colour changes.
- [ ] Legible at 16 px on a Windows tray, both light and dark taskbars: ring, fill and dot
      all distinguishable.
- [ ] Keeps updating while the window is hidden in the tray.
- [ ] `"icon"` written to the state file with its defaults on first run; an invalid value is
      corrected in the file rather than overruled silently.
- [ ] No leaked bitmaps over a long session (assign, then dispose the previous).
- [ ] Machine with only Copilot, and machine with only Claude, both behave.
- [ ] No tray (Linux desktop without one): app runs unchanged, nothing thrown.
