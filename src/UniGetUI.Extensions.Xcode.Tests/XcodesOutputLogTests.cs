using System.Text;

namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodesOutputLogTests
{
    [Fact]
    public void RetainsRecentLinesWithinBothLimits()
    {
        var log = new XcodesOutputLog();
        for (int i = 0; i < 80; i++)
            log.Add(new XcodesOutputLine($"{i:000}:" + new string('x', 8192)));

        string text = log.ToString();

        Assert.StartsWith(XcodesOutputLog.EarlierOutputOmitted + "\n", text);
        Assert.DoesNotContain("000:", text);
        Assert.Contains("079:", text);
        Assert.True(text.Length <= XcodesOutputLog.MaximumCharacters + XcodesOutputLog.MaximumLines + 100);
    }

    [Fact]
    public void ReplacingProgressDoesNotDiscardUnrelatedLines()
    {
        var log = new XcodesOutputLog();
        log.Add(new XcodesOutputLine("important diagnostic"));
        log.Add(new XcodesOutputLine(new string('x', 8000)));
        log.Add(new XcodesOutputLine("progress 2", ReplacesPrevious: true));

        Assert.Equal("important diagnostic\nprogress 2", log.ToString());
    }

    [Fact]
    public async Task RawErrorReaderOmitsAWholeLongLineAndRetainsLaterAnswersForRedaction()
    {
        string input = "secret-fragment" + new string('x', XcodesOutputLog.MaximumLineLength)
            + "\nError: private-password\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var reader = new StreamReader(stream);

        string raw = await XcodesOutputLog.ReadAsync(reader, CancellationToken.None);
        var redactor = new XcodesOutputRedactor(null);
        redactor.RememberAnswer(XcodesPromptKind.Password, "private-password");
        string text = redactor.ForError(raw);

        Assert.Contains(XcodesOutputLog.LongLineOmitted, text);
        Assert.Contains("Error: [redacted]", text);
        Assert.DoesNotContain("secret-fragment", text);
        Assert.DoesNotContain("private-password", text);
    }
}
