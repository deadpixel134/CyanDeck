using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace CyanDeck.Appearance;

public sealed class WpfShortcutBadgeAdorner : Adorner
{
    private readonly Func<ShortcutBadgeOptions> _getOptions;

    public WpfShortcutBadgeAdorner(UIElement adornedElement, Func<ShortcutBadgeOptions> getOptions)
        : base(adornedElement)
    {
        ArgumentNullException.ThrowIfNull(getOptions);
        _getOptions = getOptions;
        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var options = _getOptions().Normalize();
        var width = Math.Max(0, AdornedElement.RenderSize.Width);
        var height = Math.Max(0, AdornedElement.RenderSize.Height);
        var iconSide = Math.Min(width, height);
        if (iconSide <= 0)
        {
            return;
        }

        var side = Math.Clamp(
            iconSide * options.RelativeBadgeScale,
            options.MinimumBadgeSizeDip,
            options.MaximumBadgeSizeDip);
        side = Math.Min(side, iconSide);

        var x = Math.Max(0, 1.0);
        var y = Math.Max(0, height - side - 1.0);
        var rect = new Rect(x, y, side, side);
        var radius = Math.Max(1.5, side * 0.18);

        var background = SystemColors.WindowBrush;
        var foreground = SystemColors.WindowTextBrush;
        var outline = new Pen(foreground, Math.Max(1.0, side * 0.08));

        drawingContext.DrawRoundedRectangle(background, outline, rect, radius, radius);

        // Independent generic shortcut marker. It intentionally does not copy a
        // proprietary Windows or Stardock bitmap asset.
        var p0 = new Point(x + side * 0.24, y + side * 0.68);
        var p1 = new Point(x + side * 0.62, y + side * 0.68);
        var p2 = new Point(x + side * 0.62, y + side * 0.82);
        var p3 = new Point(x + side * 0.84, y + side * 0.56);
        var p4 = new Point(x + side * 0.62, y + side * 0.30);
        var p5 = new Point(x + side * 0.62, y + side * 0.44);
        var p6 = new Point(x + side * 0.34, y + side * 0.44);

        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            gc.BeginFigure(p0, isFilled: false, isClosed: false);
            gc.LineTo(p1, isStroked: true, isSmoothJoin: true);
            gc.LineTo(p2, isStroked: true, isSmoothJoin: true);
            gc.LineTo(p3, isStroked: true, isSmoothJoin: true);
            gc.LineTo(p4, isStroked: true, isSmoothJoin: true);
            gc.LineTo(p5, isStroked: true, isSmoothJoin: true);
            gc.LineTo(p6, isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();

        var arrowPen = new Pen(foreground, Math.Max(1.25, side * 0.11))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (arrowPen.CanFreeze)
        {
            arrowPen.Freeze();
        }

        drawingContext.DrawGeometry(null, arrowPen, geometry);
    }
}
