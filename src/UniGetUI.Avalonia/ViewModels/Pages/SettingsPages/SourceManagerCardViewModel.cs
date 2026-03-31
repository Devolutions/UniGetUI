using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class SourceManagerCardViewModel : ViewModelBase
{
    private readonly IPackageManager _manager;
    private readonly string _otherLabel;
    private readonly Dictionary<string, IManagerSource> _knownSourceMap = new();

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _showAddForm;
    [ObservableProperty] private ObservableCollection<IManagerSource> _sources = [];
    [ObservableProperty] private ObservableCollection<string> _knownSourceNames = [];
    [ObservableProperty] private string? _selectedKnownSource;
    [ObservableProperty] private string _newSourceName = "";
    [ObservableProperty] private string _newSourceUrl = "";
    [ObservableProperty] private bool _nameUrlEditable = true;

    public string TitleText => CoreTools.Translate("Manage {0} sources", _manager.DisplayName);
    public string AddLabel { get; } = CoreTools.Translate("Add source");
    public string AddConfirmLabel { get; } = CoreTools.Translate("Add");
    public string CancelLabel { get; } = CoreTools.Translate("Cancel");
    public string NameHint { get; } = CoreTools.Translate("Source name");
    public string UrlHint { get; } = CoreTools.Translate("Source URL");

    public SourceManagerCardViewModel(IPackageManager manager)
    {
        _manager = manager;
        _otherLabel = CoreTools.Translate("Other");

        _knownSourceNames.Add(_otherLabel);
        foreach (var s in manager.Properties.KnownSources)
        {
            _knownSourceNames.Add(s.Name);
            _knownSourceMap[s.Name] = s;
        }
        SelectedKnownSource = _otherLabel;

        _ = DoLoadSources();
    }

    [RelayCommand]
    private Task ReloadSources() => DoLoadSources();

    private async Task DoLoadSources()
    {
        IsLoading = true;
        Sources.Clear();

        if (!_manager.Status.Found)
        {
            IsLoading = false;
            return;
        }

        try
        {
            var loaded = await Task.Run(_manager.SourcesHelper.GetSources);
            foreach (var s in loaded)
                Sources.Add(s);
        }
        catch (Exception e)
        {
            Logger.Warn($"[SourceManagerCard] Failed to load sources for {_manager.DisplayName}: {e.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ShowAddSource() => ShowAddForm = true;

    [RelayCommand]
    private void CancelAddSource()
    {
        ShowAddForm = false;
        NewSourceName = "";
        NewSourceUrl = "";
        SelectedKnownSource = _otherLabel;
    }

    [RelayCommand]
    private async Task ConfirmAddSource()
    {
        IManagerSource source;
        if (SelectedKnownSource != _otherLabel &&
            _knownSourceMap.TryGetValue(SelectedKnownSource ?? "", out var known))
        {
            source = known;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(NewSourceName)) return;
            if (!Uri.TryCreate(NewSourceUrl.Trim(), UriKind.Absolute, out var uri)) return;
            source = new ManagerSource(_manager, NewSourceName.Trim(), uri);
        }

        ShowAddForm = false;
        NewSourceName = "";
        NewSourceUrl = "";
        SelectedKnownSource = _otherLabel;

        var op = new AddSourceOperation(source);
        op.OperationFinished += OnAddOperationFinished;
        AvaloniaOperationRegistry.Add(op);
        _ = op.MainThread();
    }

    private void OnAddOperationFinished(object? sender, EventArgs e)
    {
        if (sender is AbstractOperation op)
            op.OperationFinished -= OnAddOperationFinished;
        _ = DoLoadSources();
    }

    [RelayCommand]
    private void DeleteSource(IManagerSource source)
    {
        var op = new RemoveSourceOperation(source);
        AvaloniaOperationRegistry.Add(op);
        _ = op.MainThread();
        Sources.Remove(source);
    }

    partial void OnSelectedKnownSourceChanged(string? value)
    {
        if (value is null || value == _otherLabel)
        {
            NameUrlEditable = true;
            NewSourceName = "";
            NewSourceUrl = "";
        }
        else if (_knownSourceMap.TryGetValue(value, out var s))
        {
            NameUrlEditable = false;
            NewSourceName = s.Name;
            NewSourceUrl = s.Url.ToString();
        }
    }
}
