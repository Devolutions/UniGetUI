using CommunityToolkit.Mvvm.ComponentModel;

namespace UniGetUI.Avalonia.ViewModels;

/// <summary>
/// Base class for all ViewModels. Inherits ObservableObject from CommunityToolkit.Mvvm,
/// which provides INotifyPropertyChanged, SetProperty, and [ObservableProperty] source-generator support.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
}
