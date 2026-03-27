using UniGetUI.Avalonia.ViewModels.Pages.LogPages;

namespace UniGetUI.Avalonia.Views.Pages;

public class OperationHistoryPage : LogPages.BaseLogPage
{
    public OperationHistoryPage() : base(new OperationHistoryPageViewModel()) { }
}
