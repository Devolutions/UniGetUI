using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.Xcode;

namespace UniGetUI.Avalonia.Views.DialogPages;

/// <summary>
/// Asks the one question xcodes is waiting on: an Apple ID, a verification code, the macOS password. What is
/// typed goes straight back to xcodes; it is not stored, and a password is never shown.
/// </summary>
public sealed class XcodePromptDialog : ImmersiveConfirmationDialog
{
    // Typing this word is how xcodes is told to send a text message instead of using a trusted device.
    private const string SendTextMessage = "sms";

    private readonly TextBox _answer;
    private readonly bool _isSecret;
    private bool _textMessageRequested;

    public XcodePromptDialog(XcodesPrompt prompt)
    {
        _isSecret = prompt.IsSecret;
        _answer = new TextBox { PasswordChar = _isSecret ? '•' : '\0', AcceptsReturn = false };
        _answer.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return)) return;
            e.Handled = true;
            Complete(true);
        };

        (string title, string explanation, string label, string primary) = Describe(prompt);
        _answer.PlaceholderText = label;
        AutomationProperties.SetName(_answer, label);

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap });
        if (prompt.Kind == XcodesPromptKind.SmsCode && prompt.Message.Length > 0)
            body.Children.Add(new TextBlock { Text = prompt.Message, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });

        if (ShowsContext(prompt.Kind) && prompt.Context.Count > 0)
        {
            body.Children.Add(new ScrollViewer
            {
                MaxHeight = 200,
                Content = new SelectableTextBlock
                {
                    Text = string.Join(Environment.NewLine, prompt.Context),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.85,
                },
            });
        }

        if (prompt.Kind == XcodesPromptKind.FederatedUrl && FindWebAddress(prompt.Context) is { } address)
        {
            var open = new Button { Content = CoreTools.Translate("Open the sign-in page") };
            open.Click += (_, _) => CoreTools.Launch(address);
            body.Children.Add(open);
        }

        body.Children.Add(_answer);

        if (prompt.Kind == XcodesPromptKind.TwoFactorCode)
        {
            var text = new Button { Content = CoreTools.Translate("Send a text message instead") };
            text.Click += (_, _) =>
            {
                _textMessageRequested = true;
                Close();
            };
            body.Children.Add(text);
        }

        Configure(title, body, primary, CoreTools.Translate("Cancel"));
        FocusPrimaryButton = false;
        MaxWidth = 520;
    }

    /// <summary>What was answered; null when the dialog was cancelled or closed.</summary>
    public string? Answer
    {
        get
        {
            if (_textMessageRequested) return SendTextMessage;
            if (Result is not true) return null;
            // A password is sent as typed; anything else is not meant to start or end with a space.
            return _isSecret ? _answer.Text ?? "" : (_answer.Text ?? "").Trim();
        }
    }

    private static bool ShowsContext(XcodesPromptKind kind)
        => kind is XcodesPromptKind.PhoneNumber or XcodesPromptKind.FederatedUrl or XcodesPromptKind.Unknown;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // The base moves the focus to a button after it opens; this lands after it, so typing can start at once.
        Dispatcher.UIThread.Post(() => _answer.Focus(), DispatcherPriority.Background);
    }

    /// <summary>
    /// Shows the question and returns the answer, or null when it was cancelled. It may be called from any thread,
    /// and brings the window forward, since a question may arrive long after the page was left.
    /// </summary>
    public static Task<string?> AskAsync(XcodesPrompt prompt, CancellationToken cancellationToken)
        => Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (MainWindow.Instance is not { } owner)
                return null;

            owner.ShowFromTray();
            var dialog = new XcodePromptDialog(prompt);
            using CancellationTokenRegistration registration = cancellationToken.Register(
                () => Dispatcher.UIThread.Post(dialog.CancelPendingChoice));
            await dialog.ShowDialog(owner);
            return dialog.Answer;
        });

    private static (string Title, string Explanation, string Label, string Primary) Describe(XcodesPrompt prompt)
        => prompt.Kind switch
        {
            XcodesPromptKind.AppleId => (
                CoreTools.Translate("Sign in to Apple"),
                CoreTools.Translate("Xcode is downloaded from Apple, so xcodes needs the Apple ID you use for developer.apple.com. UniGetUI does not keep it."),
                CoreTools.Translate("Apple ID"),
                CoreTools.Translate("Continue")),
            XcodesPromptKind.Password => (
                CoreTools.Translate("Apple ID password"),
                CoreTools.Translate("xcodes saves it in your macOS keychain once you are signed in. UniGetUI never stores it."),
                CoreTools.Translate("Password"),
                CoreTools.Translate("Sign in")),
            XcodesPromptKind.TwoFactorCode => (
                CoreTools.Translate("Verification code"),
                CoreTools.Translate("Apple showed a code on your trusted devices. Enter it to continue."),
                CoreTools.Translate("Verification code"),
                CoreTools.Translate("Continue")),
            XcodesPromptKind.SmsCode => (
                CoreTools.Translate("Verification code"),
                CoreTools.Translate("Apple sent you a text message with a code. Enter it to continue."),
                CoreTools.Translate("Verification code"),
                CoreTools.Translate("Continue")),
            XcodesPromptKind.PhoneNumber => (
                CoreTools.Translate("Choose a phone number"),
                CoreTools.Translate("Enter the number in front of the trusted phone that should receive the code."),
                CoreTools.Translate("Number"),
                CoreTools.Translate("Continue")),
            XcodesPromptKind.FederatedUrl => (
                CoreTools.Translate("Sign in with your organization"),
                CoreTools.Translate("Your Apple account signs in through your organization. Finish signing in in your browser, then paste the address the browser ends up on."),
                CoreTools.Translate("Address"),
                CoreTools.Translate("Continue")),
            XcodesPromptKind.SudoPassword => (
                CoreTools.Translate("macOS password"),
                CoreTools.Translate("Finishing the setup needs administrator rights. Enter the password of your macOS account. UniGetUI never stores it."),
                CoreTools.Translate("Password"),
                CoreTools.Translate("Continue")),
            _ => (
                CoreTools.Translate("xcodes needs an answer"),
                prompt.Message.Length > 0
                    ? prompt.Message
                    : CoreTools.Translate("xcodes is waiting for your answer to continue."),
                CoreTools.Translate("Answer"),
                CoreTools.Translate("Continue")),
        };

    /// <summary>The first web address in what xcodes wrote, for a browser to open.</summary>
    internal static string? FindWebAddress(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            foreach (string word in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (Uri.TryCreate(word, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http")
                    return uri.AbsoluteUri;
            }
        }
        return null;
    }
}
