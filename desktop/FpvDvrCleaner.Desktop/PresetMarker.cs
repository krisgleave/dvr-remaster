using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
namespace FpvDvrCleaner.Desktop;
public sealed class PresetMarker : Control
{
    private double _fraction;
    public double Fraction { get => _fraction; set { _fraction = value; InvalidateVisual(); } }
    public override void Render(DrawingContext context)
    {
        var x = 7 + Math.Max(0, Bounds.Width - 14) * _fraction;
        var y = Bounds.Height / 2;
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#FF4545")), 2), new Point(x, y - 6), new Point(x, y + 6));
    }
}
