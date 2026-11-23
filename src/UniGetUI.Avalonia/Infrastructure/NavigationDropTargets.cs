using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace UniGetUI.Avalonia.Infrastructure;

internal interface INavigationDropTarget
{
    ScrollViewer? DropScrollViewer { get; }

    int? UpdateExternalDrop(Point pointInTopLevel);

    void EndExternalDrop();
}

internal static class NavigationDropTargets
{
    public static INavigationDropTarget? At(TopLevel topLevel, Point pointInTopLevel, object source)
        => topLevel.GetVisualsAt(pointInTopLevel, visual => visual.IsVisible && (visual is not IInputElement input || input.IsHitTestVisible))
            .Select(visual => visual.GetSelfAndVisualAncestors().OfType<INavigationDropTarget>().FirstOrDefault())
            .FirstOrDefault(target => target is not null && !ReferenceEquals(target, source));
}
