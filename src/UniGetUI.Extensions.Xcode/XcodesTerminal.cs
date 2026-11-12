using System.Text.RegularExpressions;

namespace UniGetUI.Extensions.Xcode;

/// <summary>One finished line of an operation's log.</summary>
/// <param name="ReplacesPrevious">The line updates the previous one, as download progress does; whoever keeps a
/// log should drop its last line before adding this one.</param>
public sealed record XcodesOutputLine(string Text, bool ReplacesPrevious = false);

public enum XcodesPromptKind
{
    /// <summary>A question this code does not know, such as <c>Enter the number of the Xcode to select:</c>.</summary>
    Unknown,
    AppleId,
    Password,
    /// <summary>The code from one of the account's trusted devices; typing <c>sms</c> asks for a text message instead.</summary>
    TwoFactorCode,
    SmsCode,
    PhoneNumber,
    /// <summary>The URL a federated (single sign-on) account's browser sign-in ends on.</summary>
    FederatedUrl,
    /// <summary>The macOS account's password, which finishing an installation or selecting an Xcode needs.</summary>
    SudoPassword,
    /// <summary>The PIN of the security key an Apple ID signs in with.</summary>
    SecurityKeyPin,
}

/// <summary>A question an operation is waiting on, such as a sign-in or administrator prompt.</summary>
/// <param name="Message">The question, such as <c>Apple ID:</c>.</param>
/// <param name="Context">What was shown with the question, such as the list of trusted phone numbers.</param>
public sealed record XcodesPrompt(XcodesPromptKind Kind, string Message, IReadOnlyList<string> Context)
{
    /// <summary>For the credential dialog only; never included in terminal output or logs.</summary>
    public string? AppleId { get; init; }

    /// <summary>Why the previous answer was refused, already user-facing, or null.</summary>
    public string? Error { get; init; }

    /// <summary>The answer to a <see cref="XcodesPromptKind.TwoFactorCode"/> prompt that asks for a text message instead.</summary>
    public const string TextMessageAnswer = "sms";

    /// <summary>Whether the answer is hidden as it is typed. Other answers can also be sensitive.</summary>
    public bool IsSecret => Kind is XcodesPromptKind.Password or XcodesPromptKind.SudoPassword
        or XcodesPromptKind.SecurityKeyPin;
}

/// <summary>Answers a question an operation asks. Returning null cancels the operation.</summary>
public delegate Task<string?> XcodesPromptHandler(XcodesPrompt prompt, CancellationToken cancellationToken);

/// <summary>Recognises sign-in and administrator questions in text, so logs can redact the answers.</summary>
internal static partial class XcodesPrompts
{
    /// <summary>
    /// What kind of question an unfinished line is, or null when it does not look like one. Known questions
    /// are named; any other line that ends the way a question does is <see cref="XcodesPromptKind.Unknown"/>.
    /// </summary>
    public static XcodesPromptKind? Detect(string pending)
    {
        string text = pending.Trim();
        if (text.Length == 0)
            return null;

        if (AppleId().IsMatch(text)) return XcodesPromptKind.AppleId;
        if (Password().IsMatch(text)) return XcodesPromptKind.Password;
        if (SudoPassword().IsMatch(text)) return XcodesPromptKind.SudoPassword;
        if (TwoFactorCode().IsMatch(text)) return XcodesPromptKind.TwoFactorCode;
        if (SmsCode().IsMatch(text)) return XcodesPromptKind.SmsCode;
        if (PhoneNumber().IsMatch(text)) return XcodesPromptKind.PhoneNumber;
        if (FederatedUrl().IsMatch(text)) return XcodesPromptKind.FederatedUrl;

        // A prompt leaves its cursor after a space; ordinary output ends its lines.
        return pending.EndsWith(": ", StringComparison.Ordinal) || pending.EndsWith("? ", StringComparison.Ordinal)
            ? XcodesPromptKind.Unknown
            : null;
    }

    /// <summary>
    /// Takes the Apple ID out of a line xcodes wrote. xcodes names the account in the password question it
    /// asks when the ID did not come from this run's own question, and again when the password was wrong;
    /// neither the log nor a dialog has any use for it.
    /// </summary>
    public static string Redact(string text)
        => AttemptedAccount().Replace(PasswordAccount().Replace(text, "$1:"), "");

    [GeneratedRegex(@"^Apple ID:$")]
    private static partial Regex AppleId();

    [GeneratedRegex(@"^(\s*Apple ID Password) \(.*\):")]
    private static partial Regex PasswordAccount();

    [GeneratedRegex(@"\s*Attempted to sign in with username .*$")]
    private static partial Regex AttemptedAccount();

    [GeneratedRegex(@"^Apple ID Password(?: \(.+\))?:$")]
    private static partial Regex Password();

    [GeneratedRegex(@"^macOS User Password:$")]
    private static partial Regex SudoPassword();

    [GeneratedRegex(@"^Enter the \d+ digit code from one of your trusted devices:$")]
    private static partial Regex TwoFactorCode();

    [GeneratedRegex(@"^Enter the \d+ digit code sent to .+:$")]
    private static partial Regex SmsCode();

    [GeneratedRegex(@"^Select a trusted phone number to receive a code via SMS:$")]
    private static partial Regex PhoneNumber();

    [GeneratedRegex(@"^Paste the URL here:$")]
    private static partial Regex FederatedUrl();
}
