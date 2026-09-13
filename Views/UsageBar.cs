using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using TokenBurnRate.Models;

namespace TokenBurnRate.Views;

/// <summary>
/// A thin progress bar whose fill is derived from its own measured width.
///
/// Avalonia cannot bind Grid.ColumnDefinitions, and a bound Width has no access to the
/// track size, so the fill is drawn directly instead.
/// </summary>
public sealed class UsageBar : Control
{
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<UsageBar, double>(nameof(Fraction));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(Track));

    public static readonly StyledProperty<double> CornerProperty =
        AvaloniaProperty.Register<UsageBar, double>(nameof(Corner), 3d);

    /// <summary>Caption drawn inside the track, e.g. "resets in 2h 48m".</summary>
    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<UsageBar, string?>(nameof(Caption));

    public static readonly StyledProperty<IBrush?> CaptionBrushProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(CaptionBrush));

    /// <summary>
    /// Brush for the runs the caption marked with <see cref="UsageText.Highlight"/>. Unset
    /// leaves those runs the caption's own colour, still set apart by their weight.
    /// </summary>
    public static readonly StyledProperty<IBrush?> CaptionHighlightBrushProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(CaptionHighlightBrush));

    /// <summary>
    /// Fractions (0-1) along the track to draw pacing tick marks at, e.g. the workday
    /// boundaries within the My Pace week bar - spanning the whole week, days still ahead
    /// included, not only those elapsed so far. The entry at <see cref="TodayMarkerIndex"/>
    /// is drawn as "today" - see <see cref="MarkerAccentBrush"/> - the rest as discrete,
    /// muted ticks regardless of whether they fall before or after it.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> MarkersProperty =
        AvaloniaProperty.Register<UsageBar, IReadOnlyList<double>?>(nameof(Markers));

    /// <summary>
    /// Index into <see cref="Markers"/> of "today". Negative means there is no "today" to
    /// mark - a window that has not started yet - and every tick draws muted.
    /// </summary>
    public static readonly StyledProperty<int> TodayMarkerIndexProperty =
        AvaloniaProperty.Register<UsageBar, int>(nameof(TodayMarkerIndex), -1);

    /// <summary>Brush for the "today" marker - see <see cref="TodayMarkerIndex"/>.</summary>
    public static readonly StyledProperty<IBrush?> MarkerAccentBrushProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(MarkerAccentBrush));

    static UsageBar()
    {
        AffectsRender<UsageBar>(FractionProperty, FillProperty, TrackProperty, CornerProperty,
            CaptionProperty, CaptionBrushProperty, CaptionHighlightBrushProperty,
            MarkersProperty, TodayMarkerIndexProperty, MarkerAccentBrushProperty);

        // Gaining or losing a caption changes how tall the row needs to be - see
        // MeasureOverride - which a render invalidation alone would not pick up.
        AffectsMeasure<UsageBar>(CaptionProperty);
    }

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public double Corner
    {
        get => GetValue(CornerProperty);
        set => SetValue(CornerProperty, value);
    }

    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public IBrush? CaptionBrush
    {
        get => GetValue(CaptionBrushProperty);
        set => SetValue(CaptionBrushProperty, value);
    }

    public IBrush? CaptionHighlightBrush
    {
        get => GetValue(CaptionHighlightBrushProperty);
        set => SetValue(CaptionHighlightBrushProperty, value);
    }

    public IReadOnlyList<double>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public int TodayMarkerIndex
    {
        get => GetValue(TodayMarkerIndexProperty);
        set => SetValue(TodayMarkerIndexProperty, value);
    }

    public IBrush? MarkerAccentBrush
    {
        get => GetValue(MarkerAccentBrushProperty);
        set => SetValue(MarkerAccentBrushProperty, value);
    }


    /// <summary>
    /// The last built caption, with the inputs it was built from.
    ///
    /// Render is on the hot path: the compositor repaints on window moves and on anything
    /// overlapping the acrylic-blurred window, and every Fraction change invalidates all
    /// eight bars. The caption text itself changes at most once a minute, so shaping it per
    /// paint is work thrown away. Rebuilt only when one of its inputs actually differs.
    /// </summary>
    private FormattedText? _cachedText;
    private string? _cachedCaption;
    private IBrush? _cachedBrush;
    private IBrush? _cachedHighlight;
    private double _cachedWidth;

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var caption = Caption;
        var hasCaption = !string.IsNullOrEmpty(caption);

        FormattedText? text = null;
        if (hasCaption)
        {
            var brush = CaptionBrush ?? Brushes.Gray;
            var highlight = CaptionHighlightBrush;

            // Width matters as well as the string: MaxTextWidth drives the ellipsis, so a
            // resized control has to re-trim even when the caption is unchanged.
            if (_cachedText is null
                || !string.Equals(_cachedCaption, caption, StringComparison.Ordinal)
                || !ReferenceEquals(_cachedBrush, brush)
                || !ReferenceEquals(_cachedHighlight, highlight)
                || _cachedWidth != w)
            {
                var (plain, spans) = UsageText.Split(caption!);

                _cachedText = new FormattedText(
                    plain,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default),
                    9,
                    brush)
                {
                    MaxTextWidth = Math.Max(0, w),
                    Trimming = TextTrimming.CharacterEllipsis,
                };

                // Weight carries the emphasis on its own, so a caller that sets no
                // highlight brush still gets readable figures rather than nothing.
                foreach (var (start, length) in spans)
                {
                    _cachedText.SetFontWeight(FontWeight.Bold, start, length);
                    if (highlight is not null)
                        _cachedText.SetForegroundBrush(highlight, start, length);
                }

                _cachedCaption = caption;
                _cachedBrush = brush;
                _cachedHighlight = highlight;
                _cachedWidth = w;
            }

            text = _cachedText;
        }

        // The control is the row's caption strip: the caption sits directly above the fill
        // band, so colour never runs through the words. A bar with no caption keeps the same
        // band in the same place and simply draws no text above it.
        //
        // The band sits near the bottom of the control rather than centred, which lets the
        // row bottom-align the label and the value to line up with the bar itself instead of
        // with the caption above it.
        //
        // It stops short of the very bottom by the today marker's overshoot, so that marker
        // can stand equally proud above and below the band and still sit inside the control:
        // pinned flush, a symmetric marker would have to overflow, and holding it back inside
        // would push it visibly off-centre on the band it marks.
        //
        // The inset is unconditional, not just on bars that draw a marker. Every bar in a
        // panel is the same height, so insetting only some of them would step their bands out
        // of line with each other - the SESSION band sitting 3px below the WEEK one.
        // The band is the same 3px whether or not this bar has a caption. It used to fill the
        // control when the caption was empty, which is reachable in a healthy panel - a plan
        // reporting fewer quota buckets than there are bars leaves the surplus ones Reset,
        // and Reset blanks the caption (see MainViewModel) - so one bar drew as a solid block
        // beside its siblings' thin bands rather than as an empty row of the same shape.
        const double gap = CaptionGap;
        const double bottomInset = TodayMarkerOvershoot;
        const double barHeight = BandHeight;
        var textHeight = text?.Height ?? 0;

        // Pinned to the bottom, never to the caption. An earlier version took the larger of
        // "below the caption" and "bottom-aligned", which quietly handed the marker's
        // reserved space to a caption taller than the control was designed around - the
        // system UI font is whatever the OS supplies (see the Typeface above), so a 13px
        // caption on Linux or macOS pushed the band down and the marker out of the control
        // entirely, with nothing clipping it. Squeezing the caption is the right trade: it
        // already ellipsises, while the marker has nowhere to go.
        var barTop = Math.Max(0, h - barHeight - bottomInset);

        // Measured from the marker's top, not the band's, so the accented tick clears the
        // caption by the same gap the band does. Against the band alone the marker ate the
        // whole gap and its first pixel landed on the caption's last.
        var textTop = Math.Max(0, barTop - TodayMarkerOvershoot - gap - textHeight);
        var radius = Math.Min(Corner, barHeight / 2);

        if (Track is { } track)
            context.DrawRectangle(track, null, new RoundedRect(new Rect(0, barTop, w, barHeight), radius));

        var f = Fraction;
        if (double.IsNaN(f) || double.IsInfinity(f)) f = 0;
        f = f < 0 ? 0 : f > 1 ? 1 : f;

        if (f > 0 && Fill is not null)
        {
            // Keep a visible nub for tiny non-zero values rather than rendering nothing.
            var fillWidth = w * f;
            if (fillWidth < barHeight) fillWidth = barHeight;
            if (fillWidth > w) fillWidth = w;

            context.DrawRectangle(Fill, null,
                new RoundedRect(new Rect(0, barTop, fillWidth, barHeight), radius));
        }

        DrawMarkers(context, w, barTop, barHeight);

        if (text is not null)
            context.DrawText(text, new Point(0, textTop));
    }

    /// <summary>
    /// Discrete tick marks along the track, e.g. the workday boundaries within the My Pace
    /// week bar - spanning the whole week, so ticks for days still ahead sit past the fill
    /// alongside those already behind it. The entry at <see cref="TodayMarkerIndex"/> stands
    /// for "today" and is drawn both taller and thicker, in <see cref="MarkerAccentBrush"/>;
    /// the rest are shorter, muted ticks that read as calendar structure without competing
    /// with the fill.
    ///
    /// The today marker carries its weight through size rather than colour alone: it shares
    /// the red the fill itself turns once the bar is past that marker, so on the run it most
    /// needs to be legible against there is no colour contrast to rely on.
    /// </summary>
    private void DrawMarkers(DrawingContext context, double w, double barTop, double barHeight)
    {
        var markers = Markers;
        if (markers is null || markers.Count == 0) return;

        // Negative means "no today" - see TodayMarkerIndexProperty. Left as-is so it matches
        // no index, rather than folded onto the last entry.
        var todayIndex = TodayMarkerIndex;

        for (var i = 0; i < markers.Count; i++)
        {
            var m = markers[i];
            if (double.IsNaN(m) || double.IsInfinity(m)) continue;
            m = m < 0 ? 0 : m > 1 ? 1 : m;

            var isCurrent = i == todayIndex;

            // Both widths are odd (1 and 3) and a pen is centred on its coordinate, so the
            // half-pixel offset puts either one's edges exactly on pixel boundaries. An even
            // or fractional width would need a whole coordinate instead - which is why the
            // today marker is 3px rather than the 2.5 the step up from 1.5 suggests.
            var x = Math.Round(w * m) + 0.5;

            var tickHeight = isCurrent ? barHeight + TodayMarkerOvershoot * 2 : barHeight;
            var pen = isCurrent ? AccentPen(MarkerAccentBrush) : DiscreteMarkerPen;

            // Centred on the band, never nudged. Render reserves the room below for the
            // overshoot (see bottomInset there), so this stays inside the control without a
            // clamp - and a clamp is what would make the marker sit visibly off-centre on the
            // very band it is marking.
            var y0 = barTop + (barHeight - tickHeight) / 2;

            context.DrawLine(pen, new Point(x, y0), new Point(x, y0 + tickHeight));
        }
    }

    /// <summary>
    /// How far the today marker stands proud of the bar band at each end. It shares its
    /// colour with the fill once the bar runs past it, so the part above and below the band
    /// is the only place it is reliably legible - see <see cref="DrawMarkers"/>.
    /// </summary>
    private const double TodayMarkerOvershoot = 3;

    /// <summary>The fill band's own thickness, under the caption.</summary>
    private const double BandHeight = 3;

    /// <summary>Space between the caption and the top of whatever is drawn below it.</summary>
    private const double CaptionGap = 3;

    /// <summary>
    /// The height the row actually needs: the caption, a gap, and the band with room for the
    /// today marker to stand proud at both ends.
    ///
    /// Measured rather than set in XAML so the two cannot drift apart. The geometry is all
    /// derived from constants in this file, and a template carrying its own Height would go
    /// silently wrong the moment one of them changed - the symptom being a marker drawn
    /// outside the control, or a caption the marker touches.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var caption = Caption;
        var textHeight = string.IsNullOrEmpty(caption) ? 0 : CaptionHeight();

        var height = textHeight
            + (textHeight > 0 ? CaptionGap : 0)
            + TodayMarkerOvershoot * 2
            + BandHeight;

        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, height);
    }

    /// <summary>
    /// The caption's line height at the size <see cref="Render"/> shapes it in. Measured from
    /// a bare typeface rather than the cached FormattedText, which does not exist yet on the
    /// first measure pass and is built against a width this pass is still deciding.
    /// </summary>
    private static double CaptionHeight()
    {
        var probe = new FormattedText(
            "0",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            9,
            Brushes.Gray);
        return probe.Height;
    }

    private static readonly IPen DiscreteMarkerPen =
        new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1);

    /// <summary>
    /// The today marker's pen, cached against its brush. Render is on the hot path and the
    /// accent brush changes only when the user recolours the panel, so allocating a pen per
    /// paint would churn continuously for a value that is almost always the same one.
    /// </summary>
    private IPen? _cachedAccentPen;
    private IBrush? _cachedAccentBrush;

    private IPen AccentPen(IBrush? accent)
    {
        var brush = accent ?? Brushes.OrangeRed;
        if (_cachedAccentPen is null || !ReferenceEquals(_cachedAccentBrush, brush))
        {
            _cachedAccentPen = new Pen(brush, 3);
            _cachedAccentBrush = brush;
        }
        return _cachedAccentPen;
    }
}
