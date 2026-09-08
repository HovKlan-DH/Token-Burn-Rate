using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Token_Burn_Rate.Views;

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

    static UsageBar()
    {
        AffectsRender<UsageBar>(FractionProperty, FillProperty, TrackProperty, CornerProperty);
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

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var r = Corner;

        if (Track is { } track)
            context.DrawRectangle(track, null, new RoundedRect(new Rect(0, 0, w, h), r));

        var f = Fraction;
        if (double.IsNaN(f) || double.IsInfinity(f)) f = 0;
        f = f < 0 ? 0 : f > 1 ? 1 : f;
        if (f <= 0 || Fill is null) return;

        // Keep a visible nub for tiny non-zero values rather than rendering nothing.
        var fillWidth = w * f;
        if (fillWidth < h) fillWidth = h;
        if (fillWidth > w) fillWidth = w;

        context.DrawRectangle(Fill, null, new RoundedRect(new Rect(0, 0, fillWidth, h), r));
    }
}
