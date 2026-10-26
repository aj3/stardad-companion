using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace StarDexs.Sync;

/// <summary>
/// The sync bar: the track fills stage by stage while a tracer runs along it to the red crosshair (the sync
/// icon). A finished sync lands on the target (it flashes and fills); one that stopped short greys it out.
/// Drawn here, frame by frame, only while something is moving.
/// </summary>
internal sealed class SyncBar : Control
{
    public enum Look { Idle, Running, Hit, Missed }

    private const double Reticle = 26, Gap = 10, TrackHeight = 6;
    private static readonly TimeSpan TracerLap = TimeSpan.FromMilliseconds(1100), HitTime = TimeSpan.FromMilliseconds(700);
    private readonly DispatcherTimer _frames = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private DateTime _since = DateTime.UtcNow;
    private Look _look = Look.Idle;
    private double _share, _shown;

    public SyncBar()
    {
        Height = Reticle + 4;
        _frames.Tick += (_, _) => InvalidateVisual();
    }

    /// <summary>Shows a sync's state and how far it has got (0 to 1).</summary>
    public void Show(Look look, double share)
    {
        if (look != _look) _since = DateTime.UtcNow;
        _look = look;
        _share = Math.Clamp(share, 0, 1);
        if (look is Look.Running or Look.Hit || Math.Abs(_shown - _share) > 0.001) _frames.Start();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _since;
        // The fill eases towards its share instead of jumping.
        _shown += (_share - _shown) * 0.15;
        var still = _look is Look.Idle or Look.Missed && Math.Abs(_shown - _share) < 0.001
                    || _look == Look.Hit && elapsed > HitTime;
        if (still) _frames.Stop();

        var width = Bounds.Width - Reticle - Gap;
        var middle = Bounds.Height / 2;
        var track = new Rect(0, middle - TrackHeight / 2, Math.Max(0, width), TrackHeight);
        context.DrawRectangle(Palette.Brush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), null, track, 3, 3);
        if (_shown > 0.002)
        {
            var fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0x59, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B), 0), new GradientStop(Palette.Accent, 1) },
            };
            context.DrawRectangle(fill, null, track.WithWidth(track.Width * _shown), 3, 3);
        }

        if (_look == Look.Running)
        {
            // The tracer: a hot dot running from the start of the track into the reticle.
            var lap = elapsed.TotalMilliseconds % TracerLap.TotalMilliseconds / TracerLap.TotalMilliseconds;
            var x = -9 + (width + Gap + Reticle / 2 + 9) * lap;
            var glow = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Palette.Target, 0.45), new GradientStop(Color.FromArgb(0, 0xFF, 0x4D, 0x4D), 1) },
            };
            context.DrawEllipse(glow, null, new Point(x, middle), 9, 5);
        }

        DrawReticle(context, new Point(Bounds.Width - Reticle / 2, middle), elapsed);
    }

    private void DrawReticle(DrawingContext context, Point centre, TimeSpan elapsed)
    {
        var colour = _look == Look.Missed ? Palette.Soft : Palette.Target;
        var scale = 1.0;
        if (_look == Look.Running) scale = 1 - 0.08 * Math.Sin(elapsed.TotalMilliseconds / TracerLap.TotalMilliseconds * Math.PI * 2);
        var hit = _look == Look.Hit ? Math.Min(1, elapsed.TotalMilliseconds / HitTime.TotalMilliseconds) : 0;
        if (_look == Look.Hit) scale = 1 + 0.35 * Math.Sin(hit * Math.PI);
        var outer = Reticle / 2 * 0.82 * scale;
        var pen = new Pen(Palette.Brush(colour), 2, lineCap: PenLineCap.Round);
        if (_look == Look.Hit)
        {
            // The hit: a ring bursting outwards, fading as it goes.
            var ring = new Pen(Palette.Brush(Color.FromArgb((byte)(200 * (1 - hit)), colour.R, colour.G, colour.B)), 2);
            context.DrawEllipse(null, ring, centre, outer + 10 * hit, outer + 10 * hit);
        }
        context.DrawEllipse(null, pen, centre, outer, outer);
        context.DrawEllipse(_look == Look.Hit ? Palette.Brush(colour) : null, pen, centre, outer * 0.42, outer * 0.42);
        var arm = Reticle / 2 * scale;
        context.DrawLine(pen, centre + new Point(0, -arm), centre + new Point(0, -outer * 0.55));
        context.DrawLine(pen, centre + new Point(0, arm), centre + new Point(0, outer * 0.55));
        context.DrawLine(pen, centre + new Point(-arm, 0), centre + new Point(-outer * 0.55, 0));
        context.DrawLine(pen, centre + new Point(arm, 0), centre + new Point(outer * 0.55, 0));
    }
}
