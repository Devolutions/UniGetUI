using System.Diagnostics;
using System.Text;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Extensions.AgentSkills;

internal interface IAgentPluginRunner
{
    Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token);
}

internal sealed class AgentPluginProcessRunner : IAgentPluginRunner
{
    private readonly Func<string?> _findExecutable;
    private readonly TimeSpan _timeout;
    private readonly string _tool;
    public AgentPluginProcessRunner() : this(FindExecutable, TimeSpan.FromMinutes(3)) { }
    internal AgentPluginProcessRunner(Func<string?> findExecutable, TimeSpan timeout, string tool = "Claude")
    {
        _findExecutable = findExecutable;
        _timeout = timeout;
        _tool = tool;
    }

    internal static string? FindExecutable()
        => FindNativeExecutable("claude");

    internal static string? FindNativeExecutable(string tool) => AgentHostLocator.Find(tool);

    internal static string NotFoundMessage(string tool) => tool switch
    {
        "Claude" => CoreTools.Translate("Claude Code was not found. Install the Claude Code CLI or Claude Desktop, then refresh."),
        "Codex" => CoreTools.Translate("Codex was not found. Install the Codex CLI or the Codex app, then refresh."),
        _ => CoreTools.Translate("{0} was not found. Install it and refresh to manage plugins.", tool),
    };

    public async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string executable = _findExecutable() ?? throw new InvalidOperationException(NotFoundMessage(_tool));
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["NO_COLOR"] = "1";
        if (_tool == "Git")
        {
            info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            info.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
            info.Environment["GIT_ALLOW_PROTOCOL"] = "https";
            info.Environment["GIT_CONFIG_COUNT"] = "3";
            info.Environment["GIT_CONFIG_KEY_0"] = "core.hooksPath";
            info.Environment["GIT_CONFIG_VALUE_0"] = "";
            info.Environment["GIT_CONFIG_KEY_1"] = "filter.lfs.required";
            info.Environment["GIT_CONFIG_VALUE_1"] = "false";
            info.Environment["GIT_CONFIG_KEY_2"] = "filter.lfs.smudge";
            info.Environment["GIT_CONFIG_VALUE_2"] = "";
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(_timeout);
        using var process = new Process { StartInfo = info };
        process.Start();
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync(limit.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(limit.Token), output, error).WaitAsync(limit.Token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(_tool == "Claude"
                    ? CoreTools.Translate("Claude plugin command failed (exit {0}): {1}", process.ExitCode,
                        (output.Result + Environment.NewLine + error.Result).Trim())
                    : CoreTools.Translate("{0} plugin command failed (exit {1}): {2}", _tool, process.ExitCode,
                        (output.Result + Environment.NewLine + error.Result).Trim()));
            if (!string.IsNullOrWhiteSpace(error.Result))
                Logger.Warn(error.Result.Trim());
            return output.Result.Trim();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException(_tool == "Claude"
                ? CoreTools.Translate("The Claude plugin command timed out. Check Claude Code or Claude Desktop and try again.")
                : CoreTools.Translate("The {0} plugin command timed out. Check the agent and try again.", _tool));
        }
    }
}
