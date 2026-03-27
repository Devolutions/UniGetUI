using Avalonia.Media;

namespace UniGetUI.Avalonia.ViewModels.Pages.LogPages;

public class LogLineItem
{
    public string Text { get; }
    public IBrush Foreground { get; }

    public LogLineItem(string text, IBrush foreground)
    {
        Text = text;
        Foreground = foreground;
    }
}
