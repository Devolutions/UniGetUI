namespace UniGetUI.PackageOperations;

public static class LiveOutputOrder
{
    public static List<(string Text, AbstractOperation.LineType Type)> Merge(
        int firstLine,
        IReadOnlyList<(string, AbstractOperation.LineType)> storedTail,
        IReadOnlyList<(int Position, string Text)> progress
    )
    {
        var result = new List<(string, AbstractOperation.LineType)>(storedTail.Count + progress.Count);
        int next = 0;
        foreach (var (position, text) in progress)
        {
            if (position < firstLine + next)
                continue;
            for (; firstLine + next < position && next < storedTail.Count; next++)
                result.Add(storedTail[next]);
            result.Add((text, AbstractOperation.LineType.ProgressIndicator));
        }
        for (; next < storedTail.Count; next++)
            result.Add(storedTail[next]);
        return result;
    }
}
