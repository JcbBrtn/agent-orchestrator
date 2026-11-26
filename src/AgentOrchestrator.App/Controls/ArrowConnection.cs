using System.Windows;
using System.Windows.Media;
using Nodify;

namespace AgentOrchestrator.App.Controls;

/// <summary>Bezier connection whose label can be placed anywhere along the curve (0 = source, 1 = target), so labels of opposite arrows between the same nodes don't overlap.</summary>
public class ArrowConnection : Connection
{
    public static readonly DependencyProperty LabelPositionProperty = DependencyProperty.Register(
        nameof(LabelPosition), typeof(double), typeof(ArrowConnection),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsRender));

    static ArrowConnection()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ArrowConnection), new FrameworkPropertyMetadata(typeof(Connection)));
    }

    public double LabelPosition
    {
        get => (double)GetValue(LabelPositionProperty);
        set => SetValue(LabelPositionProperty, value);
    }

    protected override Point GetTextPosition(FormattedText text, Point source, Point target)
    {
        // Mirrors Nodify's Connection control-point calculation (horizontal orientation).
        double direction = Direction == ConnectionDirection.Forward ? 1d : -1d;
        var spacing = new Vector(Spacing * direction, 0d);
        var p0 = source + spacing;
        var p3 = target - spacing;

        var delta = target - source;
        double width = Math.Abs(delta.X);
        double offset = Math.Max(Math.Min(100d, Math.Abs(delta.Y)), width / 2d);
        offset = Math.Min(100d + Math.Sqrt(width * 25d), offset);
        var control = new Vector(offset * direction, 0d);
        var p1 = p0 + control;
        var p2 = p3 - control;

        double t = Math.Clamp(LabelPosition, 0d, 1d), u = 1 - t;
        var point = (Point)((Vector)p0 * u * u * u + (Vector)p1 * 3 * t * u * u + (Vector)p2 * 3 * t * t * u + (Vector)p3 * t * t * t);
        return point - new Vector(text.Width / 2, text.Height / 2);
    }
}
