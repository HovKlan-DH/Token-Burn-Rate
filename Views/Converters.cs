using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TokenBurnRate.Views;

/// <summary>
/// Turns a colour string into a brush, reusing the same brush for the same string.
///
/// The bars decide their own colours - accent while inside the allowance, red past it -
/// and hand the answer over as a string, so the choice stays in one place instead of being
/// spelled out per template. Reuse matters as much as the parsing: UsageBar caches its
/// shaped caption and drops the cache when the brush identity changes, so a fresh
/// SolidColorBrush per evaluation would re-shape the text on every paint, which is the
/// exact work that cache exists to avoid.
/// </summary>
public sealed class ColourBrushConverter : IValueConverter
{
    /// <summary>Red, chosen to stay legible on the dark background the widget uses.</summary>
    public static readonly IBrush Over = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));

    /// <summary>
    /// One brush per colour string, kept for the life of the process. The set is the
    /// handful of literals the view models name, so holding them costs nothing.
    /// </summary>
    private static readonly ConcurrentDictionary<string, IBrush> _brushes = new(StringComparer.Ordinal);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? _brushes.GetOrAdd(s, Parse) : Over;

    /// <summary>
    /// An unparseable colour falls back to the over-budget red rather than to null: null
    /// draws no fill and no text at all, which reads as a bar sitting at zero rather than
    /// as the mistake it is.
    /// </summary>
    private static IBrush Parse(string s)
        => Color.TryParse(s, out var c) ? new SolidColorBrush(c) : Over;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Dims a row that has no data (e.g. a quota not included in the plan).</summary>
public sealed class DimOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? 1.0 : 0.4;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Chevron glyph for a collapsible section header.</summary>
public sealed class ChevronConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool expanded && expanded ? "\u25BC" : "\u25B6";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Drops the "click to collapse" tooltip from a header that cannot collapse.</summary>
public sealed class HeaderTipConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool solo && solo ? null : "Click to collapse or expand";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
