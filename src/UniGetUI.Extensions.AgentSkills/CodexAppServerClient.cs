using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Extensions.AgentSkills;

internal interface ICodexConfigWriter
{
    Task<string> SetPluginEnabledAsync(string pluginId, bool enabled, CancellationToken token);
}

/// <summary>
/// The Codex CLI has no enable/disable command. The Codex app, its TUI and IDE extensions all toggle plugins through
/// the app-server config API instead, so the same validated, comment-preserving write is used here. The server is
/// started for a single request and stopped again.
/// </summary>
internal sealed class CodexAppServerConfigWriter : ICodexConfigWriter
{
    private readonly Func<string?> _findExecutable;
    private readonly IReadOnlyList<string> _arguments;
    private readonly TimeSpan _timeout;

    public CodexAppServerConfigWriter() : this(() => AgentHostLocator.Find("codex"), ["app-server"], TimeSpan.FromMinutes(1)) { }
    internal CodexAppServerConfigWriter(Func<string?> findExecutable, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        _findExecutable = findExecutable;
        _arguments = arguments;
        _timeout = timeout;
    }

    public async Task<string> SetPluginEnabledAsync(string pluginId, bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string id = AgentPluginInput.PluginId(pluginId);
        string executable = _findExecutable() ?? throw new InvalidOperationException(AgentPluginProcessRunner.NotFoundMessage("Codex"));
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in _arguments) info.ArgumentList.Add(argument);
        info.Environment["NO_COLOR"] = "1";
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(_timeout);
        using var process = new Process { StartInfo = info };
        process.Start();
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            string result = await ExchangeAsync(process.StandardInput, process.StandardOutput, id, enabled, limit.Token,
                () => error.Wait(TimeSpan.FromSeconds(2)) ? error.Result : "");
            process.StandardInput.Close();
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(exit.Token); }
            catch (OperationCanceledException) { Kill(process); }
            return result;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException(CoreTools.Translate("The {0} plugin command timed out. Check the agent and try again.", "Codex"));
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    internal static async Task<string> ExchangeAsync(TextWriter input, TextReader output, string pluginId, bool enabled,
        CancellationToken token, Func<string>? errorOutput = null)
    {
        await SendAsync(input, Initialize(), token);
        await ReadResultAsync(output, 1, token, errorOutput);
        await SendAsync(input, Notification("initialized"), token);
        await SendAsync(input, WritePluginEnabled(pluginId, enabled), token);
        using JsonDocument result = await ReadResultAsync(output, 2, token, errorOutput);
        JsonElement root = result.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(CoreTools.Translate("Codex returned an unexpected response."));
        string status = root.TryGetProperty("status", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
        string file = root.TryGetProperty("filePath", out JsonElement path) && path.ValueKind == JsonValueKind.String
            ? path.GetString() ?? "" : "";
        return status switch
        {
            "ok" => enabled
                ? CoreTools.Translate("Enabled {0} in {1}", pluginId, file)
                : CoreTools.Translate("Disabled {0} in {1}", pluginId, file),
            "okOverridden" => throw new InvalidOperationException(CoreTools.Translate(
                "Codex saved the change, but a managed or higher-priority configuration overrides it.")),
            _ => throw new InvalidDataException(CoreTools.Translate("Codex returned an unexpected response.")),
        };
    }

    /// <summary>Plugin ids are only letters, digits, '-', '_', '.' and '@', so only a '.' needs a quoted key segment.</summary>
    internal static string PluginKeyPath(string pluginId) =>
        "plugins." + (pluginId.Contains('.') ? "\"" + pluginId + "\"" : pluginId);

    private static string Initialize() => Json(writer =>
    {
        writer.WriteNumber("id", 1);
        writer.WriteString("method", "initialize");
        writer.WriteStartObject("params");
        writer.WriteStartObject("clientInfo");
        writer.WriteString("name", "unigetui");
        writer.WriteString("title", "UniGetUI");
        writer.WriteString("version", CoreData.VersionName);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    private static string Notification(string method) => Json(writer => writer.WriteString("method", method));

    private static string WritePluginEnabled(string pluginId, bool enabled) => Json(writer =>
    {
        writer.WriteNumber("id", 2);
        writer.WriteString("method", "config/value/write");
        writer.WriteStartObject("params");
        writer.WriteString("keyPath", PluginKeyPath(pluginId));
        writer.WriteStartObject("value");
        writer.WriteBoolean("enabled", enabled);
        writer.WriteEndObject();
        writer.WriteString("mergeStrategy", "upsert");
        writer.WriteEndObject();
    });

    private static string Json(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task SendAsync(TextWriter input, string message, CancellationToken token)
    {
        await input.WriteLineAsync(message.AsMemory(), token);
        await input.FlushAsync(token);
    }

    /// <summary>Notifications and server requests can arrive first; skip everything but the response to <paramref name="id"/>.</summary>
    private static async Task<JsonDocument> ReadResultAsync(TextReader output, int id, CancellationToken token, Func<string>? errorOutput)
    {
        while (true)
        {
            string? line = await output.ReadLineAsync(token).AsTask().WaitAsync(token);
            if (line is null)
            {
                string detail = errorOutput?.Invoke().Trim() ?? "";
                throw new InvalidOperationException(CoreTools.Translate("Codex app-server stopped before replying. {0}", detail).Trim());
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument message;
            try
            {
                message = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                Logger.Debug("Ignoring a non-JSON line from the Codex app-server");
                continue;
            }
            JsonElement root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("method", out _)
                || !root.TryGetProperty("id", out JsonElement responseId)
                || responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out int value) || value != id)
            {
                message.Dispose();
                continue;
            }
            using (message)
            {
                if (root.TryGetProperty("error", out JsonElement error))
                {
                    string text = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement detail)
                        && detail.ValueKind == JsonValueKind.String ? detail.GetString() ?? "" : error.GetRawText();
                    throw new InvalidOperationException(CoreTools.Translate("Codex rejected the plugin change: {0}", text));
                }
                if (!root.TryGetProperty("result", out JsonElement result))
                    throw new InvalidDataException(CoreTools.Translate("Codex returned an unexpected response."));
                return JsonDocument.Parse(result.GetRawText());
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }
}
