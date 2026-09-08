using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace TokenBurnRate.Views;

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
