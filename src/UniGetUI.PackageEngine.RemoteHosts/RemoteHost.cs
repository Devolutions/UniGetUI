using System.Text.Json.Serialization;

namespace UniGetUI.PackageEngine.RemoteHosts;

public sealed class RemoteHost : IEquatable<RemoteHost>
{
    public const int MaxDestinationLength = 255;

    public Guid Id { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    public string Destination { get; init; } = "";

    public RemoteHost() { }

    public RemoteHost(string destination, string? name = null, Guid? id = null)
    {
        Destination = NormalizeDestination(destination);
        if (!IsValidDestination(Destination))
            throw new RemoteHostException(RemoteHostErrorKind.InvalidDestination);

        string? trimmedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Name = trimmedName;
        Id = id ?? Guid.NewGuid();
    }

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
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
