// The Notion CLI (`ntn`), which Notion sources go through: it signs in, keeps
// the token in the OS credential store and makes the API calls, so the library
// never handles Notion credentials itself.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Devolutions.AgentSkills;

namespace Skills;

internal enum NotionCliFailure
{
    NotInstalled,
    SignedOut,
    NotFound,
    Forbidden,
    Invalid,
    Timeout,
    Other,
}

internal sealed class NotionCliException(NotionCliFailure failure, string message) : Exception(message)
{
    public NotionCliFailure Failure { get; } = failure;

    /// The public exception, with the failure a host can act on.
    public SkillsException ToSkillsException() => new(Message, Failure switch
    {
        NotionCliFailure.NotInstalled => SkillsFailure.NotionCliMissing,
        NotionCliFailure.SignedOut => SkillsFailure.NotionSignedOut,
        NotionCliFailure.NotFound => SkillsFailure.NotionNotFound,
        _ => SkillsFailure.Other,
    }, this);
}

internal static partial class NotionCli
{
    public const string ApiVersion = "2026-03-11";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(15);

    // Long enough to find the browser, read the code and approve the sign-in
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(15);

    private const int MaxOutputBytes = 10 * 1024 * 1024;

    public const string MissingMessage = "The Notion CLI (ntn) is required for Notion sources. Install it from https://developers.notion.com/cli/get-started/installation";

    public const string SignedOutMessage = "Sign in to Notion to use Notion sources.";

    [GeneratedRegex(@"^[A-Z0-9]{2,}(?:-[A-Z0-9]{2,})+$")]
    private static partial Regex VerificationCode();

    [GeneratedRegex(@"\d+\.\d+\.\d+[0-9A-Za-z.+-]*")]
    private static partial Regex VersionNumber();

    /// Run the Notion CLI with stdin closed. Throws when it cannot be started.
    public static ProcOutput Run(TimeSpan timeout, params string[] args)
    {
        var context = Sys.Context;
        if (context?.NotionRunner is { } fake) return fake(args);
        var program = context?.NotionCli is { Length: > 0 } path ? path : "ntn";
        var command = Proc.Command(program, args).Timeout(timeout).MaxOutput(MaxOutputBytes).HideWindow();
        if (CacheDirectory(OperatingSystem.IsWindows(), Sys.EnvRaw, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) is { } cache)
            command.Env("XDG_CACHE_HOME", cache);
        try
        {
            return command.Output();
        }
        catch (ProcException e)
        {
            throw e.Kind switch
            {
                ProcErrorKind.NotFound => new NotionCliException(NotionCliFailure.NotInstalled, MissingMessage),
                ProcErrorKind.Timeout => new NotionCliException(NotionCliFailure.Timeout, $"The Notion CLI did not answer within {(int)timeout.TotalSeconds} seconds"),
                ProcErrorKind.TooMuchOutput => new NotionCliException(NotionCliFailure.Other, "The Notion CLI's output exceeded 10 MiB"),
                _ => new NotionCliException(NotionCliFailure.Other, $"Unable to start the Notion CLI: {Sanitize.StripTerminalEscapes(e.Message)}"),
            };
        }
    }

    /// The cache directory to give the CLI, or null to leave it alone. On
    /// Windows, ntn 0.23 only finds one through NOTION_HOME, XDG_CACHE_HOME or
    /// HOME, which apps started from Explorer don't have, and its login then
    /// fails with "Could not determine cache directory". Its sign-in state goes
    /// to %LOCALAPPDATA%\notion instead; its configuration stays in
    /// %APPDATA%\notion, where the ntn a user runs in a terminal finds it.
    internal static string? CacheDirectory(bool isWindows, Func<string, string?> env, string localAppData)
    {
        if (!isWindows || localAppData.Length == 0) return null;
        return new[] { "NOTION_HOME", "XDG_CACHE_HOME", "HOME" }.Any(name => !string.IsNullOrEmpty(env(name))) ? null : localAppData;
    }

    /// Call the Notion API through `ntn api`. `body` is a JSON request body.
    public static JsonNode Api(string label, string path, string? method = null, JsonNode? body = null)
    {
        var args = new List<string> { "api", path };
        if (method != null)
        {
            args.Add("-X");
            args.Add(method);
        }
        if (body != null)
        {
            args.Add("-d");
            args.Add(Json.Stringify(body, 0));
        }
        args.Add("--notion-version");
        args.Add(ApiVersion);

        var output = Run(ApiTimeout, [.. args]);
        if (!output.Success) throw Failure(output, label);
        return Json.TryParse(output.StdoutText, out var value) && value != null
            ? value
            : throw new NotionCliException(NotionCliFailure.Other, $"The Notion CLI returned invalid JSON for {label}");
    }

    /// Classify a failed call from its exit code, the error body the API
    /// returned, if any, and the CLI's message.
    public static NotionCliException Failure(ProcOutput output, string label)
    {
        int? status = null;
        string? code = null;
        string? apiMessage = null;
        foreach (var text in new[] { output.StdoutText, output.StderrText })
        {
            if (!Json.TryParse(text.Trim(), out var error) || Json.Str(error, "object") != "error") continue;
            status = Json.AsNumber(Json.Get(error, "status")) is { } s ? (int)s : null;
            code = Json.Str(error, "code");
            apiMessage = Json.NonEmpty(error, "message");
            break;
        }

        // The CLI prints the API's message, not its error code: "error: Public API request failed:
        // Could not find database with ID: …", with exit code 4 when it has no valid token
        var stderr = Sanitize.JsTrim(Sanitize.StripTerminalEscapes(output.StderrText));
        var detail = apiMessage ?? FirstLine(stderr) ?? $"the Notion CLI exited with code {output.Status?.ToString() ?? "unknown"}";
        bool Says(params string[] phrases) => phrases.Any(p => detail.Contains(p, StringComparison.OrdinalIgnoreCase));
        var failure =
            output.Status == 4 || status == 401 || code == "unauthorized" || stderr.Contains("ntn login", StringComparison.Ordinal)
            || Says("API token is invalid")
                ? NotionCliFailure.SignedOut
            : status == 404 || code is "object_not_found" or "directory_not_found" || Says("Could not find", "No skill with that ID", "no longer exists")
                ? NotionCliFailure.NotFound
            : status == 403 || code == "restricted_resource" || Says("Insufficient permissions")
                ? NotionCliFailure.Forbidden
            : status == 400 || code == "validation_error" || Says("is a page, not a database", "not a database", "should be a valid uuid", "failed validation")
                ? NotionCliFailure.Invalid
            : NotionCliFailure.Other;
        return new NotionCliException(failure, failure switch
        {
            NotionCliFailure.SignedOut => SignedOutMessage,
            NotionCliFailure.NotFound => $"Notion could not find {label}, or it is not shared with the Notion CLI's workspace: {detail}",
            _ => $"Notion could not read {label}: {detail}",
        });
    }

    private const string RequestFailed = "Public API request failed:";

    /// The first line of the CLI's error, without its "error:" and
    /// "Public API request failed:" prefixes.
    private static string? FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line == null) return null;
        if (line.StartsWith("error:", StringComparison.OrdinalIgnoreCase)) line = line[6..].Trim();
        if (line.StartsWith(RequestFailed, StringComparison.OrdinalIgnoreCase)) line = line[RequestFailed.Length..].Trim();
        return line;
    }

    // ─── Status and sign-in ───

    public static NotionStatus GetStatus()
    {
        string? version;
        try
        {
            var v = Run(QuickTimeout, "--version");
            version = v.Success && VersionNumber().Match(v.StdoutText) is { Success: true } m ? m.Value : null;
        }
        catch (NotionCliException e) when (e.Failure == NotionCliFailure.NotInstalled)
        {
            return new NotionStatus(NotionCliState.NotInstalled, null, null, null);
        }
        catch (NotionCliException e)
        {
            return new NotionStatus(NotionCliState.Unknown, null, null, e.Message);
        }

        ProcOutput whoami;
        try
        {
            whoami = Run(ApiTimeout, "whoami", "--json", "--notion-version", ApiVersion);
        }
        catch (NotionCliException e)
        {
            return new NotionStatus(NotionCliState.Unknown, version, null, e.Message);
        }

        if (whoami.Success)
        {
            var me = Json.TryParse(whoami.StdoutText, out var n) ? n : null;
            var workspace = Json.NonEmpty(Json.Get(me, "bot"), "workspace_name");
            return new NotionStatus(NotionCliState.SignedIn, version, workspace == null ? null : Sanitize.Metadata(workspace), null);
        }

        var failure = Failure(whoami, "your user");
        return failure.Failure == NotionCliFailure.SignedOut
            ? new NotionStatus(NotionCliState.SignedOut, version, null, null)
            : new NotionStatus(NotionCliState.Unknown, version, null, failure.Message);
    }

    /// Start the two-step sign-in: the CLI registers it and prints the page to
    /// open and the code that page shows.
    public static NotionSignIn BeginSignIn()
    {
        var output = Run(QuickTimeout, "login", "--no-browser");
        if (!output.Success) throw Failure(output, "the sign-in");
        return ParseSignIn(output.StdoutText)
            ?? throw new NotionCliException(NotionCliFailure.Other, "The Notion CLI did not print a sign-in address and code");
    }

    /// The sign-in page and code from `ntn login --no-browser`. Only a Notion
    /// page is accepted, since the host opens it in the user's browser.
    public static NotionSignIn? ParseSignIn(string stdout)
    {
        Uri? url = null;
        string? code = null;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = Sanitize.StripTerminalEscapes(raw).Trim();
            if (url == null && line.StartsWith("https://", StringComparison.Ordinal) && Uri.TryCreate(line, UriKind.Absolute, out var u))
                url = u;
            else if (code == null && VerificationCode().IsMatch(line))
                code = line;
        }
        if (url == null || !IsNotionHost(url.Host) || url.UserInfo.Length > 0) return null;
        code ??= QueryValue(url, "verificationCode") is { } q && VerificationCode().IsMatch(q) ? q : null;
        return code == null ? null : new NotionSignIn(url, code);
    }

    /// Wait for the user to approve the sign-in started by <see cref="BeginSignIn"/>.
    public static void CompleteSignIn()
    {
        var output = Run(SignInTimeout, "login", "poll");
        if (!output.Success) throw Failure(output, "the sign-in");
    }

    public static void SignOut()
    {
        var output = Run(QuickTimeout, "logout");
        if (!output.Success) throw Failure(output, "the sign-out");
    }

    public static bool IsNotionHost(string host)
    {
        host = host.ToLowerInvariant();
        return host is "notion.com" or "notion.so" or "notion.site"
            || host.EndsWith(".notion.com", StringComparison.Ordinal)
            || host.EndsWith(".notion.so", StringComparison.Ordinal)
            || host.EndsWith(".notion.site", StringComparison.Ordinal);
    }

    private static string? QueryValue(Uri url, string key)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            if (i > 0 && pair[..i] == key) return Uri.UnescapeDataString(pair[(i + 1)..]);
        }
        return null;
    }
}
