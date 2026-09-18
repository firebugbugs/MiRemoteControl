using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MiRemoteControl.Desktop.Infrastructure;

/// <summary>
/// Builds the 34×34 rounded tile shown for a plugin. A package icon (PNG
/// declared in plugin.json) is clipped into the tile; without one the tile
/// falls back to the plugin name's first letter.
/// </summary>
internal static class PluginIconView
{
    private const double Size = 34;
    private const double Radius = 9;

    public static IImage? Decode(byte[]? pngBytes)
    {
        if (pngBytes is not { Length: > 8 }) return null;
        try
        {
            using var stream = new MemoryStream(pngBytes);
            return new Bitmap(stream);
        }
        catch
        {
            // A corrupted icon must not take down plugin surfaces; the letter
            // fallback covers it.
            return null;
        }
    }

    public static Control Create(IImage? icon, string fallbackName)
    {
        if (icon is null)
        {
            return new Border
            {
                Width = Size,
                Height = Size,
                CornerRadius = new CornerRadius(Radius),
                Background = new SolidColorBrush(Color.Parse("#5A4BCB")),
                Child = new TextBlock
                {
                    Text = fallbackName.Trim().FirstOrDefault().ToString().ToUpperInvariant(),
                    FontSize = 17,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        // Package icons are square app logos; clip them to the same rounded
        // tile the letter fallback uses so mixed lists stay visually even.
        // Avalonia 12 has no RoundedRectGeometry, so the clip is drawn by hand.
        return new Border
        {
            Width = Size,
            Height = Size,
            CornerRadius = new CornerRadius(Radius),
            Clip = RoundedTileClip(),
            Child = new Image
            {
                Source = icon,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private static Geometry RoundedTileClip()
    {
        var segments = new List<PathSegment>();
        void Line(double x, double y) => segments.Add(new LineSegment { Point = new Point(x, y) });
        void Arc(double x, double y) => segments.Add(new ArcSegment
        {
            Point = new Point(x, y),
            Size = new Size(Radius, Radius),
            SweepDirection = SweepDirection.Clockwise
        });
        Line(Size - Radius, 0);
        Arc(Size, Radius);
        Line(Size, Size - Radius);
        Arc(Size - Radius, Size);
        Line(Radius, Size);
        Arc(0, Size - Radius);
        Line(0, Radius);
        Arc(Radius, 0);
        var figure = new PathFigure
        {
            StartPoint = new Point(Radius, 0),
            IsClosed = true,
            Segments = new PathSegments(segments)
        };
        return new PathGeometry { Figures = new PathFigures { figure } };
    }
}
