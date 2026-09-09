using System;
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

    static UsageBar()
    {
        AffectsRender<UsageBar>(FractionProperty, FillProperty, TrackProperty, CornerProperty,
            CaptionProperty, CaptionBrushProperty, CaptionHighlightBrushProperty);
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

        // Without a caption this is a plain slim bar. With one, the control becomes the
        // row's caption strip: the caption sits directly above the fill band, so colour
        // never runs through the words.
        //
        // The band is pinned to the bottom of the control rather than centred, which lets
        // the row bottom-align the label and the value to line up with the bar itself
        // instead of with the caption above it.
        const double gap = 3;
        var barHeight = hasCaption ? 3.0 : h;
        var textHeight = text?.Height ?? 0;

        var barTop = hasCaption ? Math.Max(textHeight + gap, h - barHeight) : 0;
        var textTop = Math.Max(0, barTop - gap - textHeight);
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

        if (text is not null)
            context.DrawText(text, new Point(0, textTop));
    }

}
