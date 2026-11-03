using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Managers.DotNetManager;

internal sealed record NuGetCredential(string Username, string Password);

internal sealed class NuGetSourceMapping
{
    private readonly IReadOnlyDictionary<string, string[]> _patternsBySource;

    public NuGetSourceMapping(IReadOnlyDictionary<string, string[]> patternsBySource)
    {
        _patternsBySource = patternsBySource;
    }

    public static NuGetSourceMapping None { get; } =
        new(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));

    public bool IsEnabled => _patternsBySource.Count > 0;

    public IReadOnlySet<string> SourcesFor(string packageId)
    {
        HashSet<string> best = new(StringComparer.OrdinalIgnoreCase);
        int bestScore = -1;

        foreach (var (source, patterns) in _patternsBySource)
        {
            foreach (string pattern in patterns)
            {
                int score = Score(pattern, packageId);
                if (score < 0 || score < bestScore)
                    continue;

                if (score > bestScore)
                {
                    best.Clear();
                    bestScore = score;
                }

                best.Add(source);
            }
        }

        return best;
    }

    private static int Score(string pattern, string packageId)
    {
        if (!pattern.EndsWith('*'))
            return string.Equals(pattern, packageId, StringComparison.OrdinalIgnoreCase)
                ? int.MaxValue
                : -1;

        string prefix = pattern[..^1];
        return packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? prefix.Length : -1;
    }
}

internal sealed record NuGetConfigSettings(
    NuGetSourceMapping Mapping,
    IReadOnlyDictionary<string, NuGetCredential> Credentials
);

internal static partial class NuGetConfigFiles
{
    private static readonly byte[] PasswordEntropy = Encoding.UTF8.GetBytes("NuGet");

    public static NuGetConfigSettings Read(IReadOnlyList<string> pathsByPriority)
    {
        Dictionary<string, string[]> mapping = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, NuGetCredential?> credentials = new(StringComparer.OrdinalIgnoreCase);
        bool mappingCleared = false;
        bool credentialsCleared = false;

        foreach (string path in pathsByPriority)
        {
            XElement? root = Load(path);
            if (root is null)
                continue;

            if (!mappingCleared && root.Element("packageSourceMapping") is { } mappingSection)
                mappingCleared = ReadMapping(mappingSection, mapping);

            if (
                !credentialsCleared
                && root.Element("packageSourceCredentials") is { } credentialsSection
            )
                credentialsCleared = ReadCredentials(credentialsSection, credentials);
        }

        Dictionary<string, NuGetCredential> usable = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, credential) in credentials)
        {
            if (credential is not null)
                usable[key] = credential;
        }

        return new NuGetConfigSettings(new NuGetSourceMapping(mapping), usable);
    }

    private static XElement? Load(string path)
    {
        try
        {
            using XmlReader reader = XmlReader.Create(
                path,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }
            );
            return XDocument.Load(reader).Root;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read the NuGet configuration file {path}");
            Logger.Warn(ex);
            return null;
        }
    }

    private static bool ReadMapping(XElement section, Dictionary<string, string[]> mapping)
    {
        Dictionary<string, string[]> fromThisFile = new(StringComparer.OrdinalIgnoreCase);
        bool cleared = false;

        foreach (XElement entry in section.Elements())
        {
            if (entry.Name.LocalName == "clear")
            {
                fromThisFile.Clear();
                cleared = true;
                continue;
            }

            if (entry.Name.LocalName != "packageSource" || entry.Attribute("key")?.Value is not { } key)
                continue;

            fromThisFile[key] =
            [
                .. entry
                    .Elements("package")
                    .Select(package => package.Attribute("pattern")?.Value?.Trim())
                    .OfType<string>()
                    .Where(pattern => pattern.Length > 0),
            ];
        }

        foreach (var (key, patterns) in fromThisFile)
            mapping.TryAdd(key, patterns);

        return cleared;
    }

    private static bool ReadCredentials(
        XElement section,
        Dictionary<string, NuGetCredential?> credentials
    )
    {
        Dictionary<string, NuGetCredential?> fromThisFile = new(StringComparer.OrdinalIgnoreCase);
        bool cleared = false;

        foreach (XElement entry in section.Elements())
        {
            if (entry.Name.LocalName == "clear")
            {
                fromThisFile.Clear();
                cleared = true;
                continue;
            }

            fromThisFile[XmlConvert.DecodeName(entry.Name.LocalName)] = ReadBasicCredential(entry);
        }

        foreach (var (key, credential) in fromThisFile)
            credentials.TryAdd(key, credential);

        return cleared;
    }

    private static NuGetCredential? ReadBasicCredential(XElement entry)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement add in entry.Elements("add"))
        {
            if (add.Attribute("key")?.Value is { } name && add.Attribute("value")?.Value is { } value)
                values[name] = Environment.ExpandEnvironmentVariables(value);
        }

        if (
            values.TryGetValue("ValidAuthenticationTypes", out string? authTypes)
            && !authTypes
                .Split(',', StringSplitOptions.TrimEntries)
                .Contains("basic", StringComparer.OrdinalIgnoreCase)
        )
            return null;

        if (!values.TryGetValue("Username", out string? username))
            return null;

        string? password = values.TryGetValue("ClearTextPassword", out string? clearText)
            ? clearText
            : values.TryGetValue("Password", out string? encrypted)
                ? DecryptPassword(encrypted)
                : null;

        return password is null ? null : new NuGetCredential(username, password);
    }

    private static string? DecryptPassword(string encrypted)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(encrypted)));
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not decrypt a password stored in the NuGet configuration");
            Logger.Warn(ex);
            return null;
        }
    }

    internal static unsafe byte[] Unprotect(byte[] protectedBytes)
    {
        fixed (byte* input = protectedBytes)
        fixed (byte* entropy = PasswordEntropy)
        {
            DataBlob inputBlob = new() { Size = protectedBytes.Length, Data = (IntPtr)input };
            DataBlob entropyBlob = new() { Size = PasswordEntropy.Length, Data = (IntPtr)entropy };

            if (
                !CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out DataBlob output
                )
            )
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());

            try
            {
                byte[] result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
    }

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut
    );

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
