using Avalonia.Controls;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class GitRepositoriesPage : UserControl
{
    private const int RepositoriesRow = 4;
    private const int DetailsRow = 6;

    public GitRepositoriesPage(GitRepositoriesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        PageGrid.RowDefinitions[DetailsRow].Height = new GridLength(GitRepositoryList.DetailsHeight);
        DetailsSplitter.DragCompleted += (_, _) => SaveDetailsHeight();
    }

    /// <summary>
    /// Keeps the height the panel was dragged to. The splitter can turn the clones' row into a fixed height
    /// too, so it is made to fill the rest again, and resizing the window grows or shrinks the clones instead.
    /// </summary>
    private void SaveDetailsHeight()
    {
        double height = PageGrid.RowDefinitions[DetailsRow].ActualHeight;
        if (height <= 0) return;
        PageGrid.RowDefinitions[RepositoriesRow].Height = new GridLength(1, GridUnitType.Star);
        PageGrid.RowDefinitions[DetailsRow].Height = new GridLength(height);
        GitRepositoryList.DetailsHeight = height;
    }
}
