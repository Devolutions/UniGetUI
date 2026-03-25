using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.ViewModels.DialogPages;

public partial class OperationOutputViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _outputText = "";

    public OperationOutputViewModel(AbstractOperation operation)
    {
        Title = operation.Metadata.Title;
        OutputText = string.Join("\n", operation.GetOutput().Select(x => x.Item1));
    }
}
