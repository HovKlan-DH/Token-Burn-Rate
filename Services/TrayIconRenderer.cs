using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TokenBurnRate.Services;

/// <summary>
/// Draws the tray icon as a "quota ring": a filled arc showing one bar's utilisation, in
/// that panel's own accent colour. See Assets/live-ring-icon.md for the design spec.
/// </summary>
public static class TrayIconRenderer
{
    private const string TileColour = "#1A1B1E";
    private const string TrackColour = "#3A3D44";

    /// <summary>
    /// A single render size. WindowIcon takes one bitmap and every backend scales it as
    /// needed, so there is no benefit to rendering the full 16-48 set the spec's design
    /// space enumerates. Rendered at the small-size cut (see RenderPng): a Windows tray
    /// icon displays at 16px, and a downscaled render of the flame variant confirmed the
    /// spec's own warning - the flame collapses into an indistinct blob and the thinner
    /// ring aliases into a rough smudge at that size. The small-size cut's thicker
    /// stroke and plain dot were designed for exactly this size and hold up far better.
    /// </summary>
    private const int Size = 20;

    private static readonly Dictionary<string, IBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    private static IBrush BrushFor(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var cached)) return cached;
        var brush = new SolidColorBrush(Color.Parse(hex));
        BrushCache[hex] = brush;
        return brush;
    }

    /// <summary>
    /// Renders the ring into one <see cref="WindowIcon"/>. Must run on the UI thread, like
    /// all Avalonia drawing.
    /// </summary>
    public static WindowIcon Render(double fraction, string colourHex)
    {
        if (double.IsNaN(fraction) || double.IsInfinity(fraction)) fraction = 0;
        fraction = Math.Clamp(fraction, 0, 1);

        // The stream is deliberately not disposed inside this method and not shared: a
        // WindowIcon is not documented to consume its stream eagerly, and a tray icon is
        // re-rasterised on a DPI change - long after any `using` here would have closed it,
        // which would surface as ObjectDisposedException from inside the shell's icon path
        // and a tray icon that silently stops updating. MemoryStream holds no unmanaged
        // handle, so letting the icon own it costs nothing but the bytes it already needs.
        var stream = new MemoryStream(RenderPng(Size, fraction, colourHex), writable: false);
        return new WindowIcon(stream);
    }

    /// <summary>Renders the ring and encodes it to PNG bytes.</summary>
    private static byte[] RenderPng(int size, double fraction, string colourHex)
    {
        var pixel = new PixelSize(size, size);
        var dpi = new Vector(96, 96);
        using var target = new RenderTargetBitmap(pixel, dpi);

        using (var ctx = target.CreateDrawingContext())
        {
            var s = size / 256.0;
            var accentBrush = BrushFor(colourHex);

            ctx.DrawRectangle(BrushFor(TileColour), null,
                new RoundedRect(new Rect(0, 0, size, size), 56 * s));

            var big = size >= 24;
            var radius = (big ? 86 : 88) * s;
            var width = (big ? 26 : 34) * s;
            var centre = new Point(size / 2.0, size / 2.0);

            ctx.DrawEllipse(null, new Pen(BrushFor(TrackColour), width), centre, radius, radius);

            if (fraction > 0)
            {
                if (fraction >= 1)
                {
                    // A 360-degree ArcSegment degenerates to nothing, so a full ring is a
                    // plain stroked circle instead.
                    ctx.DrawEllipse(null, new Pen(accentBrush, width), centre, radius, radius);
                }
                else
                {
                    var pen = new Pen(accentBrush, width)
                    {
                        LineCap = big ? PenLineCap.Round : PenLineCap.Square,
                    };
                    ctx.DrawGeometry(null, pen, ArcGeometry(centre, radius, fraction));
                }
            }

            if (big) ctx.DrawGeometry(accentBrush, null, FlameGeometry(s));
            else ctx.DrawEllipse(accentBrush, null, centre, 34 * s, 34 * s);
        }

        // Encoded once, straight to bytes: decoding the PNG back into a Bitmap only to
        // re-encode it for the WindowIcon was a round trip with no effect on the result.
        // Avalonia 12 deprecated Save(Stream, int? quality) in favour of an options object;
        // PngBitmapEncoderOptions with its default CompressionLevel matches what the old
        // quality-less call already did.
        using var stream = new MemoryStream();
        target.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    /// <summary>An open arc starting at 12 o'clock, sweeping clockwise fraction * 360 degrees.</summary>
    private static StreamGeometry ArcGeometry(Point centre, double radius, double fraction)
    {
        var angle = fraction * 360.0;
        var startPoint = PointOnCircle(centre, radius, 0);
        var endPoint = PointOnCircle(centre, radius, angle);
        var isLargeArc = angle > 180.0;

        var geometry = new StreamGeometry();
        using var gc = geometry.Open();
        gc.BeginFigure(startPoint, isFilled: false);
        gc.ArcTo(endPoint, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise);
        gc.EndFigure(false);
        return geometry;
    }

    /// <summary>Point on a circle at <paramref name="degrees"/> clockwise from 12 o'clock.</summary>
    private static Point PointOnCircle(Point centre, double radius, double degrees)
    {
        var radians = (degrees - 90) * Math.PI / 180.0;
        return new Point(centre.X + radius * Math.Cos(radians), centre.Y + radius * Math.Sin(radians));
    }

    /// <summary>The flame mark, scaled 0.5 and translated (64, 67) as specified.</summary>
    private static StreamGeometry FlameGeometry(double s)
    {
        var geometry = new StreamGeometry();
        using var gc = geometry.Open();

        Point P(double x, double y) => new((64 + x * 0.5) * s, (67 + y * 0.5) * s);

        gc.BeginFigure(P(128, 22), isFilled: true);
        gc.CubicBezierTo(P(152, 74), P(188, 96), P(188, 142));
        gc.CubicBezierTo(P(188, 184), P(161, 214), P(128, 214));
        gc.CubicBezierTo(P(95, 214), P(68, 184), P(68, 142));
        gc.CubicBezierTo(P(68, 110), P(92, 98), P(102, 64));
        gc.CubicBezierTo(P(112, 88), P(122, 78), P(128, 22));
        gc.EndFigure(true);

        return geometry;
    }
}
