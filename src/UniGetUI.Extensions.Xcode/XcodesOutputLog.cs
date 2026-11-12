using System.Text;

namespace UniGetUI.Extensions.Xcode;

/// <summary>Retains recent complete lines without keeping unbounded process output.</summary>
internal sealed class XcodesOutputLog
{
    internal const int MaximumLineLength = 16 * 1024;
    internal const int MaximumLines = 300;
    internal const int MaximumCharacters = 512 * 1024;
    internal const string EarlierOutputOmitted = "[Earlier output was omitted.]";
    internal const string LongLineOmitted = "[A long output line was omitted.]";

    private readonly List<string> _lines = [];
    private int _characters;
    private bool _omitted;

    internal static string LimitLine(string text)
        => text.Length <= MaximumLineLength ? text : LongLineOmitted;

    public void Add(XcodesOutputLine line)
    {
        string text = LimitLine(line.Text);
        if (line.ReplacesPrevious && _lines.Count > 0)
        {
            _characters -= _lines[^1].Length;
            _lines[^1] = text;
        }
        else
        {
            _lines.Add(text);
        }
        _characters += text.Length;

        while (_lines.Count > MaximumLines || _characters > MaximumCharacters)
        {
            _characters -= _lines[0].Length;
            _lines.RemoveAt(0);
            _omitted = true;
        }
    }

    public override string ToString()
    {
        string text = string.Join('\n', _lines);
        return _omitted ? EarlierOutputOmitted + "\n" + text : text;
    }

    /// <summary>
    /// Keeps raw stderr until all answers are known, so redaction can also remove answers echoed there.
    /// An oversized line is discarded in full rather than exposing part of a secret.
    /// </summary>
    internal static async Task<string> ReadAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var retained = new XcodesOutputLog();
        var line = new StringBuilder();
        bool overflow = false;
        bool endedWithNewline = false;

        void Emit()
        {
            retained.Add(new XcodesOutputLine(overflow ? LongLineOmitted : line.ToString()));
            line.Clear();
            overflow = false;
        }

        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                char character = buffer[i];
                endedWithNewline = character == '\n';
                if (endedWithNewline)
                    Emit();
                else if (!overflow)
                {
                    if (line.Length == MaximumLineLength)
                    {
                        line.Clear();
                        overflow = true;
                    }
                    else
                    {
                        line.Append(character);
                    }
                }
            }
        }
        if (line.Length > 0 || overflow)
            Emit();
        else if (endedWithNewline)
            retained.Add(new XcodesOutputLine(""));
        return retained.ToString();
    }
}
