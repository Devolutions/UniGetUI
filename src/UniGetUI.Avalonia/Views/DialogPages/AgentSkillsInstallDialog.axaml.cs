using System.Collections.ObjectModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Devolutions.AgentSkills;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Avalonia.Views.DialogPages;

public sealed partial class SkillInstallTarget(AgentInfo agent, bool selected) : ObservableObject
{
    public string Id => agent.Id;
    public string DisplayName => agent.DisplayName;
    public string DetectionLabel => agent.IsDetected
        ? CoreTools.Translate("Detected on this device")
        : agent.IsUniversal ? CoreTools.Translate("Uses the shared skills folder")
        : CoreTools.Translate("Not detected on this device");

    [ObservableProperty]
    private bool _isSelected = selected;
}

public sealed record AgentSkillsInstallSelection(IReadOnlyList<string> AgentIds, bool RememberAsDefault);

public partial class AgentSkillsInstallDialog : ImmersiveDialog
{
    public ObservableCollection<SkillInstallTarget> Targets { get; } = [];
    public IReadOnlyList<SkillItem> Skills { get; }
    public bool HasSkills => Skills.Count > 0;
    public string Summary { get; }
    public string DialogTitle { get; }
    public AgentSkillsInstallSelection? Selection { get; private set; }

    public AgentSkillsInstallDialog(IReadOnlyList<SkillItem> skills, IReadOnlyList<AgentInfo> agents,
        IReadOnlyList<string>? defaultTargets)
    {
        InitializeComponent();
        Skills = skills;
        DialogTitle = skills.Count == 1
            ? CoreTools.Translate("Install {0}", skills[0].Name)
            : CoreTools.Translate("Install {0} skills", skills.Count);
        Summary = skills.Count == 1
            ? CoreTools.Translate("Choose where to install {0}.", skills[0].Name)
            : CoreTools.Translate("Choose where to install these {0} skills.", skills.Count);
        var defaults = defaultTargets is { Count: > 0 }
            ? defaultTargets.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : agents.Where(agent => agent.IsDetected || agent.IsUniversal)
                .Select(agent => agent.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents.Where(agent => agent.GlobalSkillsDirectory is not null)
                     .OrderBy(agent => agent.DisplayName, StringComparer.OrdinalIgnoreCase))
            Targets.Add(new SkillInstallTarget(agent, defaults.Contains(agent.Id)));

        DataContext = this;
        InstallButton.Click += (_, _) => Confirm();
        CancelButton.Click += (_, _) => Close();
        Selection = null;
    }

    public static async Task<AgentSkillsInstallSelection?> ShowAsync(IReadOnlyList<SkillItem> skills,
        IReadOnlyList<AgentInfo> agents, IReadOnlyList<string>? defaultTargets)
    {
        if (MainWindow.Instance is not { } owner)
            throw new InvalidOperationException("The main window is not available to show the skill install confirmation.");

        var dialog = new AgentSkillsInstallDialog(skills, agents, defaultTargets);
        await dialog.ShowDialog(owner);
        return dialog.Selection;
    }

    private void Confirm()
    {
        var selected = Targets.Where(target => target.IsSelected).Select(target => target.Id).ToArray();
        if (selected.Length == 0)
        {
            TargetValidationText.IsVisible = true;
            TargetValidationText.BringIntoView();
            Dispatcher.UIThread.Post(() => TargetValidationText.Focus(), DispatcherPriority.Background);
            return;
        }

        Selection = new AgentSkillsInstallSelection(selected, RememberTargetsCheckBox.IsChecked == true);
        Close();
    }
}
