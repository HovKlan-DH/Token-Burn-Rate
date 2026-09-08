using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

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

    static UsageBar()
    {
        AffectsRender<UsageBar>(FractionProperty, FillProperty, TrackProperty, CornerProperty,
            CaptionProperty, CaptionBrushProperty);
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
            text = new FormattedText(
                caption!,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default),
                9,
                CaptionBrush ?? Brushes.Gray)
            {
                MaxTextWidth = Math.Max(0, w),
                Trimming = TextTrimming.CharacterEllipsis,
            };
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
