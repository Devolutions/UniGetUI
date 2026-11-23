using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace UniGetUI.Avalonia.Infrastructure;

internal sealed class DragGhost
{
    private readonly Control _visual;
    private readonly Point _grabOffset;

    private DragGhost(Control visual, Point grabOffset)
    {
        _visual = visual;
        _grabOffset = grabOffset;
    }

    public static DragGhost? Show(Visual host, Control visual, Point grabOffset)
    {
        if (OverlayLayer.GetOverlayLayer(host) is not { } overlay)
            return null;

        visual.IsHitTestVisible = false;
        overlay.Children.Add(visual);
        return new DragGhost(visual, grabOffset);
    }

    public void Move(PointerEventArgs e)
    {
        if (_visual.GetVisualParent() is not Visual layer)
            return;

        Point position = e.GetPosition(layer) - _grabOffset;
        Canvas.SetLeft(_visual, position.X);
        Canvas.SetTop(_visual, position.Y);
    }

    public void Remove() => (_visual.GetVisualParent() as Panel)?.Children.Remove(_visual);
}
