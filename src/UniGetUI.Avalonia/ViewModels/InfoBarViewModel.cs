using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UniGetUI.Avalonia.ViewModels;

public enum InfoBarSeverity { Informational, Warning, Error, Success }

public partial class InfoBarViewModel : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private InfoBarSeverity _severity = InfoBarSeverity.Informational;
    [ObservableProperty] private bool _isClosable = true;
    [ObservableProperty] private string _actionButtonText = "";
    [ObservableProperty] private ICommand? _actionButtonCommand;

    public Action? OnClosed { get; set; }

    partial void OnIsOpenChanged(bool value)
    {
        if (!value) OnClosed?.Invoke();
    }

    [RelayCommand]
    private void Close() => IsOpen = false;
}
