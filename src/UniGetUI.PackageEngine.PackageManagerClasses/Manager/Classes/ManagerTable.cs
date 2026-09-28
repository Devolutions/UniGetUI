namespace UniGetUI.PackageEngine.ManagerClasses.Classes
{
    public static class ManagerTable
    {
        public const string UntruncatedTableTail = " -AutoSize | Out-String -Width 4096";

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
            if (index >= starts.Count)
            {
                return "";
            }

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
