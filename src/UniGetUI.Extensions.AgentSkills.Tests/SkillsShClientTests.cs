using System.Net;
using System.Text;
using System.Text.Json;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Extensions.AgentSkills.Tests;

public sealed class SkillsShClientTests
{
    private const string ValidEntry = """{"id":"contoso/skills/review","name":"review","source":"contoso/skills"}""";
    private const string ValidPayload = """{"skills":[""" + ValidEntry + "]}";

    [Fact]
    public void Search_TrimsAndEncodesQueryAtFixedEndpointWithBoundedLimit()
    {
        using var handler = new RecordingHandler(() => JsonResponse(ValidPayload));
        var client = new SkillsShClient(handler);

        var result = Assert.Single(client.Search("  c# & café/?  "));

        Assert.Equal("https://skills.sh/api/search?q=c%23%20%26%20caf%C3%A9%2F%3F&limit=50",
            handler.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.False(string.IsNullOrWhiteSpace(handler.UserAgent));
        Assert.True(handler.CanCancel);
        Assert.Equal("review", result.Name);
        Assert.Equal("https://github.com/contoso/skills", result.Source.InstallSource);
        Assert.Equal(TimeSpan.FromSeconds(10), SkillsShClient.RequestTimeout);
        // The injected handler remains usable after a search disposes its HttpClient.
        Assert.Single(client.Search("ab"));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void Search_ShortTrimmedQueriesDoNotSendRequests()
    {
        using var handler = new RecordingHandler(() => JsonResponse(ValidPayload));
        var client = new SkillsShClient(handler);
        foreach (string query in new[] { "", " ", "\t\r\n", "a", " a " })
            Assert.Empty(client.Search(query));
        Assert.Equal(0, handler.Calls);

        Assert.Equal("review", Assert.Single(client.Search(" ab ")).Name);
        Assert.Equal("https://skills.sh/api/search?q=ab&limit=50", handler.RequestUri!.AbsoluteUri);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Search_PreservesRankAndDeduplicatesCanonicalSourceAndSkill()
    {
        const string payload = """
            {"skills":[
              {"id":"Contoso/Skills/zebra","name":"zebra","source":"Contoso/Skills","skillId":"zebra"},
              {"id":"contoso/skills/alpha","name":"alpha","source":"contoso/skills"},
              {"id":"contoso/skills/zebra","name":"zebra","source":"contoso/skills"},
              {"id":"fabrikam/skills/zebra","name":"zebra","source":"fabrikam/skills"}]}
            """;
        using var handler = new RecordingHandler(() => JsonResponse(payload));

        var results = new SkillsShClient(handler).Search("review");

        Assert.Equal(["contoso/skills:zebra", "contoso/skills:alpha", "fabrikam/skills:zebra"],
            results.Select(result => $"{result.Source.Name}:{result.Name}"));
        Assert.Equal(["https://github.com/Contoso/Skills", "https://github.com/contoso/skills",
            "https://github.com/fabrikam/skills"], results.Select(result => result.Source.InstallSource));
        Assert.All(results, result => Assert.True(result.Source.IsGitHub));
    }

    public enum InvalidEntry
    {
        Null, MissingName, NullName, EmptyName, LeadingDot, SlashName, WildcardName, ControlName,
        TooLongName, MissingSource, NullSource, LocalPath, GitHubUrl, NonGitHubUrl, TreePath,
        SourceSelector, DotGitSuffix, Traversal, Credentials, MissingId, NullId, OtherRepoId,
        OtherNameId, MismatchedSkillId, InvalidSkillId, TooLongSkillId, TrailingNewlineName,
        TrailingNewlineSlug, BackslashName, LeadingHyphenName,
        PrefixedRepository,
    }

    [Theory]
    [InlineData(InvalidEntry.Null)]
    [InlineData(InvalidEntry.MissingName)]
    [InlineData(InvalidEntry.NullName)]
    [InlineData(InvalidEntry.EmptyName)]
    [InlineData(InvalidEntry.LeadingDot)]
    [InlineData(InvalidEntry.SlashName)]
    [InlineData(InvalidEntry.WildcardName)]
    [InlineData(InvalidEntry.ControlName)]
    [InlineData(InvalidEntry.TooLongName)]
    [InlineData(InvalidEntry.MissingSource)]
    [InlineData(InvalidEntry.NullSource)]
    [InlineData(InvalidEntry.LocalPath)]
    [InlineData(InvalidEntry.GitHubUrl)]
    [InlineData(InvalidEntry.NonGitHubUrl)]
    [InlineData(InvalidEntry.TreePath)]
    [InlineData(InvalidEntry.SourceSelector)]
    [InlineData(InvalidEntry.DotGitSuffix)]
    [InlineData(InvalidEntry.Traversal)]
    [InlineData(InvalidEntry.Credentials)]
    [InlineData(InvalidEntry.MissingId)]
    [InlineData(InvalidEntry.NullId)]
    [InlineData(InvalidEntry.OtherRepoId)]
    [InlineData(InvalidEntry.OtherNameId)]
    [InlineData(InvalidEntry.MismatchedSkillId)]
    [InlineData(InvalidEntry.InvalidSkillId)]
    [InlineData(InvalidEntry.TooLongSkillId)]
    [InlineData(InvalidEntry.TrailingNewlineName)]
    [InlineData(InvalidEntry.TrailingNewlineSlug)]
    [InlineData(InvalidEntry.BackslashName)]
    [InlineData(InvalidEntry.LeadingHyphenName)]
    [InlineData(InvalidEntry.PrefixedRepository)]
    public void Search_RejectsInvalidEntriesWithoutDroppingValidNeighbors(InvalidEntry kind)
    {
        var entry = new Dictionary<string, string?>
        {
            ["name"] = "bad",
            ["source"] = "contoso/skills",
            ["id"] = "contoso/skills/bad",
            ["skillId"] = "bad",
        };
        switch (kind)
        {
            case InvalidEntry.MissingName: entry.Remove("name"); break;
            case InvalidEntry.NullName: entry["name"] = null; break;
            case InvalidEntry.EmptyName: entry["name"] = ""; break;
            case InvalidEntry.LeadingDot: entry["name"] = ".bad"; break;
            case InvalidEntry.SlashName: entry["name"] = "bad/name"; break;
            case InvalidEntry.WildcardName: entry["name"] = "bad*name"; break;
            case InvalidEntry.ControlName: entry["name"] = "bad\u000Cname"; break;
            case InvalidEntry.TooLongName: entry["name"] = new string('a', 129); break;
            case InvalidEntry.MissingSource: entry.Remove("source"); break;
            case InvalidEntry.NullSource: entry["source"] = null; break;
            case InvalidEntry.LocalPath: entry["source"] = @"C:\skills"; break;
            case InvalidEntry.GitHubUrl: entry["source"] = "https://github.com/contoso/skills"; break;
            case InvalidEntry.NonGitHubUrl: entry["source"] = "https://gitlab.com/contoso/skills"; break;
            case InvalidEntry.TreePath: entry["source"] = "contoso/skills/tree/main"; break;
            case InvalidEntry.SourceSelector: entry["source"] = "contoso/skills@review"; break;
            case InvalidEntry.DotGitSuffix: entry["source"] = "contoso/skills.git"; break;
            case InvalidEntry.Traversal: entry["source"] = "contoso/.."; break;
            case InvalidEntry.Credentials: entry["source"] = "https://user:pass@github.com/contoso/skills"; break;
            case InvalidEntry.MissingId: entry.Remove("id"); break;
            case InvalidEntry.NullId: entry["id"] = null; break;
            case InvalidEntry.OtherRepoId: entry["id"] = "fabrikam/skills/bad"; break;
            case InvalidEntry.OtherNameId:
                entry["id"] = "contoso/skills/sibling";
                entry["skillId"] = "bad";
                break;
            case InvalidEntry.MismatchedSkillId: entry["skillId"] = "sibling"; break;
            case InvalidEntry.InvalidSkillId:
                entry["skillId"] = "bad:slug";
                entry["id"] = "contoso/skills/bad:slug";
                break;
            case InvalidEntry.TooLongSkillId:
                entry["skillId"] = new string('a', 129);
                entry["id"] = $"contoso/skills/{entry["skillId"]}";
                break;
            case InvalidEntry.TrailingNewlineName: entry["name"] = "bad\n"; break;
            case InvalidEntry.TrailingNewlineSlug:
                entry["skillId"] = "bad\n";
                entry["id"] = "contoso/skills/bad\n";
                break;
            case InvalidEntry.BackslashName: entry["name"] = @"bad\name"; break;
            case InvalidEntry.LeadingHyphenName: entry["name"] = "-bad"; break;
            case InvalidEntry.PrefixedRepository: entry["source"] = "github:contoso/skills"; break;
        }
        // Name is the install selector, independent of the valid id/slug above. A broken name
        // validator must not be masked by the slug validator rejecting the same unsafe value.
        // Likewise, unsafe sources get internally consistent ids, isolating source validation.
        if (kind is >= InvalidEntry.LocalPath and <= InvalidEntry.Credentials or InvalidEntry.PrefixedRepository)
            entry["id"] = $"{entry["source"]}/bad";
        string invalid = kind == InvalidEntry.Null ? "null" : JsonSerializer.Serialize(entry);
        string payload = """{"skills":[""" + ValidEntry + "," + invalid
            + """,{"id":"fabrikam/skills/after","name":"after","source":"fabrikam/skills"}]}""";
        using var handler = new RecordingHandler(() => JsonResponse(payload));

        var results = new SkillsShClient(handler).Search("ab");

        Assert.Equal(["contoso/skills:review", "fabrikam/skills:after"],
            results.Select(result => $"{result.Source.Name}:{result.Name}"));
    }

    [Fact]
    public void Search_AcceptsNameLengthBoundariesAndOptionalSelector()
    {
        string longName = "A" + new string('b', 124) + "._-";
        string payload = """{"skills":[{"id":"contoso/skills/a","name":"a","source":"contoso/skills","skillId":null},"""
            + $$"""{"id":"contoso/skills/{{longName}}","name":"{{longName}}","source":"contoso/skills","skillId":"{{longName}}"}]}""";
        using var handler = new RecordingHandler(() => JsonResponse(payload));

        Assert.Equal(["a", longName], new SkillsShClient(handler).Search("ab").Select(result => result.Name));
        Assert.Equal(128, longName.Length);
    }

    [Fact]
    public void Search_PreservesInstallSelectorDistinctFromSlugAndSupportsHttpsWebsiteSources()
    {
        const string payload = """
            {"skills":[
              {"id":"google-labs-code/stitch-skills/reactcomponents","source":"google-labs-code/stitch-skills",
               "skillId":"reactcomponents","name":"react:components"},
              {"id":"open.feishu.cn/components","source":"open.feishu.cn","name":"组件 review:2"},
              {"id":"uizze.sh/components","source":"uizze.sh","skillId":"components","name":"react:components"},
              {"id":"UIZZE.SH/COMPONENTS","source":"UIZZE.SH","skillId":"components","name":"react:components"},
              {"id":"www.feishu.cn/aliased","source":"www.feishu.cn","skillId":"aliased","name":"Alias review"},
              {"id":"feishu.cn/aliased","source":"feishu.cn","skillId":"aliased","name":"Alias review"}]}
            """;
        using var handler = new RecordingHandler(() => JsonResponse(payload));

        var results = new SkillsShClient(handler).Search("react");

        Assert.Equal(["react:components", "组件 review:2", "react:components", "Alias review"], results.Select(result => result.Name));
        Assert.Equal(["google-labs-code/stitch-skills", "open.feishu.cn", "uizze.sh", "feishu.cn"],
            results.Select(result => result.Source.Name));
        Assert.Equal(["https://github.com/google-labs-code/stitch-skills", "https://open.feishu.cn", "https://uizze.sh", "https://www.feishu.cn"],
            results.Select(result => result.Source.InstallSource));
        Assert.Equal([SkillSourceKind.Repository, SkillSourceKind.Index, SkillSourceKind.Index, SkillSourceKind.Index],
            results.Select(result => result.Source.Kind));
    }

    [Fact]
    public void Search_AcceptsWebsiteLabelAndHostLengthBoundaries()
    {
        string labelLimit = new string('a', 63) + ".sh";
        string hostLimit = string.Join('.', new string('a', 63), new string('b', 63),
            new string('c', 63), new string('d', 58), "sh");
        string tldLimit = "skills." + new string('a', 63);
        string[] hosts = [labelLimit, hostLimit, tldLimit];
        string payload = JsonSerializer.Serialize(new
        {
            skills = hosts.Select(host => new { id = $"{host}/review", source = host, name = "review" }).ToArray(),
        });
        using var handler = new RecordingHandler(() => JsonResponse(payload));

        var results = new SkillsShClient(handler).Search("review");

        Assert.Equal(253, hostLimit.Length);
        Assert.Equal(hosts, results.Select(result => result.Source.Name));
        Assert.Equal(hosts.Select(host => $"https://{host}"), results.Select(result => result.Source.InstallSource));
    }

    public enum InvalidWebsite
    {
        Localhost, LocalSuffix, LocaldomainSuffix, InternalSuffix, TestSuffix, InvalidSuffix, ExampleSuffix,
        Ipv4, Ipv6, Url, Credentials, Port, Path, LeadingHyphen, TrailingHyphen, EmptyLabel, Underscore,
        NumericTld, LongLabel, LongHost, TrailingNewline, LongTld, LocalhostSuffix,
    }

    [Theory]
    [InlineData(InvalidWebsite.Localhost)]
    [InlineData(InvalidWebsite.LocalSuffix)]
    [InlineData(InvalidWebsite.LocaldomainSuffix)]
    [InlineData(InvalidWebsite.InternalSuffix)]
    [InlineData(InvalidWebsite.TestSuffix)]
    [InlineData(InvalidWebsite.InvalidSuffix)]
    [InlineData(InvalidWebsite.ExampleSuffix)]
    [InlineData(InvalidWebsite.Ipv4)]
    [InlineData(InvalidWebsite.Ipv6)]
    [InlineData(InvalidWebsite.Url)]
    [InlineData(InvalidWebsite.Credentials)]
    [InlineData(InvalidWebsite.Port)]
    [InlineData(InvalidWebsite.Path)]
    [InlineData(InvalidWebsite.LeadingHyphen)]
    [InlineData(InvalidWebsite.TrailingHyphen)]
    [InlineData(InvalidWebsite.EmptyLabel)]
    [InlineData(InvalidWebsite.Underscore)]
    [InlineData(InvalidWebsite.NumericTld)]
    [InlineData(InvalidWebsite.LongLabel)]
    [InlineData(InvalidWebsite.LongHost)]
    [InlineData(InvalidWebsite.TrailingNewline)]
    [InlineData(InvalidWebsite.LongTld)]
    [InlineData(InvalidWebsite.LocalhostSuffix)]
    public void Search_RejectsNonPublicOrNonCanonicalWebsiteSources(InvalidWebsite kind)
    {
        string host = kind switch
        {
            InvalidWebsite.Localhost => "localhost",
            InvalidWebsite.LocalSuffix => "skills.LOCAL",
            InvalidWebsite.LocaldomainSuffix => "skills.localdomain",
            InvalidWebsite.InternalSuffix => "skills.internal",
            InvalidWebsite.TestSuffix => "skills.test",
            InvalidWebsite.InvalidSuffix => "skills.invalid",
            InvalidWebsite.ExampleSuffix => "skills.example",
            InvalidWebsite.Ipv4 => "127.0.0.1",
            InvalidWebsite.Ipv6 => "[::1]",
            InvalidWebsite.Url => "https://uizze.sh",
            InvalidWebsite.Credentials => "user:pass@uizze.sh",
            InvalidWebsite.Port => "uizze.sh:443",
            InvalidWebsite.Path => "uizze.sh/team",
            InvalidWebsite.LeadingHyphen => "-skills.sh",
            InvalidWebsite.TrailingHyphen => "skills-.sh",
            InvalidWebsite.EmptyLabel => "skills..sh",
            InvalidWebsite.Underscore => "my_skills.sh",
            InvalidWebsite.NumericTld => "skills.123",
            InvalidWebsite.LongLabel => new string('a', 64) + ".sh",
            InvalidWebsite.TrailingNewline => "uizze.sh\n",
            InvalidWebsite.LongTld => "skills." + new string('a', 64),
            InvalidWebsite.LocalhostSuffix => "skills.localhost",
            _ => string.Join('.', new string('a', 63), new string('b', 63),
                new string('c', 63), new string('d', 59), "sh"),
        };
        string payload = JsonSerializer.Serialize(new
        {
            skills = new[] { new { id = $"{host}/review", source = host, name = "review", skillId = "review" } },
        });
        using var handler = new RecordingHandler(() => JsonResponse(payload));

        Assert.Empty(new SkillsShClient(handler).Search("review"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Search_InspectsOnlyFirstFiftyEntries()
    {
        string[] entries = Enumerable.Range(0, 51)
            .Select(i => $$"""{"id":"contoso/skills/skill-{{i}}","name":"skill-{{i}}","source":"contoso/skills"}""")
            .ToArray();
        using var handler = new RecordingHandler(() => JsonResponse("""{"skills":[""" + string.Join(',', entries) + "]}"));

        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"skill-{i}"),
            new SkillsShClient(handler).Search("ab").Select(result => result.Name));
    }

    [Fact]
    public void Search_DoesNotFillInvalidFirstFiftySlotsFromLaterEntries()
    {
        string payload = """{"skills":[""" + string.Join(',', Enumerable.Repeat("null", 50)) + "," + ValidEntry + "]}";
        using var handler = new RecordingHandler(() => JsonResponse(payload));

        Assert.Empty(new SkillsShClient(handler).Search("ab"));
    }

    public enum InvalidPayload { Malformed, NullRoot, MissingList, NullList, ObjectList, StringEntry, WrongFieldType }

    [Theory]
    [InlineData(InvalidPayload.Malformed)]
    [InlineData(InvalidPayload.NullRoot)]
    [InlineData(InvalidPayload.MissingList)]
    [InlineData(InvalidPayload.NullList)]
    [InlineData(InvalidPayload.ObjectList)]
    [InlineData(InvalidPayload.StringEntry)]
    [InlineData(InvalidPayload.WrongFieldType)]
    public void Search_RejectsMissingOrMalformedSkillLists(InvalidPayload kind)
    {
        string payload = kind switch
        {
            InvalidPayload.Malformed => "{",
            InvalidPayload.NullRoot => "null",
            InvalidPayload.MissingList => "{}",
            InvalidPayload.NullList => """{"skills":null}""",
            InvalidPayload.ObjectList => """{"skills":{}}""",
            InvalidPayload.StringEntry => """{"skills":["bad"]}""",
            _ => """{"skills":[{"name":42}]}""",
        };
        using var handler = new RecordingHandler(() => JsonResponse(payload));

        Assert.Throws<JsonException>(() => new SkillsShClient(handler).Search("ab"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Search_AcceptsAnEmptySkillsList()
    {
        using var handler = new RecordingHandler(() => JsonResponse("""{"skills":[]}"""));
        Assert.Empty(new SkillsShClient(handler).Search("ab"));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void Search_PropagatesHttpFailure(HttpStatusCode status)
    {
        using var handler = new RecordingHandler(() => new HttpResponseMessage(status) { Content = new StringContent(ValidPayload) });
        var error = Assert.Throws<HttpRequestException>(() => new SkillsShClient(handler).Search("ab"));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Search_PropagatesNetworkFailure()
    {
        var expected = new HttpRequestException("offline fake handler");
        using var handler = new RecordingHandler(() => throw expected);
        Assert.Same(expected, Assert.Throws<HttpRequestException>(() => new SkillsShClient(handler).Search("ab")));
    }

    [Fact]
    public void Search_PropagatesCancellationWithoutReturningResults()
    {
        using var handler = new RecordingHandler(() => throw new OperationCanceledException("cancelled fake request"));
        Assert.ThrowsAny<OperationCanceledException>(() => new SkillsShClient(handler).Search("ab"));
        Assert.True(handler.CanCancel);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Search_EnforcesResponseByteBoundary(bool knownLength)
    {
        const int maximumBytes = 1024 * 1024;
        byte[] bytes = Encoding.UTF8.GetBytes(ValidPayload.PadRight(maximumBytes, ' '));
        using var exact = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new BufferedContent(bytes, knownLength),
        });
        Assert.Equal("review", Assert.Single(new SkillsShClient(exact).Search("ab")).Name);
        using var oversized = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new BufferedContent([.. bytes, (byte)' '], knownLength),
        });
        Assert.Throws<HttpRequestException>(() => new SkillsShClient(oversized).Search("ab"));
        Assert.Equal(1, oversized.Calls);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? UserAgent { get; private set; }
        public bool CanCancel { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestUri = request.RequestUri;
            Method = request.Method;
            UserAgent = request.Headers.UserAgent.ToString();
            CanCancel = cancellationToken.CanBeCanceled;
            return Task.FromResult(respond());
        }
    }

    private sealed class BufferedContent(byte[] bytes, bool knownLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return knownLength;
        }
    }
}
