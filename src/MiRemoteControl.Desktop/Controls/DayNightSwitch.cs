using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace MiRemoteControl.Desktop.Controls;

/// <summary>
/// Avalonia port of andrew-demchenk0's Uiverse day/night switch.
/// </summary>
public sealed class DayNightSwitch : ToggleButton
{
    private static readonly Color DayTrack = Color.FromRgb(115, 192, 252);
    private static readonly Color DayBorder = Color.FromRgb(90, 173, 232);
    private static readonly Color NightTrack = Color.FromRgb(21, 25, 30);
    private static readonly Color NightBorder = Color.FromRgb(48, 56, 64);
    private static readonly IBrush ThumbBrush = new SolidColorBrush(Color.FromRgb(237, 241, 244));
    private static readonly IBrush SunBrush = new SolidColorBrush(Color.FromRgb(255, 212, 59));
    private static readonly IBrush MoonBrush = new SolidColorBrush(Color.FromRgb(118, 226, 190));
    private static readonly Geometry MoonGeometry = CreateMoonGeometry();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _transition = new();
    private readonly Stopwatch _animationClock = new();
    private double _progress;
    private double _from;
    private double _target;

    public DayNightSwitch()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        ClipToBounds = true;
        UseLayoutRounding = true;
        _timer.Tick += (_, _) => Animate();
        AttachedToVisualTree += (_, _) =>
        {
            _animationClock.Start();
            _timer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _animationClock.Stop();
            _timer.Stop();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsCheckedProperty) return;
        _from = _progress;
        _target = IsChecked == true ? 1 : 0;
        _transition.Restart();
        _timer.Start();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var scaleX = width / 64d;
        var scaleY = height / 34d;
        // Keep the anti-aliased edge inside the control bounds. Drawing directly on
        // the bounds lets the compositor clip the outer half-pixels into jagged edges.
        var edgeInset = 0.75 * Math.Min(scaleX, scaleY);
        var trackRect = new Rect(
            edgeInset,
            edgeInset,
            Math.Max(0, width - edgeInset * 2),
            Math.Max(0, height - edgeInset * 2));
        var trackRadius = trackRect.Height / 2;
        var trackBrush = new SolidColorBrush(Lerp(DayTrack, NightTrack, _progress));
        var borderPen = new Pen(
            new SolidColorBrush(Lerp(DayBorder, NightBorder, _progress)),
            Math.Max(1, Math.Min(scaleX, scaleY)));
        context.DrawRectangle(trackBrush, borderPen, trackRect, trackRadius, trackRadius);

        using (context.PushClip(new RoundedRect(trackRect, trackRadius)))
        {
            DrawMoon(context, scaleX, scaleY);
            DrawSun(context, scaleX, scaleY);

            var thumbLeft = (2 + 30 * Ease(_progress)) * scaleX;
            var thumbTop = 2 * scaleY;
            context.DrawEllipse(ThumbBrush, null,
                new Point(thumbLeft + 15 * scaleX, thumbTop + 15 * scaleY),
                15 * scaleX, 15 * scaleY);
        }

        if (IsKeyboardFocusWithin)
            context.DrawRectangle(null, new Pen(MoonBrush, 1), trackRect, trackRadius, trackRadius);
    }

    private void DrawSun(DrawingContext context, double scaleX, double scaleY)
    {
        var center = new Point(48 * scaleX, 18 * scaleY);
        var radius = 5 * Math.Min(scaleX, scaleY);
        context.DrawEllipse(SunBrush, null, center, radius, radius);

        var angleOffset = _animationClock.Elapsed.TotalSeconds * Math.PI * 2 / 15;
        var pen = new Pen(SunBrush, 2 * Math.Min(scaleX, scaleY), lineCap: PenLineCap.Round);
        for (var index = 0; index < 8; index++)
        {
            var angle = angleOffset + index * Math.PI / 4;
            var direction = new Point(Math.Cos(angle), Math.Sin(angle));
            context.DrawLine(pen,
                new Point(center.X + direction.X * 8 * scaleX, center.Y + direction.Y * 8 * scaleY),
                new Point(center.X + direction.X * 10 * scaleX, center.Y + direction.Y * 10 * scaleY));
        }
    }

    private void DrawMoon(DrawingContext context, double scaleX, double scaleY)
    {
        var phase = _animationClock.Elapsed.TotalSeconds / 5 % 1;
        var degrees = phase switch
        {
            < 0.25 => -40 * phase,
            < 0.75 => -10 + 40 * (phase - 0.25),
            _ => 10 - 40 * (phase - 0.75)
        };
        var tilt = degrees * Math.PI / 180;
        var center = new Point(17 * scaleX, 17 * scaleY);
        using (context.PushTransform(Matrix.CreateRotation(tilt, center)))
        using (context.PushTransform(Matrix.CreateTranslation(8 * scaleX, 5 * scaleY)))
        using (context.PushTransform(Matrix.CreateScale(scaleX, scaleY)))
            context.DrawGeometry(MoonBrush, null, MoonGeometry);
    }

    private static Geometry CreateMoonGeometry()
    {
        var geometry = StreamGeometry.Parse(
            "M223.5,32 C100,32 0,132.3 0,256 S100,480 223.5,480 C284.1,480 339,455.8 379.3,416.6 C384.3,411.7 385.6,404.1 382.4,397.9 S372.3,388.2 365.4,389.4 C355.6,391.1 345.6,392 335.3,392 C238.4,392 159.8,313.2 159.8,216 C159.8,150.2 195.8,92.9 249.1,62.7 C255.2,59.2 258.3,52.2 256.8,45.4 S249.5,33.5 242.5,32.9 C236.2,32.4 229.8,32 223.5,32 Z");
        geometry.Transform = new MatrixTransform(Matrix.CreateScale(0.046875, 0.046875));
        return geometry;
    }

    private void Animate()
    {
        if (_transition.IsRunning)
        {
            var elapsed = _transition.Elapsed.TotalMilliseconds / 400d;
            if (elapsed >= 1)
            {
                _progress = _target;
                _transition.Stop();
            }
            else
            {
                _progress = _from + (_target - _from) * Ease(elapsed);
            }
        }
        InvalidateVisual();
    }

    private static double Ease(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }

    private static Color Lerp(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(from.A + (to.A - from.A) * amount),
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }
}
