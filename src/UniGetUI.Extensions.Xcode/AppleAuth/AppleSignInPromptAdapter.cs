using System.Globalization;

namespace UniGetUI.Extensions.Xcode.AppleAuth;

/// <summary>Asks the sign-in's questions through the page's prompt dialog. A null answer cancels the sign-in.</summary>
internal sealed class AppleSignInPromptAdapter(XcodesPromptHandler ask) : IAppleSignInPrompts
{
    internal const string TextMessageAnswer = XcodesPrompt.TextMessageAnswer;

    public async Task<AppleCredentials?> AskCredentialsAsync(string? appleId, string? error, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appleId))
        {
            appleId = (await ask(new XcodesPrompt(XcodesPromptKind.AppleId, "Apple ID:", []) { Error = error },
                cancellationToken).ConfigureAwait(false))?.Trim();
            if (string.IsNullOrEmpty(appleId))
                return null;
            error = null;
        }

        string? password = await ask(new XcodesPrompt(XcodesPromptKind.Password, "Password:", [])
        {
            AppleId = appleId,
            Error = error,
        }, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(password) ? null : new AppleCredentials(appleId, password);
    }

    public async Task<AppleCodeAnswer?> AskCodeAsync(AppleCodeRequest request, CancellationToken cancellationToken)
    {
        XcodesPrompt prompt = request.Delivery == AppleCodeDelivery.Sms
            ? new XcodesPrompt(XcodesPromptKind.SmsCode,
                request.Phone is { } phone ? $"Sent to {phone.Number}." : "", [])
            { Error = request.Error }
            : new XcodesPrompt(XcodesPromptKind.TwoFactorCode, "", []) { Error = request.Error };
        string? answer = (await ask(prompt, cancellationToken).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(answer))
            return null;
        if (request.CanRequestSms && string.Equals(answer, TextMessageAnswer, StringComparison.OrdinalIgnoreCase))
            return new AppleCodeAnswer(null, UseSms: true);
        return new AppleCodeAnswer(answer);
    }

    public async Task<AppleTrustedPhone?> ChoosePhoneAsync(IReadOnlyList<AppleTrustedPhone> phones, CancellationToken cancellationToken)
    {
        if (phones.Count == 1)
            return phones[0];
        string[] lines = [.. phones.Select((phone, index) => $"{index + 1}: {phone.Number}")];
        string? error = null;
        while (true)
        {
            string? answer = (await ask(new XcodesPrompt(XcodesPromptKind.PhoneNumber, "", lines) { Error = error },
                cancellationToken).ConfigureAwait(false))?.Trim();
            if (string.IsNullOrEmpty(answer))
                return null;
            if (int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                && number >= 1 && number <= phones.Count)
                return phones[number - 1];
            error = $"Enter a number from 1 to {phones.Count}.";
        }
    }

    public async Task<string?> AskSecurityKeyPinAsync(string? error, CancellationToken cancellationToken)
    {
        string? pin = await ask(new XcodesPrompt(XcodesPromptKind.SecurityKeyPin, "", []) { Error = error },
            cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(pin) ? null : pin;
    }

    public async Task<Uri?> AskFederatedCallbackAsync(Uri signInPage, CancellationToken cancellationToken)
    {
        string? error = null;
        while (true)
        {
            string? answer = (await ask(new XcodesPrompt(XcodesPromptKind.FederatedUrl, "", [signInPage.AbsoluteUri])
            {
                Error = error,
            }, cancellationToken).ConfigureAwait(false))?.Trim();
            if (string.IsNullOrEmpty(answer))
                return null;
            if (Uri.TryCreate(answer, UriKind.Absolute, out Uri? callback) && callback.Scheme == Uri.UriSchemeHttps)
                return callback;
            error = "Paste the full https address the browser ended on.";
        }
    }
}
