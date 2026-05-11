using Avalonia.Controls;
using Avalonia.Controls.Templates;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views;

namespace UniGetUI.Avalonia;

/// <summary>
/// Given a view model, returns the corresponding view if possible without reflection.
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        if (param is SidebarViewModel sidebar)
        {
            return new SidebarView
            {
                DataContext = sidebar,
            };
        }

        return new TextBlock { Text = "Not Found: " + param.GetType().Name };
    }

    public bool Match(object? data)
    {
        return data is SidebarViewModel;
    }
}
