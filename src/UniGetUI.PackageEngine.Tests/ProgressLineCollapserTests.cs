using UniGetUI.PackageOperations;
using LineType = UniGetUI.PackageOperations.AbstractOperation.LineType;

namespace UniGetUI.PackageEngine.Tests;

public class ProgressLineCollapserTests
{
    // Applies the collapser to a stream of (text, type) lines exactly like the live log view does,
    // returning the lines that would actually be rendered.
    private static List<string> Render(IEnumerable<(string Text, LineType Type)> stream)
    {
        var collapser = new ProgressLineCollapser();
        var rendered = new List<string>();
        foreach (var (text, type) in stream)
        {
            if (collapser.Next(type) is ProgressLineCollapser.Fold.ReplaceLast && rendered.Count > 0)
                rendered[^1] = text;
            else
                rendered.Add(text);
        }
        return rendered;
    }

    [Fact]
    public void SpinnerFramesCollapseToASingleLine()
    {
        var spinner = new[] { "|", "/", "-", "\\", "|", "/", "-", "\\" }
            .Select(f => ($"Installing {f}", LineType.ProgressIndicator));

        var rendered = Render(spinner);

        Assert.Single(rendered);
        Assert.Equal("Installing \\", rendered[0]);   // last frame wins
    }

    [Fact]
    public void DownloadProgressCollapsesAndSettlesIntoFinalLine()
    {
        var stream = new List<(string, LineType)>
        {
            ("Fetching download url...", LineType.Information),
            ("[=         ] 10 MB/100 MB", LineType.ProgressIndicator),
            ("[=====     ] 50 MB/100 MB", LineType.ProgressIndicator),
            ("[==========] 100 MB/100 MB", LineType.ProgressIndicator),
            ("The file was saved to C:\\out.exe", LineType.Information),
        };

        var rendered = Render(stream);

        Assert.Equal(
        [
            "Fetching download url...",
            "The file was saved to C:\\out.exe",   // progress bar settled into this line
        ], rendered);
    }

    [Fact]
    public void NonProgressLinesAlwaysAppend()
    {
        var stream = new[]
        {
            ("line 1", LineType.Information),
            ("line 2", LineType.VerboseDetails),
            ("line 3", LineType.Error),
        };

        var rendered = Render(stream);

        Assert.Equal(["line 1", "line 2", "line 3"], rendered);
    }

    [Fact]
    public void ProgressBetweenNormalLinesDoesNotEatThem()
    {
        var stream = new[]
        {
            ("before", LineType.Information),
            ("spin 1", LineType.ProgressIndicator),
            ("spin 2", LineType.ProgressIndicator),
            ("after", LineType.Information),
            ("end", LineType.Information),
        };

        var rendered = Render(stream);

        // "before" kept, two spins collapse and settle into "after", then "end" appends.
        Assert.Equal(["before", "after", "end"], rendered);
    }

    [Fact]
    public void FirstLineNeverReplaces()
    {
        Assert.Equal(ProgressLineCollapser.Fold.Append, new ProgressLineCollapser().Next(LineType.ProgressIndicator));
    }
}
