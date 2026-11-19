using System.Text.Json;
using System.Text.Json.Serialization;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.Data;

public sealed class ThirdPartyNotice
{
    public string Text { get; init; } = "";
}

public sealed class ThirdPartyComponent
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string License { get; init; } = "";
    public string Source { get; init; } = "";
    public string Copyright { get; init; } = "";
    public string LicenseText { get; init; } = "";
    public ThirdPartyNotice? Notice { get; init; }
}

public sealed class ThirdPartyNoticesDocument
{
    public string AppName { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public List<ThirdPartyComponent> Components { get; init; } = [];
}

public static class ThirdPartyNotices
{
    public const string TextFileName = "THIRD-PARTY-NOTICES.txt";
    public const string JsonFileName = "THIRD-PARTY-NOTICES.json";

    public static string TextFilePath => Path.Join(CoreData.UniGetUIExecutableDirectory, TextFileName);
    public static string JsonFilePath => Path.Join(CoreData.UniGetUIExecutableDirectory, JsonFileName);

    public static IReadOnlyList<ThirdPartyComponent> Load() => Load(JsonFilePath);

    public static IReadOnlyList<ThirdPartyComponent> Load(string jsonPath)
    {
        try
        {
            if (!File.Exists(jsonPath))
            {
                Logger.Warn($"Third-party notices not found at {jsonPath}");
                return [];
            }

            using FileStream stream = File.OpenRead(jsonPath);
            ThirdPartyNoticesDocument? document = JsonSerializer.Deserialize(
                stream,
                ThirdPartyNoticesJsonContext.Default.ThirdPartyNoticesDocument
            );
            return document?.Components
                    .Where(component => !string.IsNullOrWhiteSpace(component.Name))
                    .OrderBy(component => component.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                ?? [];
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not read the third-party notices at {jsonPath}");
            Logger.Error(ex);
            return [];
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ThirdPartyNoticesDocument))]
internal sealed partial class ThirdPartyNoticesJsonContext : JsonSerializerContext;
