using System.Text;
using System.Text.RegularExpressions;

namespace UniGetUI.Extensions.Xcode;

/// <summary>One finished line of what xcodes wrote to its terminal.</summary>
/// <param name="ReplacesPrevious">xcodes moved the cursor up and wrote over the previous line, as it does for
/// download progress; whoever keeps a log should drop its last line before adding this one.</param>
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
}

/// <summary>A question xcodes is waiting on.</summary>
/// <param name="Message">The question as xcodes wrote it, such as <c>Apple ID:</c>.</param>
/// <param name="Context">What xcodes wrote since the last question, such as the list of trusted phone numbers.</param>
public sealed record XcodesPrompt(XcodesPromptKind Kind, string Message, IReadOnlyList<string> Context)
{
    /// <summary>Whether the answer must not be shown as it is typed, or kept.</summary>
    public bool IsSecret => Kind is XcodesPromptKind.Password or XcodesPromptKind.SudoPassword;
}

/// <summary>Answers a question xcodes asks. Returning null cancels the command.</summary>
public delegate Task<string?> XcodesPromptHandler(XcodesPrompt prompt, CancellationToken cancellationToken);

/// <summary>
/// Turns the bytes a terminal would draw into the lines a log wants: escape sequences are dropped, a carriage
/// return rewinds the line, a backspace removes a character and a cursor-up marks the next line as
/// overwriting the previous one. The text of an unfinished line is kept in <see cref="Pending"/>, which is
/// where a question xcodes asks sits while it waits.
/// </summary>
internal sealed class XcodesTerminalDecoder
{
    private enum State
    {
        Text,
        Escape,
        ControlSequence,
        OperatingSystemCommand,
        OperatingSystemCommandEscape,
    }

    private readonly StringBuilder _line = new();
    private readonly StringBuilder _sequence = new();
    private State _state;
    private bool _carriageReturn;
    private bool _replacesPrevious;

    public string Pending => _line.ToString();

    public bool HasPending => _line.Length > 0;

    public void ClearPending()
    {
        _line.Clear();
        _carriageReturn = false;
        _replacesPrevious = false;
    }

    public IReadOnlyList<XcodesOutputLine> Feed(string chunk)
    {
        List<XcodesOutputLine> lines = [];
        foreach (char c in chunk)
        {
            switch (_state)
            {
                case State.Escape:
                    if (c == '[')
                    {
                        _sequence.Clear();
                        _state = State.ControlSequence;
                    }
                    else if (c == ']')
                    {
                        _state = State.OperatingSystemCommand;
                    }
                    else if (c < ' ')
                    {
                        // Not part of a sequence at all: the escape was stray.
                        _state = State.Text;
                        Character(c, lines);
                    }
                    else if (c > '/') // An intermediate byte (0x20-0x2F) keeps the sequence going.
                    {
                        _state = State.Text;
                    }
                    break;

                case State.ControlSequence:
                    if (c is >= '@' and <= '~')
                    {
                        EndControlSequence(c);
                        _state = State.Text;
                    }
                    else
                    {
                        _sequence.Append(c);
                    }
                    break;

                case State.OperatingSystemCommand:
                    if (c == '\a') _state = State.Text;
                    else if (c == '\x1B') _state = State.OperatingSystemCommandEscape;
                    break;

                case State.OperatingSystemCommandEscape:
                    _state = State.Text;
                    break;

                default:
                    Character(c, lines);
                    break;
            }
        }
        return lines;
    }

    /// <summary>Returns what is left of an unfinished line when the output ends.</summary>
    public XcodesOutputLine? Flush()
    {
        if (_line.Length == 0)
            return null;
        var line = new XcodesOutputLine(_line.ToString().TrimEnd(), _replacesPrevious);
        ClearPending();
        return line;
    }

    private void Character(char c, List<XcodesOutputLine> lines)
    {
        switch (c)
        {
            case '\x1B':
                _state = State.Escape;
                return;
            case '\r':
                _carriageReturn = true;
                return;
            case '\n':
                lines.Add(new XcodesOutputLine(_line.ToString().TrimEnd(), _replacesPrevious));
                ClearPending();
                return;
            case '\b':
                // script echoes an end-of-file as ^D followed by two backspaces.
                if (_line.Length > 0 && !_carriageReturn)
                    _line.Length--;
                return;
        }

        if (char.IsControl(c) && c != '\t')
            return;
        if (_carriageReturn)
        {
            _line.Clear();
            _carriageReturn = false;
        }
        _line.Append(c);
    }

    private void EndControlSequence(char final)
    {
        string parameters = _sequence.ToString();
        _sequence.Clear();
        switch (final)
        {
            case 'A': // Cursor up: what follows is written over a line that was already finished.
                _replacesPrevious = true;
                break;
            case 'K': // Erase in line.
                if (parameters is "" or "0")
                {
                    if (_carriageReturn)
                    {
                        _line.Clear();
                        _carriageReturn = false;
                    }
                }
                else if (parameters is "1" or "2")
                {
                    _line.Clear();
                    _carriageReturn = false;
                }
                break;
        }
    }
}

/// <summary>Recognises the questions xcodes asks, from the unfinished line it is waiting at.</summary>
internal static partial class XcodesPrompts
{
    /// <summary>
    /// What kind of question an unfinished line is, or null when it does not look like one. Questions xcodes
    /// asks are named; any other line that ends the way a question does is <see cref="XcodesPromptKind.Unknown"/>.
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
