using System.Runtime.InteropServices;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

internal sealed record MsiInfo(IReadOnlyDictionary<string, string> Properties, string? Template, byte[]? Icon = null,
    string? ExternalMedia = null)
{
    public string? Property(string name) =>
        Properties.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

internal static unsafe partial class MsiDatabase
{
    private const uint Success = 0;
    private const uint MoreData = 234;
    private const uint TemplateProperty = 7;
    private const int GuidLength = 38;
    private const int MaxIconBytes = 4 * 1024 * 1024;

    public static MsiInfo Read(string path)
    {
        uint result = MsiOpenDatabase(path, 0, out nint database);
        if (result != Success)
            throw new InvalidDataException($"It could not be opened as a Windows Installer package (error {result}).");
        try
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            if (MsiDatabaseOpenView(database, "SELECT `Property`, `Value` FROM `Property`", out nint view) != Success)
                throw new InvalidDataException("It has no Property table.");
            try
            {
                if (MsiViewExecute(view, 0) != Success)
                    throw new InvalidDataException("Its Property table could not be read.");
                while (MsiViewFetch(view, out nint record) == Success)
                {
                    try
                    {
                        string? name = RecordString(record, 1);
                        if (!string.IsNullOrEmpty(name) && properties.Count < 10000)
                            properties[name] = RecordString(record, 2) ?? "";
                    }
                    finally { MsiCloseHandle(record); }
                }
            }
            finally { MsiCloseHandle(view); }
            return new MsiInfo(properties, SummaryString(database, TemplateProperty),
                ReadIcon(database, properties.GetValueOrDefault("ARPPRODUCTICON")), ExternalMedia(database));
        }
        finally { MsiCloseHandle(database); }
    }

    private static byte[]? ReadIcon(nint database, string? productIcon)
    {
        IEnumerable<string> names = productIcon is null ? [] : [productIcon];
        names = names.Concat(FirstColumn(database, "SELECT `Icon_` FROM `Shortcut`"))
            .Concat(FirstColumn(database, "SELECT `Name` FROM `Icon`"));
        foreach (string name in names.Where(IsIdentifier).Distinct(StringComparer.Ordinal).Take(16))
        {
            if (IconData(database, name) is { } data && PeReader.IconFromFile(data) is { } icon)
                return icon;
        }
        return null;
    }

    private static string? ExternalMedia(nint database)
    {
        var cabinets = FirstColumn(database, "SELECT `Cabinet` FROM `Media`");
        if (cabinets.FirstOrDefault(c => !c.StartsWith('#')) is { } external)
            return external;
        return cabinets.Count == 0 && FirstColumn(database, "SELECT `File` FROM `File`").Count > 0 ? "uncompressed files" : null;
    }

    private static bool IsIdentifier(string name) =>
        name.Length is > 0 and <= 72 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.');

    private static List<string> FirstColumn(nint database, string query)
    {
        var values = new List<string>();
        if (MsiDatabaseOpenView(database, query, out nint view) != Success)
            return values;
        try
        {
            if (MsiViewExecute(view, 0) != Success)
                return values;
            while (values.Count < 16 && MsiViewFetch(view, out nint record) == Success)
            {
                try
                {
                    if (RecordString(record, 1) is { Length: > 0 } value)
                        values.Add(value);
                }
                finally { MsiCloseHandle(record); }
            }
        }
        finally { MsiCloseHandle(view); }
        return values;
    }

    private static byte[]? IconData(nint database, string name)
    {
        if (MsiDatabaseOpenView(database, $"SELECT `Data` FROM `Icon` WHERE `Name` = '{name}'", out nint view) != Success)
            return null;
        try
        {
            if (MsiViewExecute(view, 0) != Success || MsiViewFetch(view, out nint record) != Success)
                return null;
            try
            {
                using var data = new MemoryStream();
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    uint read = (uint)buffer.Length;
                    uint result;
                    fixed (byte* pointer = buffer)
                        result = MsiRecordReadStream(record, 1, pointer, ref read);
                    if (result != Success)
                        return null;
                    if (read == 0)
                        return data.ToArray();
                    if (data.Length + read > MaxIconBytes)
                        return null;
                    data.Write(buffer, 0, (int)read);
                }
            }
            finally { MsiCloseHandle(record); }
        }
        finally { MsiCloseHandle(view); }
    }

    private static string? RecordString(nint record, uint field)
    {
        uint size = 0;
        char empty = '\0';
        uint result = MsiRecordGetString(record, field, &empty, ref size);
        if (result is not (Success or MoreData))
            return null;
        char[] buffer = new char[++size];
        fixed (char* pointer = buffer)
            result = MsiRecordGetString(record, field, pointer, ref size);
        return result == Success ? new string(buffer, 0, (int)size) : null;
    }

    private static string? SummaryString(nint database, uint property)
    {
        if (MsiGetSummaryInformation(database, null, 0, out nint summary) != Success)
            return null;
        try
        {
            uint size = 0;
            char empty = '\0';
            uint result = MsiSummaryInfoGetProperty(summary, property, out _, out _, out _, &empty, ref size);
            if (result is not (Success or MoreData))
                return null;
            char[] buffer = new char[++size];
            fixed (char* pointer = buffer)
                result = MsiSummaryInfoGetProperty(summary, property, out _, out _, out _, pointer, ref size);
            return result == Success ? new string(buffer, 0, (int)size) : null;
        }
        finally { MsiCloseHandle(summary); }
    }

    public static IReadOnlyList<string> RelatedProducts(string upgradeCode)
    {
        var products = new List<string>();
        char* buffer = stackalloc char[GuidLength + 1];
        for (uint index = 0; index < 256; index++)
        {
            uint result = MsiEnumRelatedProducts(upgradeCode, 0, index, buffer);
            if (result != Success)
                break;
            products.Add(new string(buffer, 0, GuidLength));
        }
        return products;
    }

    public static string? ProductInfo(string productCode, string property)
    {
        uint size = 0;
        char empty = '\0';
        uint result = MsiGetProductInfo(productCode, property, &empty, ref size);
        if (result is not (Success or MoreData))
            return null;
        char[] buffer = new char[++size];
        fixed (char* pointer = buffer)
            result = MsiGetProductInfo(productCode, property, pointer, ref size);
        return result == Success ? new string(buffer, 0, (int)size) : null;
    }

    [LibraryImport("msi.dll", EntryPoint = "MsiOpenDatabaseW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiOpenDatabase(string databasePath, nint persist, out nint database);

    [LibraryImport("msi.dll", EntryPoint = "MsiDatabaseOpenViewW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiDatabaseOpenView(nint database, string query, out nint view);

    [LibraryImport("msi.dll")]
    private static partial uint MsiViewExecute(nint view, nint record);

    [LibraryImport("msi.dll")]
    private static partial uint MsiViewFetch(nint view, out nint record);

    [LibraryImport("msi.dll", EntryPoint = "MsiRecordGetStringW")]
    private static partial uint MsiRecordGetString(nint record, uint field, char* value, ref uint size);

    [LibraryImport("msi.dll")]
    private static partial uint MsiRecordReadStream(nint record, uint field, byte* data, ref uint size);

    [LibraryImport("msi.dll", EntryPoint = "MsiGetSummaryInformationW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiGetSummaryInformation(nint database, string? databasePath, uint updateCount, out nint summary);

    [LibraryImport("msi.dll", EntryPoint = "MsiSummaryInfoGetPropertyW")]
    private static partial uint MsiSummaryInfoGetProperty(nint summary, uint property, out uint dataType, out int integer,
        out long fileTime, char* value, ref uint size);

    [LibraryImport("msi.dll", EntryPoint = "MsiEnumRelatedProductsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, char* productCode);

    [LibraryImport("msi.dll", EntryPoint = "MsiGetProductInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiGetProductInfo(string product, string property, char* value, ref uint size);

    [LibraryImport("msi.dll")]
    private static partial uint MsiCloseHandle(nint handle);
}
