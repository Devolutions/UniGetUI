using System.Globalization;

namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal sealed class WinGetTableLayout
{
    public const int NameColumn = 0;
    public const int IdColumn = 1;
    public const int VersionColumn = 2;
    public const int AvailableColumn = 3;

    private readonly int[] _columnStarts;
    private readonly int _tableWidth;

    private WinGetTableLayout(int[] columnStarts, int tableWidth)
    {
        _columnStarts = columnStarts;
        _tableWidth = tableWidth;
    }

    public int ColumnCount => _columnStarts.Length;

    public int LastColumn => _columnStarts.Length - 1;

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

        return columnStarts.Count >= 3
            ? new WinGetTableLayout([.. columnStarts], separatorLine.TrimEnd().Length)
            : null;
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

    public bool StartsSeparateCell(string line, int column)
    {
        if (column <= 0 || column >= _columnStarts.Length)
        {
            return false;
        }

        return CharIndexOfColumn(line, _columnStarts[column])
            > CharIndexOfColumn(line, _columnStarts[column - 1]);
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
        int width = 0;
        int index = 0;

        while (index < line.Length)
        {
            width += GetDisplayWidth(FirstCodePoint(line, index));
            index += TextElementLength(line, index);
        }

        return width;
    }

    private static int CharIndexOfColumn(string line, int displayColumn)
    {
        int index = 0;
        int width = 0;

        while (index < line.Length && width < displayColumn)
        {
            width += GetDisplayWidth(FirstCodePoint(line, index));
            index += TextElementLength(line, index);
        }

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

    private static bool IsFullWidth(int codePoint) =>
        codePoint
            is >= 0x1100 and <= 0x115F
                or >= 0x2E80 and <= 0x303E
                or >= 0x3041 and <= 0x33FF
                or >= 0x3400 and <= 0x4DBF
                or >= 0x4E00 and <= 0x9FFF
                or >= 0xA000 and <= 0xA4CF
                or >= 0xA960 and <= 0xA97F
                or >= 0xAC00 and <= 0xD7A3
                or >= 0xF900 and <= 0xFAFF
                or >= 0xFE10 and <= 0xFE19
                or >= 0xFE30 and <= 0xFE6F
                or >= 0xFF00 and <= 0xFF60
                or >= 0xFFE0 and <= 0xFFE6
                or >= 0x17000 and <= 0x18CFF
                or >= 0x1B000 and <= 0x1B2FF
                or >= 0x20000 and <= 0x2FFFD
                or >= 0x30000 and <= 0x3FFFD;
}
