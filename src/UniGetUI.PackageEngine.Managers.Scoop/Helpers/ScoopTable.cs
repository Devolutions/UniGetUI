using System.Text.RegularExpressions;

namespace UniGetUI.PackageEngine.Managers.ScoopManager
{
    internal static partial class ScoopTable
    {
        [GeneratedRegex(@"\x1b\[[0-9;]*m")]
        private static partial Regex AnsiSequence();

        public static string StripAnsiSequences(string line) =>
            line.Contains('\x1b') ? AnsiSequence().Replace(line, "") : line;

        public static IReadOnlyList<int>? ReadColumnStarts(string line)
        {
            if (!line.Contains("---"))
            {
                return null;
            }

            List<int> starts = [];
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] is not '-')
                {
                    continue;
                }

                starts.Add(i);
                while (i < line.Length && line[i] is '-')
                {
                    i++;
                }
            }

            return starts;
        }

        public static string ReadColumn(string line, IReadOnlyList<int> starts, int index)
        {
            int start = starts[index];
            if (start >= line.Length)
            {
                return "";
            }

            int end =
                index + 1 < starts.Count ? Math.Min(starts[index + 1], line.Length) : line.Length;
            return line[start..end].Trim();
        }
    }
}
