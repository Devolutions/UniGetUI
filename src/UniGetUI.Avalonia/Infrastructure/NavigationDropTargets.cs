using Avalonia;
using Avalonia.Controls;
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
        => topLevel.GetVisualsAt(pointInTopLevel)
            .Select(visual => visual.GetSelfAndVisualAncestors().OfType<INavigationDropTarget>().FirstOrDefault())
            .FirstOrDefault(target => target is not null && !ReferenceEquals(target, source));
}
