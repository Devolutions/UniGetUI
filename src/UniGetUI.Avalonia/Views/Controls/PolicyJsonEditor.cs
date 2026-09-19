using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using AvaloniaEdit;

namespace UniGetUI.Avalonia.Views.Controls;

public sealed class PolicyJsonEditor : TextEditor
{
    protected override Type StyleKeyOverride => typeof(TextEditor);

    public PolicyJsonEditor()
    {
        ShowLineNumbers = true;
        WordWrap = false;
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
        FontSize = 12;
        Padding = new Thickness(8);
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    public bool TryNavigateToJsonPointer(string? pointer)
    {
        if (!TryFindJsonPointerSelection(
                Text,
                pointer,
                out int offset,
                out int length))
            return false;

        Select(offset, length);
        CaretOffset = offset;
        ScrollToLine(Document.GetLineByOffset(offset).LineNumber);
        return true;
    }

    internal static bool TryFindJsonPointerSelection(
        string json,
        string? pointer,
        out int characterOffset,
        out int characterLength)
    {
        characterOffset = 0;
        characterLength = 0;
        string[] segments = (pointer ?? "").Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal))
            .ToArray();
        if (segments.Length == 0)
            return false;

        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        long byteOffset;
        int byteLength;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read()
                || !TryFindProperty(
                    ref reader,
                    utf8,
                    segments,
                    0,
                    out byteOffset,
                    out byteLength))
            {
                return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        characterOffset = Encoding.UTF8.GetCharCount(
            utf8.AsSpan(0, checked((int)byteOffset)));
        characterLength = Encoding.UTF8.GetCharCount(
            utf8.AsSpan(checked((int)byteOffset), byteLength));
        return true;
    }

    internal static string LastPropertySegment(string? pointer)
    {
        string[] segments = (pointer ?? "").Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = segments.Length - 1; index >= 0; index--)
        {
            if (!int.TryParse(segments[index], out _))
            {
                return segments[index]
                    .Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal);
            }
        }

        return "";
    }

    private static bool TryFindProperty(
        ref Utf8JsonReader reader,
        ReadOnlySpan<byte> json,
        IReadOnlyList<string> segments,
        int depth,
        out long byteOffset,
        out int byteLength)
    {
        byteOffset = 0;
        byteLength = 0;
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return false;

                string property = reader.GetString() ?? "";
                long propertyOffset = reader.TokenStartIndex;
                int propertyLength = GetPropertyTokenLength(
                    json,
                    checked((int)propertyOffset));
                if (!reader.Read())
                    return false;

                if (depth < segments.Count
                    && property.Equals(segments[depth], StringComparison.Ordinal))
                {
                    if (depth == segments.Count - 1)
                    {
                        byteOffset = propertyOffset;
                        byteLength = propertyLength;
                        return true;
                    }

                    if (TryFindProperty(
                            ref reader,
                            json,
                            segments,
                            depth + 1,
                            out byteOffset,
                            out byteLength))
                    {
                        return true;
                    }
                }
                else
                {
                    reader.Skip();
                }
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray
                 && depth < segments.Count
                 && int.TryParse(segments[depth], out int targetIndex))
        {
            int index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (index == targetIndex)
                {
                    if (depth == segments.Count - 1)
                    {
                        byteOffset = reader.TokenStartIndex;
                        byteLength = Math.Max(
                            1,
                            checked((int)(
                                reader.BytesConsumed
                                - reader.TokenStartIndex)));
                        return true;
                    }

                    return TryFindProperty(
                        ref reader,
                        json,
                        segments,
                        depth + 1,
                        out byteOffset,
                        out byteLength);
                }

                reader.Skip();
                index++;
            }
        }

        return false;
    }

    private static int GetPropertyTokenLength(
        ReadOnlySpan<byte> json,
        int propertyStart)
    {
        bool escaped = false;
        for (int index = propertyStart + 1; index < json.Length; index++)
        {
            if (!escaped && json[index] == (byte)'"')
            {
                return index - propertyStart + 1;
            }

            escaped = !escaped && json[index] == (byte)'\\';
        }

        return 1;
    }
}
