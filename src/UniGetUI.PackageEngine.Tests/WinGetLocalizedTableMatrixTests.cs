#if WINDOWS
using System.Text;
using UniGetUI.Core.Data;
using UniGetUI.PackageEngine.Managers.WingetManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;

namespace UniGetUI.PackageEngine.Tests;

[Collection(WinGetManagerTestCollection.Name)]
public sealed class WinGetLocalizedTableMatrixTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        "WinGetLocalizedTableMatrixTests",
        Guid.NewGuid().ToString("N")
    );

    public WinGetLocalizedTableMatrixTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
    }

    public void Dispose()
    {
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private sealed record Locale(
        string Tag,
        string Name,
        string Id,
        string Version,
        string Available,
        string Match,
        string Source
    );

    private static readonly Locale[] Locales =
    [
        new("en-US", "Name", "Id", "Version", "Available", "Match", "Source"),
        new("resw", "SearchName", "SearchId", "SearchVersion", "AvailableHeader", "SearchMatch", "SearchSource"),
        new("de-DE", "Name", "ID", "Version", "Verfügbar", "Übereinstimmung", "Quelle"),
        new("es-ES", "Nombre", "Id", "Versión", "Disponible", "Coincidencia", "Origen"),
        new("fr-FR", "Nom", "ID", "Version", "Disponible", "Correspondance", "Source"),
        new("it-IT", "Nome", "Id", "Versione", "Disponibile", "Corrispondenza", "Origine"),
        new("ja-JP", "名前", "ID", "バージョン", "利用可能", "一致", "ソース"),
        new("ko-KR", "이름", "장치 ID", "버전", "사용 가능", "일치", "원본"),
        new("pt-BR", "Nome", "ID", "Versão", "Disponível", "Correspondência", "Origem"),
        new("ru-RU", "Имя", "ИД", "Версия", "Доступно", "Совпадение", "Источник"),
        new("zh-CN", "名称", "ID", "版本", "可用", "匹配", "源"),
        new("zh-TW", "名稱", "識別碼", "版本", "可用", "相符", "來源"),
    ];

    private static int Width(string text)
    {
        int width = 0;
        foreach (char c in text)
        {
            width +=
                c is >= 'ᄀ' and <= 'ᅟ'
                or >= '⺀' and <= '〾'
                or >= 'ぁ' and <= '㏿'
                or >= '㐀' and <= '䶿'
                or >= '一' and <= '鿿'
                or >= '가' and <= '힣'
                or >= '豈' and <= '﫿'
                or >= '！' and <= '｠'
                    ? 2
                    : 1;
        }

        return width;
    }

    private static string[] Render(IReadOnlyList<string[]> rows)
    {
        int columns = rows[0].Length;
        int[] widths = new int[columns];
        foreach (string[] row in rows)
        {
            for (int i = 0; i < columns; i++)
            {
                widths[i] = Math.Max(widths[i], Width(row[i]));
            }
        }

        List<string> lines = [];
        foreach (string[] row in rows)
        {
            StringBuilder line = new();
            for (int i = 0; i < columns; i++)
            {
                if (i == columns - 1)
                {
                    line.Append(row[i]);
                    break;
                }

                bool restEmpty = true;
                for (int j = i + 1; j < columns; j++)
                {
                    restEmpty &= row[j].Length == 0;
                }

                if (row[i].Length == 0 && restEmpty)
                {
                    line.Append(' ', widths[i] - Width(row[i]) + 1);
                    continue;
                }

                line.Append(row[i]).Append(' ', widths[i] - Width(row[i]) + 1);
            }

            lines.Add(line.ToString().TrimEnd());
            if (lines.Count == 1)
            {
                lines.Add(new string('-', widths.Sum() + columns - 1));
            }
        }

        return [.. lines];
    }

    public static TheoryData<string, bool, bool, bool> ListShapes()
    {
        TheoryData<string, bool, bool, bool> data = [];
        foreach (Locale locale in Locales)
        {
            foreach (bool available in new[] { false, true })
            {
                foreach (bool source in new[] { false, true })
                {
                    foreach (bool singleRow in new[] { false, true })
                    {
                        data.Add(locale.Tag, available, source, singleRow);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ListShapes))]
    public void InstalledPackagesParseInEveryLocaleAndColumnShape(
        string tag,
        bool withAvailable,
        bool withSource,
        bool singleRow
    )
    {
        Locale locale = Locales.First(l => l.Tag == tag);
        var manager = new WinGet();

        List<string> header = [locale.Name, locale.Id, locale.Version];
        if (withAvailable)
            header.Add(locale.Available);
        if (withSource)
            header.Add(locale.Source);

        string[] Row(string name, string id, string version, string available, string source)
        {
            List<string> cells = [name, id, version];
            if (withAvailable)
                cells.Add(available);
            if (withSource)
                cells.Add(source);
            return [.. cells];
        }

        List<string[]> rows = [[.. header]];

        // A spaced identifier whose second word lands on Korean's "장치 ID" continuation.
        rows.Add(Row("Dell SupportAssist", "Dell App", "3.14.1", "3.15.0", "winget"));
        if (!singleRow)
        {
            rows.Add(Row("Visual Studio Code", "Microsoft.VisualStudioCode", "1.136.1", "", "winget"));
            rows.Add(Row("Vim", "Vim", "9.1", "", ""));
        }

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Render(rows)
        );

        Assert.Equal(singleRow ? 1 : 3, packages.Count);
        PackageAssert.Matches(packages[0], "Dell SupportAssist", "Dell App", "3.14.1");
        if (withSource)
        {
            Assert.Equal("winget", packages[0].Source.Name);
        }

        if (!singleRow)
        {
            PackageAssert.Matches(
                packages[1],
                "Visual Studio Code",
                "Microsoft.VisualStudioCode",
                "1.136.1"
            );
            PackageAssert.Matches(packages[2], "Vim", "Vim", "9.1");
            Assert.Same(manager.LocalPcSource, packages[2].Source);
        }
    }

    [Theory]
    [MemberData(nameof(ListShapes))]
    public void FoundPackagesParseInEveryLocaleAndColumnShape(
        string tag,
        bool withMatch,
        bool withSource,
        bool singleRow
    )
    {
        Locale locale = Locales.First(l => l.Tag == tag);
        var manager = new WinGet();

        List<string> header = [locale.Name, locale.Id, locale.Version];
        if (withMatch)
            header.Add(locale.Match);
        if (withSource)
            header.Add(locale.Source);

        string[] Row(string name, string id, string version, string match, string source)
        {
            List<string> cells = [name, id, version];
            if (withMatch)
                cells.Add(match);
            if (withSource)
                cells.Add(source);
            return [.. cells];
        }

        List<string[]> rows = [[.. header]];
        rows.Add(Row("Dell SupportAssist", "Dell App", "3.14.1", "Moniker: dell", "winget"));
        if (!singleRow)
        {
            rows.Add(Row("VLC", "XPDM1ZW6815MQM", "Unknown", "", "msstore"));
        }

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseFoundPackages(
            manager,
            Render(rows)
        );

        Assert.Equal(singleRow ? 1 : 2, packages.Count);
        PackageAssert.Matches(packages[0], "Dell SupportAssist", "Dell App", "3.14.1");
        if (withSource)
        {
            Assert.Equal("winget", packages[0].Source.Name);
        }

        if (!singleRow)
        {
            PackageAssert.Matches(packages[1], "VLC", "XPDM1ZW6815MQM", "Unknown");
            if (withSource)
            {
                Assert.Equal("msstore", packages[1].Source.Name);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ListShapes))]
    public void AvailableUpdatesParseInEveryLocaleAndColumnShape(
        string tag,
        bool _,
        bool withSource,
        bool singleRow
    )
    {
        Locale locale = Locales.First(l => l.Tag == tag);
        var manager = new WinGet();

        List<string> header = [locale.Name, locale.Id, locale.Version, locale.Available];
        if (withSource)
            header.Add(locale.Source);

        string[] Row(string name, string id, string version, string available, string source)
        {
            List<string> cells = [name, id, version, available];
            if (withSource)
                cells.Add(source);
            return [.. cells];
        }

        List<string[]> rows = [[.. header]];
        rows.Add(Row("Dell SupportAssist", "Dell App", "3.14.1", "3.15.0", "winget"));
        if (!singleRow)
        {
            rows.Add(Row("Git", "Git.Git", "2.51.0", "2.52.0", "winget"));
        }

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseAvailableUpdates(
            manager,
            Render(rows)
        );

        Assert.Equal(singleRow ? 1 : 2, packages.Count);
        PackageAssert.Matches(packages[0], "Dell SupportAssist", "Dell App", "3.14.1", "3.15.0");
        if (!singleRow)
        {
            PackageAssert.Matches(packages[1], "Git", "Git.Git", "2.51.0", "2.52.0");
        }
    }
}
#endif
