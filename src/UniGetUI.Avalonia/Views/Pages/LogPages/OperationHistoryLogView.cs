using UniGetUI.Avalonia.ViewModels.Pages.LogPages;

namespace UniGetUI.Avalonia.Views.Pages.LogPages;

/// <summary>The raw-console "Log" tab of the operation history page.</summary>
public class OperationHistoryLogView : BaseLogPage
{
    public OperationHistoryLogView() : base(new OperationHistoryPageViewModel()) { }
}
