using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace StarDexs.Sync;

/// <summary>
/// The sync badge beside the panel's status. Idle: a quiet crosshair. Syncing: the crosshair turns slowly while
/// a ring fills around it, stage by stage. Done: a green check that pops once. Stopped: an amber "!".
/// Drawn here, frame by frame, only while something is moving.
/// </summary>
internal sealed class SyncBar : Control
{
    public enum Look { Idle, Running, Hit, Missed }

    private const double Size = 44, Ring = 19, RingWidth = 2.5;
    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(4), PopTime = TimeSpan.FromMilliseconds(450);
    private readonly DispatcherTimer _frames = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private DateTime _since = DateTime.UtcNow;
    private Look _look = Look.Idle;
    private bool _active;
    private double _share, _shown;

    public SyncBar()
    {
        Width = Height = Size;
        _frames.Tick += (_, _) => InvalidateVisual();
    }

    /// <summary>Shows a sync's state and how far it has got (0 to 1).</summary>
    public void Show(Look look, double share)
    {
        if (look != _look) _since = DateTime.UtcNow;
        _look = look;
        _share = Math.Clamp(share, 0, 1);
        if (_active && (look is Look.Running or Look.Hit || Math.Abs(_shown - _share) > 0.001)) _frames.Start();
        InvalidateVisual();
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (!active) _frames.Stop();
        else Show(_look, _share);
    }

    private Color Colour => _look switch
    {
        Look.Running => Palette.Accent,
        Look.Hit => Palette.Good,
        Look.Missed => Palette.Warn,
        _ => Palette.Soft,
    };

    public override void Render(DrawingContext context)
    {
        var elapsed = DateTime.UtcNow - _since;
        // The ring eases towards its share instead of jumping.
        _shown += (_share - _shown) * 0.12;
        var settled = Math.Abs(_shown - _share) < 0.001;
        if (settled && (_look is Look.Idle or Look.Missed || _look == Look.Hit && elapsed > PopTime)) _frames.Stop();

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var colour = Colour;
        var pop = _look == Look.Hit ? 1 + 0.12 * Math.Sin(Math.Min(1, elapsed / PopTime) * Math.PI) : 1;
        context.DrawEllipse(Palette.Tint(colour, 0.14), null, centre, Ring * pop, Ring * pop);

        if (_look == Look.Running)
        {
            context.DrawEllipse(null, new Pen(Palette.Tint(colour, 0.18), RingWidth), centre, Ring, Ring);
            DrawArc(context, centre, Math.Max(0.04, _shown), new Pen(Palette.Brush(colour), RingWidth, lineCap: PenLineCap.Round));
        }

        var pen = new Pen(Palette.Brush(colour), 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        switch (_look)
        {
            case Look.Hit:
                var s = 1.15 * pop;
                var check = new StreamGeometry();
                using (var draw = check.Open())
                {
                    draw.BeginFigure(centre + new Point(-6 * s, 0.5 * s), false);
                    draw.LineTo(centre + new Point(-1.8 * s, 4.6 * s));
                    draw.LineTo(centre + new Point(6.5 * s, -4.8 * s));
                    draw.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(Palette.Brush(colour), 2.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), check);
                break;
            case Look.Missed:
                context.DrawLine(new Pen(Palette.Brush(colour), 2.4, lineCap: PenLineCap.Round), centre + new Point(0, -6), centre + new Point(0, 2));
                context.DrawEllipse(Palette.Brush(colour), null, centre + new Point(0, 6.5), 1.4, 1.4);
                break;
            default:
                var angle = _look == Look.Running ? elapsed / Turn * Math.PI * 2 : 0;
                DrawReticle(context, centre, angle, pen);
                break;
        }
    }

    /// <summary>A small crosshair: a circle, a dot, and four short ticks, turned by <paramref name="angle"/>.</summary>
    private static void DrawReticle(DrawingContext context, Point centre, double angle, Pen pen)
    {
        const double circle = 7.5, outer = 11.5;
        context.DrawEllipse(null, pen, centre, circle, circle);
        context.DrawEllipse(pen.Brush, null, centre, 1.6, 1.6);
        for (var i = 0; i < 4; i++)
        {
            var a = angle + i * Math.PI / 2;
            var direction = new Point(Math.Cos(a), Math.Sin(a));
            context.DrawLine(pen, centre + direction * (circle - 2.5), centre + direction * outer);
        }
    }

    private static void DrawArc(DrawingContext context, Point centre, double share, Pen pen)
    {
        if (share >= 0.999)
        {
            context.DrawEllipse(null, pen, centre, Ring, Ring);
            return;
        }
        var start = -Math.PI / 2;
        var end = start + share * Math.PI * 2;
        var arc = new StreamGeometry();
        using (var draw = arc.Open())
        {
            draw.BeginFigure(centre + new Point(Math.Cos(start), Math.Sin(start)) * Ring, false);
            draw.ArcTo(centre + new Point(Math.Cos(end), Math.Sin(end)) * Ring, new Avalonia.Size(Ring, Ring), 0, share > 0.5, SweepDirection.Clockwise);
            draw.EndFigure(false);
        }
        context.DrawGeometry(null, pen, arc);
    }
}
