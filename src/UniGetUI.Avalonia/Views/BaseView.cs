using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.Views;

/// <summary>
/// Typed base class for all UserControl views.
/// Provides a strongly-typed ViewModel property that mirrors DataContext.
/// </summary>
public abstract class BaseView<TViewModel> : UserControl where TViewModel : ViewModelBase
{
    public TViewModel? ViewModel => DataContext as TViewModel;
}
