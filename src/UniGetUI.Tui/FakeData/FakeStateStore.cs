using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniGetUI.Tui.FakeData;

internal sealed class FakeInstalledPackage
{
    public string Manager { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string Location { get; set; } = "";
}

internal sealed class FakeSourceEntry
{
    public string Manager { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

internal sealed class FakeState
{
    public List<FakeInstalledPackage> Installed { get; set; } = [];
    public List<FakeSourceEntry> Sources { get; set; } = [];
    public List<string> OperationJournal { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FakeState))]
internal sealed partial class FakeStateJsonContext : JsonSerializerContext;

/// <summary>
/// The on-disk "system" the fake package managers act on. It plays the role a real manager's
/// installed-package database plays: the fake CLI process (<see cref="FakePackageManagerProcess"/>)
/// mutates it, and the in-process fake managers read it back when they list packages, exactly as a
/// real manager's list command reflects what its install command did. It lives in the fake-data
/// sandbox directory, never anywhere a real package manager looks.
/// </summary>
internal static class FakeStateStore
{
    private static readonly object _gate = new();

    public static FakeState Read(string path)
    {
        lock (_gate)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return CreateSeed();
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return JsonSerializer.Deserialize(stream, FakeStateJsonContext.Default.FakeState) ?? CreateSeed();
                }
                catch (IOException) when (attempt < 40)
                {
                    Thread.Sleep(25);
                }
            }
        }
    }

    /// <summary>Applies <paramref name="mutate"/> under an exclusive file lock (safe across processes).</summary>
    public static FakeState Update(string path, Action<FakeState> mutate)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    FakeState state = stream.Length == 0
                        ? CreateSeed()
                        : JsonSerializer.Deserialize(stream, FakeStateJsonContext.Default.FakeState) ?? CreateSeed();
                    mutate(state);
                    stream.SetLength(0);
                    stream.Position = 0;
                    JsonSerializer.Serialize(stream, state, FakeStateJsonContext.Default.FakeState);
                    return state;
                }
                catch (IOException) when (attempt < 200)
                {
                    Thread.Sleep(25);
                }
            }
        }
    }

    /// <summary>Writes the initial data set if the sandbox has no state yet.</summary>
    public static void EnsureSeeded(string path)
    {
        if (File.Exists(path)) return;
        Update(path, _ => { });
    }

    public static FakeState CreateSeed()
    {
        var state = new FakeState();
        foreach (var (manager, id, version, source) in FakeCatalog.InitiallyInstalled)
        {
            state.Installed.Add(new FakeInstalledPackage
            {
                Manager = manager,
                Id = id,
                Version = version,
                Source = source,
            });
        }

        foreach (FakeCatalogSource source in FakeCatalog.DefaultSources)
        {
            state.Sources.Add(new FakeSourceEntry { Manager = source.Manager, Name = source.Name, Url = source.Url });
        }

        return state;
    }
}
