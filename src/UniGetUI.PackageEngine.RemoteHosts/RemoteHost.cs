using System.Text.Json.Serialization;

namespace UniGetUI.PackageEngine.RemoteHosts;

public enum RemoteHostKind
{
    Ssh = 0,
    Wsl = 1,
}

public sealed class RemoteHost : IEquatable<RemoteHost>
{
    public const int MaxDestinationLength = 255;

    public Guid Id { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    public string Destination { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RemoteHostKind Kind { get; init; } = RemoteHostKind.Ssh;

    public RemoteHost() { }

    public RemoteHost(string destination, string? name = null, Guid? id = null, RemoteHostKind kind = RemoteHostKind.Ssh)
    {
        Kind = kind;
        Destination = NormalizeDestination(destination);
        if (!IsValidDestination(Destination))
            throw new RemoteHostException(RemoteHostErrorKind.InvalidDestination);

        string? trimmedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Name = trimmedName;
        Id = id ?? Guid.NewGuid();
    }

    public static RemoteHost ForWsl(string distroName)
        => new(distroName, name: null, id: WslDistroCatalog.CreateHostId(distroName), kind: RemoteHostKind.Wsl);

    public static RemoteHost ForWsl(WslDistroInfo distro)
        => ForWsl(distro.Name);

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (Kind == RemoteHostKind.Wsl)
            {
                string distro = Destination.Length == 0 ? "WSL" : Destination;
                string pretty = char.ToUpperInvariant(distro[0]) + distro[1..];
                return pretty + " (WSL)";
            }

            string value = DroppingLocalSuffix(Name ?? Destination);
            if (value.Length == 0)
                return Destination;
            return char.ToUpperInvariant(value[0]) + value[1..];
        }
    }

    public static string NormalizeDestination(string destination)
        => destination.Trim();

    public static bool IsValidDestination(string destination)
    {
        if (string.IsNullOrEmpty(destination)
            || destination.Length > MaxDestinationLength
            || destination.StartsWith('-'))
        {
            return false;
        }

        foreach (char c in destination)
        {
            if (char.IsAsciiLetterOrDigit(c))
                continue;
            if (c is '.' or '_' or '@' or ':' or '%' or '+' or '-' or '[' or ']')
                continue;
            return false;
        }

        return true;
    }

    private static string DroppingLocalSuffix(string value)
        => value.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            ? value[..^".local".Length]
            : value;

    public bool Equals(RemoteHost? other)
        => other is not null && Id == other.Id;

    public override bool Equals(object? obj) => Equals(obj as RemoteHost);

    public override int GetHashCode() => Id.GetHashCode();
}

public enum RemoteHostErrorKind
{
    InvalidDestination,
    DuplicateDestination,
}

public sealed class RemoteHostException : Exception
{
    public RemoteHostErrorKind Kind { get; }

    public RemoteHostException(RemoteHostErrorKind kind)
        : base(kind switch
        {
            RemoteHostErrorKind.InvalidDestination =>
                "Enter an SSH host or alias without options or spaces, such as linux-box or user@server.",
            RemoteHostErrorKind.DuplicateDestination =>
                "A remote host with this SSH destination already exists.",
            _ => "The remote host configuration is invalid.",
        })
    {
        Kind = kind;
    }
}
