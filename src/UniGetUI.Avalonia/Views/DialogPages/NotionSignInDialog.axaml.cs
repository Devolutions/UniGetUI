using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Devolutions.AgentSkills;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Managers.SkillsManager;

namespace UniGetUI.Avalonia.Views.DialogPages;

/// <summary>
/// Signs the Notion CLI in, for Notion skill sources: opens Notion's sign-in page in the browser,
/// shows the code that page shows so the user can check that the two match, and waits for the
/// approval. The Notion CLI keeps the token in the OS credential store; UniGetUI never sees it.
/// </summary>
public partial class NotionSignInDialog : ImmersiveDialog
{
    private readonly AgentSkills _skills;
    private readonly CancellationTokenSource _cancel = new();
    private Uri? _signInPage;
    private bool _waiting;

    public NotionSignInDialog(AgentSkills skills)
    {
        _skills = skills;
        InitializeComponent();

        Title = CoreTools.Translate("Sign in to Notion");
        DescBlock.Text = CoreTools.Translate("Starting the sign-in…");
        SetButtonLabel(OpenButton, CoreTools.Translate("Open Notion again"));
        SetButtonLabel(CloseButton, CoreTools.Translate("Cancel"));

        OpenButton.Click += (_, _) => OpenSignInPage();
        CloseButton.Click += (_, _) => Close();
        Closing += (_, _) =>
        {
            // Stops the Notion CLI waiting for an approval that will not come
            if (_waiting)
                _cancel.Cancel();
        };
    }

    /// <summary>Whether the sign-in went through.</summary>
    public bool SignedIn { get; private set; }

    private static void SetButtonLabel(Button button, string label)
    {
        button.Content = label;
        AutomationProperties.SetName(button, label);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = SignInAsync();
    }

    private async Task SignInAsync()
    {
        _waiting = true;
        try
        {
            NotionSignIn signIn = await _skills.BeginNotionSignInAsync(_cancel.Token);
            _signInPage = signIn.Url;
            DescBlock.Text = CoreTools.Translate(
                "Notion opened in your browser. Check that it shows this code, then approve the sign-in:"
            );
            CodeBlock.Text = signIn.VerificationCode;
            CodeBlock.IsVisible = true;
            InfoBlock.Text = CoreTools.Translate("Waiting for you to approve the sign-in in Notion…");
            OpenButton.IsVisible = true;
            OpenSignInPage();

            await _skills.CompleteNotionSignInAsync(_cancel.Token);
            SignedIn = true;
            NotionStatus status = await _skills.GetNotionStatusAsync();
            ShowResult(
                status.WorkspaceName is { } workspace
                    ? CoreTools.Translate("You are signed in to the Notion workspace {0}.", workspace)
                    : CoreTools.Translate("You are signed in to Notion.")
            );
        }
        catch (OperationCanceledException)
        {
            // Closed while waiting
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not sign in to Notion");
            Logger.Warn(ex);
            ShowResult(CoreTools.Translate("Could not sign in to Notion: {0}", ex.Message));
        }
        finally
        {
            _waiting = false;
        }
    }

    private void ShowResult(string message)
    {
        ProgressBar.IsVisible = false;
        OpenButton.IsVisible = false;
        if (SignedIn)
        {
            DescBlock.IsVisible = false;
            CodeBlock.IsVisible = false;
        }

        InfoBlock.Text = message;
        SetButtonLabel(CloseButton, CoreTools.Translate("Close"));
    }

    // The library only hands over Notion pages, over HTTPS
    private void OpenSignInPage()
    {
        if (_signInPage is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(_signInPage.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not open the Notion sign-in page: {ex.Message}");
        }
    }
}
