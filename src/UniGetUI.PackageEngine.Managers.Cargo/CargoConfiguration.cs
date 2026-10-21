using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal static class CargoConfiguration
{
    internal static string WorkingDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    internal static string Home =>
        Path.GetFullPath(Environment.GetEnvironmentVariable("CARGO_HOME")
            is { Length: > 0 } home ? home : Path.Join(WorkingDirectory, ".cargo"), WorkingDirectory);

    internal static TomlTable ReadConfiguration()
    {
        string legacy = Path.Join(Home, "config");
        string path = File.Exists(legacy) ? legacy : Path.Join(Home, "config.toml");
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : new TomlTable();
    }

    internal static TomlTable Parse(string text) =>
        TomlSerializer.Deserialize(text, CargoTomlContext.Default.TomlTable)
        ?? throw new InvalidDataException("Cargo TOML document was empty.");

    internal static string GetRoot(string? explicitRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return Path.GetFullPath(explicitRoot, WorkingDirectory);
        if (Environment.GetEnvironmentVariable("CARGO_INSTALL_ROOT") is { Length: > 0 } root)
            return Path.GetFullPath(root, WorkingDirectory);
        var configuration = ReadConfiguration();
        if (configuration.TryGetValue("install", out object? installValue))
        {
            if (installValue is not TomlTable install)
                throw new InvalidDataException("Cargo install configuration must be a table.");
            if (install.TryGetValue("root", out object? configured))
            {
                if (configured is not string { Length: > 0 } configuredRoot)
                    throw new InvalidDataException("Cargo install.root configuration must be a nonempty path.");
                return Path.GetFullPath(configuredRoot, Path.GetDirectoryName(Home) ?? Home);
            }
        }
        return Home;
    }

    internal static TomlTable? Table(TomlTable table, string key) =>
        table.TryGetValue(key, out object? value) ? value as TomlTable : null;

    internal static string? String(TomlTable table, string key) =>
        table.TryGetValue(key, out object? value) ? value as string : null;

    internal static bool HasBuildOverrides()
    {
        TomlTable configuration = ReadConfiguration();
        return Environment.GetEnvironmentVariable("CARGO_BUILD_TARGET") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("RUSTFLAGS") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("CARGO_ENCODED_RUSTFLAGS") is { Length: > 0 }
            || Table(configuration, "build") is { Count: > 0 }
            || Table(configuration, "target") is { Count: > 0 }
            || Table(configuration, "source") is { Count: > 0 }
            || Table(configuration, "registries") is { Count: > 0 }
            || Table(configuration, "registry") is { Count: > 0 }
            || configuration.ContainsKey("include");
    }

    internal static void RequireRegularPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null;
            current = Path.GetDirectoryName(current))
        {
            if (Path.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Cargo installation path contains a link: {current}");
        }
    }
}

[TomlSerializable(typeof(TomlTable))]
internal partial class CargoTomlContext : TomlSerializerContext;
