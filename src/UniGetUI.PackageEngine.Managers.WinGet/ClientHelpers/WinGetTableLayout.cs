using System.Globalization;
using System.Text;

namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal enum HeaderKind
{
    Name,
    Id,
    Version,
    Available,
    Match,
    Source,
}

internal sealed record WinGetTable(WinGetTableLayout Layout, IReadOnlyList<string> Rows);

internal sealed class WinGetTableLayout
{
    public const int NameColumn = 0;
    public const int IdColumn = 1;
    public const int VersionColumn = 2;
    public const int AvailableColumn = 3;

    private const int MinimumColumns = 3;

    private static readonly Dictionary<string, HeaderKind> HeaderNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        { "Name", HeaderKind.Name },
        { "Nom", HeaderKind.Name },
        { "Nombre", HeaderKind.Name },
        { "Nome", HeaderKind.Name },
        { "SearchName", HeaderKind.Name },
        { "Имя", HeaderKind.Name },
        { "名前", HeaderKind.Name },
        { "名称", HeaderKind.Name },
        { "名稱", HeaderKind.Name },
        { "이름", HeaderKind.Name },

        { "ID", HeaderKind.Id },
        { "SearchId", HeaderKind.Id },
        { "ИД", HeaderKind.Id },
        { "識別碼", HeaderKind.Id },
        { "장치 ID", HeaderKind.Id },

        { "SearchVersion", HeaderKind.Version },
        { "Version", HeaderKind.Version },
        { "Versione", HeaderKind.Version },
        { "Versión", HeaderKind.Version },
        { "Versão", HeaderKind.Version },
        { "Версия", HeaderKind.Version },
        { "バージョン", HeaderKind.Version },
        { "版本", HeaderKind.Version },
        { "버전", HeaderKind.Version },

        { "Available", HeaderKind.Available },
        { "AvailableHeader", HeaderKind.Available },
        { "Disponibile", HeaderKind.Available },
        { "Disponible", HeaderKind.Available },
        { "Disponível", HeaderKind.Available },
        { "Verfügbar", HeaderKind.Available },
        { "Доступно", HeaderKind.Available },
        { "利用可能", HeaderKind.Available },
        { "可用", HeaderKind.Available },
        { "사용 가능", HeaderKind.Available },

        { "Coincidencia", HeaderKind.Match },
        { "Correspondance", HeaderKind.Match },
        { "Correspondência", HeaderKind.Match },
        { "Corrispondenza", HeaderKind.Match },
        { "Match", HeaderKind.Match },
        { "SearchMatch", HeaderKind.Match },
        { "Übereinstimmung", HeaderKind.Match },
        { "Совпадение", HeaderKind.Match },
        { "一致", HeaderKind.Match },
        { "匹配", HeaderKind.Match },
        { "相符", HeaderKind.Match },
        { "일치", HeaderKind.Match },

        { "Origem", HeaderKind.Source },
        { "Origen", HeaderKind.Source },
        { "Origine", HeaderKind.Source },
        { "Quelle", HeaderKind.Source },
        { "SearchSource", HeaderKind.Source },
        { "Source", HeaderKind.Source },
        { "Источник", HeaderKind.Source },
        { "ソース", HeaderKind.Source },
        { "來源", HeaderKind.Source },
        { "源", HeaderKind.Source },
        { "원본", HeaderKind.Source },
    };

    private readonly string _headerLine;
    private readonly int[] _columnStarts;
    private readonly int _tableWidth;

    private WinGetTableLayout(string headerLine, int[] columnStarts, int tableWidth)
    {
        _headerLine = headerLine;
        _columnStarts = columnStarts;
        _tableWidth = tableWidth;
    }

    public int ColumnCount => _columnStarts.Length;

    public int LastColumn => _columnStarts.Length - 1;

    public bool HasSourceColumn =>
        ColumnCount >= 5 || (ColumnCount == 4 && !LastHeaderIsAvailableOrMatch());

    public static bool IsSeparatorLine(string line)
    {
        int dashes = 0;
        foreach (char character in line)
        {
            if (character == '-')
            {
                dashes++;
            }
            else if (character != ' ')
            {
                return false;
            }
        }

        return dashes >= 3;
    }

    public static IEnumerable<WinGetTable> ReadTables(IEnumerable<string> lines)
    {
        string previousLine = "";
        WinGetTableLayout? layout = null;
        List<string> rows = [];

        foreach (string line in lines)
        {
            if (IsSeparatorLine(line))
            {
                if (layout is not null)
                {
                    yield return BuildTable(layout, rows);
                }

                layout = Parse(previousLine, line);
                rows = [];
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                if (layout is not null)
                {
                    yield return BuildTable(layout, rows);
                }

                layout = null;
                rows = [];
            }
            else if (
                layout is not null
                && layout.IsRowReaching(line, VersionColumn)
                && !layout.StraddlesColumn(line, IdColumn)
            )
            {
                rows.Add(line);
            }

            previousLine = line;
        }

        if (layout is not null)
        {
            yield return BuildTable(layout, rows);
        }
    }

    private static WinGetTable BuildTable(WinGetTableLayout layout, List<string> rows) =>
        new(layout.MergeContinuationColumns(rows), rows);

    public static WinGetTableLayout? Parse(string headerLine, string separatorLine)
    {
        List<int> columnStarts = [];
        bool previousWasSpace = true;
        int displayColumn = 0;
        int index = 0;

        while (index < headerLine.Length)
        {
            int codePoint = FirstCodePoint(headerLine, index);
            bool isSpace = codePoint == ' ';

            if (!isSpace && previousWasSpace)
            {
                columnStarts.Add(displayColumn);
            }

            previousWasSpace = isSpace;
            displayColumn += GetDisplayWidth(codePoint);
            index += TextElementLength(headerLine, index);
        }

        return columnStarts.Count >= MinimumColumns
            ? new WinGetTableLayout(headerLine, [.. columnStarts], separatorLine.TrimEnd().Length)
            : null;
    }

    public WinGetTableLayout MergeContinuationColumns(IReadOnlyList<string> rows)
    {
        if (_columnStarts.Length <= MinimumColumns)
        {
            return this;
        }

        List<int> kept = [_columnStarts[0]];
        int remaining = _columnStarts.Length;

        for (int column = 1; column < _columnStarts.Length; column++)
        {
            if (remaining - 1 >= MinimumColumns && CompletesAHeaderName(kept[^1], column))
            {
                remaining--;
                continue;
            }

            int straddled = 0;
            int startsACell = 0;

            foreach (string row in rows)
            {
                if (Straddles(row, _columnStarts[column]))
                {
                    straddled++;
                }
                else if (StartsACell(row, _columnStarts[column]))
                {
                    startsACell++;
                }
            }

            if (
                rows.Count > 0
                && (startsACell == 0 || straddled > startsACell)
                && remaining - 1 >= MinimumColumns
            )
            {
                remaining--;
            }
            else
            {
                kept.Add(_columnStarts[column]);
            }
        }

        return kept.Count == _columnStarts.Length
            ? this
            : new WinGetTableLayout(_headerLine, [.. kept], _tableWidth);
    }

    private bool CompletesAHeaderName(int previousStart, int column)
    {
        int endColumn =
            column + 1 < _columnStarts.Length ? _columnStarts[column + 1] : int.MaxValue;

        return HeaderNames.ContainsKey(HeaderTextBetween(previousStart, endColumn));
    }

    private bool LastHeaderIsAvailableOrMatch()
    {
        string text = GetCell(_headerLine, LastColumn);
        return HeaderNames.TryGetValue(text, out HeaderKind kind)
            && kind is HeaderKind.Available or HeaderKind.Match;
    }

    private string HeaderTextBetween(int startDisplayColumn, int endDisplayColumn)
    {
        int start = CharIndexOfColumn(_headerLine, startDisplayColumn);
        if (start >= _headerLine.Length)
        {
            return "";
        }

        int end =
            endDisplayColumn == int.MaxValue
                ? _headerLine.Length
                : CharIndexOfColumn(_headerLine, endDisplayColumn);

        if (end > _headerLine.Length)
        {
            end = _headerLine.Length;
        }

        return end <= start ? "" : _headerLine[start..end].Trim();
    }

    public bool IsRowReaching(string line, int column)
    {
        if (column < 0 || column >= _columnStarts.Length)
        {
            return false;
        }

        if (DisplayWidth(line) > _tableWidth)
        {
            return false;
        }

        return CharIndexOfColumn(line, _columnStarts[column]) < line.Length;
    }

    public bool StraddlesColumn(string line, int column) =>
        column > 0
        && column < _columnStarts.Length
        && Straddles(line, _columnStarts[column]);

    private static bool Straddles(string line, int displayColumn)
    {
        int index = RawCharIndexOfColumn(line, displayColumn);
        return index > 0 && index < line.Length && line[index] != ' ' && line[index - 1] != ' ';
    }

    private static bool StartsACell(string line, int displayColumn)
    {
        int index = RawCharIndexOfColumn(line, displayColumn);
        return index < line.Length && line[index] != ' ' && (index == 0 || line[index - 1] == ' ');
    }

    public string GetCell(string line, int column) => GetCell(line, column, column + 1);

    public string GetCell(string line, int firstColumn, int columnAfterLast)
    {
        if (firstColumn < 0 || firstColumn >= _columnStarts.Length)
        {
            return "";
        }

        int start = CharIndexOfColumn(line, _columnStarts[firstColumn]);
        if (start >= line.Length)
        {
            return "";
        }

        int end =
            columnAfterLast < _columnStarts.Length
                ? CharIndexOfColumn(line, _columnStarts[columnAfterLast])
                : line.Length;

        if (end > line.Length)
        {
            end = line.Length;
        }

        return end <= start ? "" : line[start..end].Trim();
    }

    private static int DisplayWidth(string line)
    {
        if (Ascii.IsValid(line))
        {
            return line.Length;
        }

        int width = 0;
        int index = 0;

        while (index < line.Length)
        {
            width += GetDisplayWidth(FirstCodePoint(line, index));
            index += TextElementLength(line, index);
        }

        return width;
    }

    private static int RawCharIndexOfColumn(string line, int displayColumn)
    {
        if (Ascii.IsValid(line))
        {
            return Math.Min(displayColumn, line.Length);
        }

        int index = 0;
        int width = 0;

        while (index < line.Length && width < displayColumn)
        {
            width += GetDisplayWidth(FirstCodePoint(line, index));
            index += TextElementLength(line, index);
        }

        return index;
    }

    private static int CharIndexOfColumn(string line, int displayColumn)
    {
        int index = RawCharIndexOfColumn(line, displayColumn);

        while (index > 0 && index < line.Length && line[index] != ' ' && line[index - 1] != ' ')
        {
            index--;
        }

        return index;
    }

    private static int TextElementLength(string text, int index) =>
        Math.Max(1, StringInfo.GetNextTextElementLength(text.AsSpan(index)));

    private static int FirstCodePoint(string text, int index) =>
        char.IsHighSurrogate(text[index])
        && index + 1 < text.Length
        && char.IsLowSurrogate(text[index + 1])
            ? char.ConvertToUtf32(text[index], text[index + 1])
            : text[index];

    private static int GetDisplayWidth(int codePoint) => IsFullWidth(codePoint) ? 2 : 1;

    private static bool IsFullWidth(int codePoint)
    {
        int index = Array.BinarySearch(WideRangeStarts, codePoint);
        if (index >= 0)
        {
            return true;
        }

        index = ~index - 1;
        return index >= 0 && codePoint <= WideRangeEnds[index];
    }

    private static readonly int[] WideRangeStarts =
    [
        0x01100, 0x0231A, 0x02329, 0x023E9, 0x023F0, 0x023F3, 0x025FD, 0x02614,
        0x02630, 0x02648, 0x0267F, 0x0268A, 0x02693, 0x026A1, 0x026AA, 0x026BD,
        0x026C4, 0x026CE, 0x026D4, 0x026EA, 0x026F2, 0x026F5, 0x026FA, 0x026FD,
        0x02705, 0x0270A, 0x02728, 0x0274C, 0x0274E, 0x02753, 0x02757, 0x02795,
        0x027B0, 0x027BF, 0x02B1B, 0x02B50, 0x02B55, 0x02E80, 0x02E9B, 0x02F00,
        0x02FF0, 0x03041, 0x03099, 0x03105, 0x03131, 0x03190, 0x031EF, 0x03220,
        0x03250, 0x0A490, 0x0A960, 0x0AC00, 0x0F900, 0x0FE10, 0x0FE30, 0x0FE54,
        0x0FE68, 0x0FF01, 0x0FFE0, 0x16FE0, 0x16FF0, 0x17000, 0x18800, 0x18CFF,
        0x1AFF0, 0x1AFF5, 0x1AFFD, 0x1B000, 0x1B132, 0x1B150, 0x1B155, 0x1B164,
        0x1B170, 0x1D300, 0x1D360, 0x1F004, 0x1F0CF, 0x1F18E, 0x1F191, 0x1F200,
        0x1F210, 0x1F240, 0x1F250, 0x1F260, 0x1F300, 0x1F32D, 0x1F337, 0x1F37E,
        0x1F3A0, 0x1F3CF, 0x1F3E0, 0x1F3F4, 0x1F3F8, 0x1F440, 0x1F442, 0x1F4FF,
        0x1F54B, 0x1F550, 0x1F57A, 0x1F595, 0x1F5A4, 0x1F5FB, 0x1F680, 0x1F6CC,
        0x1F6D0, 0x1F6D5, 0x1F6DC, 0x1F6EB, 0x1F6F4, 0x1F7E0, 0x1F7F0, 0x1F90C,
        0x1F93C, 0x1F947, 0x1FA70, 0x1FA80, 0x1FA8F, 0x1FACE, 0x1FADF, 0x1FAF0,
        0x20000, 0x30000,
    ];

    private static readonly int[] WideRangeEnds =
    [
        0x0115F, 0x0231B, 0x0232A, 0x023EC, 0x023F0, 0x023F3, 0x025FE, 0x02615,
        0x02637, 0x02653, 0x0267F, 0x0268F, 0x02693, 0x026A1, 0x026AB, 0x026BE,
        0x026C5, 0x026CE, 0x026D4, 0x026EA, 0x026F3, 0x026F5, 0x026FA, 0x026FD,
        0x02705, 0x0270B, 0x02728, 0x0274C, 0x0274E, 0x02755, 0x02757, 0x02797,
        0x027B0, 0x027BF, 0x02B1C, 0x02B50, 0x02B55, 0x02E99, 0x02EF3, 0x02FD5,
        0x0303E, 0x03096, 0x030FF, 0x0312F, 0x0318E, 0x031E5, 0x0321E, 0x03247,
        0x0A48C, 0x0A4C6, 0x0A97C, 0x0D7A3, 0x0FAFF, 0x0FE19, 0x0FE52, 0x0FE66,
        0x0FE6B, 0x0FF60, 0x0FFE6, 0x16FE4, 0x16FF1, 0x187F7, 0x18CD5, 0x18D08,
        0x1AFF3, 0x1AFFB, 0x1AFFE, 0x1B122, 0x1B132, 0x1B152, 0x1B155, 0x1B167,
        0x1B2FB, 0x1D356, 0x1D376, 0x1F004, 0x1F0CF, 0x1F18E, 0x1F19A, 0x1F202,
        0x1F23B, 0x1F248, 0x1F251, 0x1F265, 0x1F320, 0x1F335, 0x1F37C, 0x1F393,
        0x1F3CA, 0x1F3D3, 0x1F3F0, 0x1F3F4, 0x1F43E, 0x1F440, 0x1F4FC, 0x1F53D,
        0x1F54E, 0x1F567, 0x1F57A, 0x1F596, 0x1F5A4, 0x1F64F, 0x1F6C5, 0x1F6CC,
        0x1F6D2, 0x1F6D7, 0x1F6DF, 0x1F6EC, 0x1F6FC, 0x1F7EB, 0x1F7F0, 0x1F93A,
        0x1F945, 0x1F9FF, 0x1FA7C, 0x1FA89, 0x1FAC6, 0x1FADC, 0x1FAE9, 0x1FAF8,
        0x2FFFD, 0x3FFFD,
    ];
}
