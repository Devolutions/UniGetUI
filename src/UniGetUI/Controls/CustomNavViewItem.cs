using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using UniGetUI.Interface.Enums;
using UniGetUI.Interface.Widgets;

namespace UniGetUI.Controls;

internal sealed partial class CustomNavViewItem : NavigationViewItem
{
    int _iconSize = 24;
    public IconType LocalIcon
    {
        set => base.Icon = new LocalIcon(value);
    }
    public string GlyphIcon
    {
        set => base.Icon = new FontIcon() { Glyph = value };
    }
    public new IconElement Icon
    {
        set => base.Icon = value;
    }

    public bool IsLoading
    {
        set
        {
            if (value)
                _ = increaseMargins();
            else
                _ = decreaseMargins();
            _progressRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public int IconSize
    {
        set => Resources["NavigationViewItemOnLeftIconBoxHeight"] = _iconSize = value;
    }

    public string Text
    {
        set
        {
            string text = CoreTools.Translate(value);
            _textBlock.Text = text;
            ToolTipService.SetToolTip(this, text);
        }
    }

    private readonly TextBlock _textBlock;
    private readonly ProgressRing _progressRing;

    private PageType _pageType;
    public new PageType Tag
    {
        set => _pageType = value;
        get => _pageType;
    }

    public CustomNavViewItem()
    {
        Height = 54;
        Resources["NavigationViewItemOnLeftIconBoxHeight"] = _iconSize;
        Resources["NavigationViewItemContentPresenterMargin"] = new Thickness(0);

        var grid = new Grid { Height = 44 };

        _progressRing = new ProgressRing
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(-42, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsIndeterminate = true,
            Visibility = Visibility.Collapsed,
        };

        _textBlock = new TextBlock { VerticalAlignment = VerticalAlignment.Center };

        grid.Children.Add(_progressRing);
        grid.Children.Add(_textBlock);
        base.Content = grid;
    }

    public async Task increaseMargins()
    {
        for (int i = (int)base.Icon.Margin.Left; i < 6; i += 2)
        {
            base.Icon.Margin = new Thickness(i);
            await Task.Delay(15);
        }
        base.Icon.Margin = new Thickness(6);
    }

    public async Task decreaseMargins()
    {
        for (int i = (int)base.Icon.Margin.Left; i > 0; i -= 2)
        {
            base.Icon.Margin = new Thickness(i);
            await Task.Delay(15);
        }
        base.Icon.Margin = new Thickness(0);
    }
}
