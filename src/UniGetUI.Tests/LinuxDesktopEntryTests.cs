using System.Buffers.Binary;
using System.Text;
using Avalonia.Headless;
using UniGetUI.Avalonia.Infrastructure;
using Outcome = UniGetUI.Avalonia.Infrastructure.LinuxDesktopEntry.Outcome;

namespace UniGetUI.Tests;

public sealed class LinuxDesktopEntryTests : IDisposable
{
    private static readonly byte[] IconBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LinuxDesktopEntryTests).Assembly);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "unigetui-desktop-entry-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataHome;
    private readonly string _systemDataDir;
    private readonly LinuxDesktopEntry.Assets _assets;

    public LinuxDesktopEntryTests()
    {
        _dataHome = Path.Combine(_root, "home");
        _systemDataDir = Path.Combine(_root, "usr-share");
        Directory.CreateDirectory(_systemDataDir);
        LinuxDesktopEntry.Assets real = _session.Dispatch(LinuxDesktopEntry.LoadAssets, CancellationToken.None).GetAwaiter().GetResult();
        _assets = real with { Icon = IconBytes, Locale = "fr", LocalizedComment = "Interface graphique pour vos gestionnaires de paquets" };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string EntryPath => Path.Combine(_dataHome, "applications", LinuxDesktopEntry.EntryFileName);
    private string IconPath => LinuxDesktopEntry.GetIconPath(_dataHome);
    private string SystemEntryPath => Path.Combine(_systemDataDir, "applications", LinuxDesktopEntry.EntryFileName);

    private Outcome Ensure(string executable, LinuxDesktopEntry.Assets? assets = null) =>
        LinuxDesktopEntry.Ensure(executable, _dataHome, [_systemDataDir], assets ?? _assets);

    private void AddSystemEntry()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SystemEntryPath)!);
        File.WriteAllText(SystemEntryPath, "[Desktop Entry]\nType=Application\nName=UniGetUI\nExec=/usr/bin/unigetui\n");
    }

    private string ExistingExecutable()
    {
        string path = Path.Combine(_root, "other-copy", "UniGetUI");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void FirstRun_WritesTheEntryAndAnIconOutsideTheIconTheme()
    {
        Assert.Equal(Outcome.Created, Ensure("/home/user/.local/bin/UniGetUI.linux-x64/UniGetUI"));

        Dictionary<string, string> entry = ParseDesktopEntry(File.ReadAllText(EntryPath));
        Assert.Equal("\"/home/user/.local/bin/UniGetUI.linux-x64/UniGetUI\"", entry["Exec"]);
        Assert.Equal("/home/user/.local/bin/UniGetUI.linux-x64/UniGetUI", entry["TryExec"]);
        Assert.Equal(IconPath, UnescapeString(entry["Icon"]));
        Assert.Equal(LinuxDesktopEntry.EnglishComment, entry["Comment"]);
        Assert.Equal("Interface graphique pour vos gestionnaires de paquets", entry["Comment[fr]"]);
        Assert.Equal("true", entry["X-UniGetUI-Generated"]);
        Assert.Equal(IconBytes, File.ReadAllBytes(IconPath));
        Assert.DoesNotContain(Path.Combine("icons", "hicolor"), IconPath);
    }

    [Fact]
    public void SecondRun_FromTheSameFolder_IsUpToDate()
    {
        Ensure("/opt/portable/UniGetUI");
        string before = File.ReadAllText(EntryPath);

        Assert.Equal(Outcome.UpToDate, Ensure("/opt/portable/UniGetUI"));
        Assert.Equal(before, File.ReadAllText(EntryPath));
    }

    [Fact]
    public void MenuEditorChanges_AreKeptOnTheNextLaunch()
    {
        Ensure("/opt/portable/UniGetUI");
        string edited = File.ReadAllText(EntryPath)
            .Replace("Name=UniGetUI\n", "Name=My package GUI\n")
            .Replace("Exec=\"/opt/portable/UniGetUI\"\n", "Exec=\"/opt/portable/UniGetUI\" --daemon\n")
            + "NoDisplay=true\n";
        File.WriteAllText(EntryPath, edited);

        Assert.Equal(Outcome.UpToDate, Ensure("/opt/portable/UniGetUI"));
        Assert.Equal(edited, File.ReadAllText(EntryPath));
    }

    [Fact]
    public void MovedFolder_UpdatesOnlyTheManagedKeys()
    {
        Ensure("/old/UniGetUI");
        File.WriteAllText(EntryPath, File.ReadAllText(EntryPath).Replace("Name=UniGetUI\n", "Name=Renamed\n") + "NoDisplay=true\n");

        Assert.Equal(Outcome.Updated, Ensure("/new/UniGetUI"));

        Dictionary<string, string> entry = ParseDesktopEntry(File.ReadAllText(EntryPath));
        Assert.Equal("\"/new/UniGetUI\"", entry["Exec"]);
        Assert.Equal("/new/UniGetUI", entry["TryExec"]);
        Assert.Equal("Renamed", entry["Name"]);
        Assert.Equal("true", entry["NoDisplay"]);
    }

    [Fact]
    public void MovedFolder_RestoresManagedKeysTheEditorRemoved()
    {
        Ensure("/old/UniGetUI");
        string[] kept = File.ReadAllText(EntryPath).Split('\n')
            .Where(line => !line.StartsWith("TryExec=", StringComparison.Ordinal) && !line.StartsWith("Icon=", StringComparison.Ordinal))
            .ToArray();
        File.WriteAllText(EntryPath, string.Join('\n', kept));

        Assert.Equal(Outcome.Updated, Ensure("/new/UniGetUI"));
        Dictionary<string, string> entry = ParseDesktopEntry(File.ReadAllText(EntryPath));
        Assert.Equal("/new/UniGetUI", entry["TryExec"]);
        Assert.Equal(IconPath, UnescapeString(entry["Icon"]));
    }

    [Fact]
    public void AnotherInstalledCopy_KeepsTheEntry()
    {
        string other = ExistingExecutable();
        Ensure(other);
        string before = File.ReadAllText(EntryPath);

        Assert.Equal(Outcome.OwnedByAnotherCopy, Ensure("/home/user/beta/UniGetUI"));
        Assert.Equal(before, File.ReadAllText(EntryPath));
        Assert.Equal(Outcome.UpToDate, Ensure(other));
    }

    [Fact]
    public void ChangedBundledIcon_IsRefreshed()
    {
        Ensure("/opt/portable/UniGetUI");
        byte[] newIcon = [0x89, 0x50, 0x4E, 0x47, 9, 9, 9, 9];

        Assert.Equal(Outcome.Updated, Ensure("/opt/portable/UniGetUI", _assets with { Icon = newIcon }));
        Assert.Equal(newIcon, File.ReadAllBytes(IconPath));
    }

    [Fact]
    public void MissingIcon_IsRestored()
    {
        Ensure("/opt/portable/UniGetUI");
        File.Delete(IconPath);

        Assert.Equal(Outcome.Updated, Ensure("/opt/portable/UniGetUI"));
        Assert.True(File.Exists(IconPath));
    }

    [Fact]
    public void EntryDeletedByTheUser_IsNotRecreatedByAnyCopy()
    {
        Ensure("/opt/portable/UniGetUI");
        File.Delete(EntryPath);

        Assert.Equal(Outcome.RemovedByUser, Ensure("/opt/portable/UniGetUI"));
        Assert.Equal(Outcome.RemovedByUser, Ensure("/home/user/another-copy/UniGetUI"));
        Assert.False(File.Exists(EntryPath));
    }

    [Fact]
    public void EntryWrittenByTheUser_IsLeftAlone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        const string custom = "[Desktop Entry]\nType=Application\nName=My UniGetUI\nExec=/somewhere/UniGetUI\n";
        File.WriteAllText(EntryPath, custom);

        Assert.Equal(Outcome.NotGeneratedByUniGetUI, Ensure("/opt/portable/UniGetUI"));
        Assert.Equal(custom, File.ReadAllText(EntryPath));
        Assert.False(File.Exists(IconPath));
    }

    [Fact]
    public void PackageInstalledEntry_SkipsTheUserEntry()
    {
        AddSystemEntry();

        Assert.Equal(Outcome.SystemEntryExists, Ensure("/opt/unigetui/UniGetUI"));
        Assert.False(File.Exists(EntryPath));
    }

    [Fact]
    public void PackageInstalledLater_RemovesTheGeneratedEntryThatWouldShadowIt()
    {
        Ensure("/home/user/Apps/UniGetUI");
        AddSystemEntry();

        Assert.Equal(Outcome.ReplacedBySystemEntry, Ensure("/opt/unigetui/UniGetUI"));
        Assert.False(File.Exists(EntryPath));
        Assert.False(File.Exists(IconPath));

        File.Delete(SystemEntryPath);
        Assert.Equal(Outcome.Created, Ensure("/home/user/Apps/UniGetUI"));
    }

    [Fact]
    public void PackageInstalledLater_KeepsAUserWrittenEntry()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        File.WriteAllText(EntryPath, "[Desktop Entry]\nName=Mine\n");
        AddSystemEntry();

        Assert.Equal(Outcome.SystemEntryExists, Ensure("/opt/unigetui/UniGetUI"));
        Assert.True(File.Exists(EntryPath));
    }

    [Fact]
    public void ExecutablePathWithANewline_IsSkipped()
    {
        Assert.Equal(Outcome.Skipped, Ensure("/tmp/bad\nname/UniGetUI"));
        Assert.False(File.Exists(EntryPath));
    }

    [Theory]
    [InlineData("SNAP", "/snap/code/200", true)]
    [InlineData("FLATPAK_ID", "com.visualstudio.code", true)]
    [InlineData("SNAP", "", false)]
    [InlineData("HOME", "/home/user", false)]
    public void SandboxedParents_AreDetected(string variable, string value, bool expected)
    {
        Assert.Equal(expected, LinuxDesktopEntry.IsSandboxed(name => name == variable ? value : null));
    }

    [Theory]
    [InlineData(null, new[] { "/usr/local/share", "/usr/share" })]
    [InlineData("", new[] { "/usr/local/share", "/usr/share" })]
    [InlineData("/snap/code/200/usr/share", new[] { "/snap/code/200/usr/share", "/usr/local/share", "/usr/share" })]
    [InlineData("/usr/share/gnome:/usr/share/:relative", new[] { "/usr/share/gnome", "/usr/share/", "/usr/local/share", "/usr/share" })]
    [InlineData("/usr/share:/usr/local/share", new[] { "/usr/share", "/usr/local/share" })]
    public void DataDirs_AlwaysIncludeTheSystemDefaults(string? xdgDataDirs, string[] expected)
    {
        Assert.Equal(expected, LinuxDesktopEntry.GetDataDirs(xdgDataDirs));
    }

    [Theory]
    [InlineData("en", LinuxDesktopEntry.EnglishComment, null)]
    [InlineData("de", LinuxDesktopEntry.EnglishComment, null)]
    [InlineData("Unset/Unknown", "Anything", null)]
    [InlineData("pt_BR", "Interface gráfica", "Comment[pt_BR]=Interface gráfica")]
    [InlineData("fr", "Deux\nlignes", "Comment[fr]=Deux\\nlignes")]
    public void LocalizedComment_IsAddedOnlyWhenTranslatedForAValidLocale(string locale, string localized, string? expectedLine)
    {
        string entry = LinuxDesktopEntry.BuildEntry(
            _assets with { Locale = locale, LocalizedComment = localized }, "/opt/UniGetUI", "/icon.png");
        string[] localizedLines = entry.Split('\n').Where(l => l.StartsWith("Comment[", StringComparison.Ordinal)).ToArray();

        if (expectedLine is null)
            Assert.Empty(localizedLines);
        else
            Assert.Equal([expectedLine], localizedLines);
    }

    [Theory]
    [InlineData("/a b/UniGetUI", "\"/a b/UniGetUI\"")]
    [InlineData("/a\"b/UniGetUI", "\"/a\\\"b/UniGetUI\"")]
    [InlineData("/$HOME/`x`/UniGetUI", "\"/\\$HOME/\\`x\\`/UniGetUI\"")]
    [InlineData("/a\\b/UniGetUI", "\"/a\\\\b/UniGetUI\"")]
    [InlineData("/100%/UniGetUI", "\"/100%%/UniGetUI\"")]
    public void QuoteExecArgument_FollowsTheDesktopEntrySpec(string path, string expected)
    {
        Assert.Equal(expected, LinuxDesktopEntry.QuoteExecArgument(path));
    }

    [Fact]
    public void ExecValue_EscapesBackslashesAgainAsAString()
    {
        string entry = LinuxDesktopEntry.BuildEntry(_assets, "/a\\b/UniGetUI", "/icon.png");
        string execLine = entry.Split('\n').Single(l => l.StartsWith("Exec=", StringComparison.Ordinal));
        Assert.Equal("Exec=\"/a\\\\\\\\b/UniGetUI\"", execLine);
    }

    [Fact]
    public void TemplateWithWindowsLineEndings_StillProducesAnLfEntry()
    {
        string entry = LinuxDesktopEntry.BuildEntry(
            _assets with { Template = _assets.Template.Replace("\n", "\r\n") }, "/opt/UniGetUI", "/icon.png");
        Assert.DoesNotContain('\r', entry);
        Assert.Equal(LinuxDesktopEntry.BuildEntry(_assets, "/opt/UniGetUI", "/icon.png"), entry);
    }

    [Fact]
    public Task BundledAssets_AreTheTemplateAndThe512Png() => _session.Dispatch(() =>
    {
        LinuxDesktopEntry.Assets assets = LinuxDesktopEntry.LoadAssets();

        Assert.StartsWith("[Desktop Entry]\n", assets.Template.Replace("\r\n", "\n"));
        foreach (string placeholder in new[] { "@COMMENT@", "@EXEC@", "@ICON@" })
            Assert.Contains(placeholder, assets.Template);

        byte[] png = assets.Icon;
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(512, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(512, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }, CancellationToken.None);

    [Fact]
    public Task FullRun_WritesTheRealAssetsAndRemembersADeletedEntry() => _session.Dispatch(() =>
    {
        const string exe = "/home/user/Apps/UniGetUI.linux-x64/UniGetUI";

        Assert.Equal(Outcome.Created, LinuxDesktopEntry.EnsureFor(exe, _dataHome, [_systemDataDir]));
        Assert.Equal(LinuxDesktopEntry.LoadAssets().Icon, File.ReadAllBytes(IconPath));
        ParseDesktopEntry(File.ReadAllText(EntryPath));

        Assert.Equal(Outcome.UpToDate, LinuxDesktopEntry.EnsureFor(exe, _dataHome, [_systemDataDir]));

        File.Delete(EntryPath);
        Assert.Equal(Outcome.RemovedByUser, LinuxDesktopEntry.EnsureFor(exe, _dataHome, [_systemDataDir]));
        Assert.False(File.Exists(EntryPath));
    }, CancellationToken.None);

    [Theory]
    [InlineData("/home/user/src/UniGetUI/src/UniGetUI.Avalonia/bin/Debug/net10.0/UniGetUI")]
    [InlineData("/home/user/src/UniGetUI/src/UniGetUI.Avalonia/bin/Release/net10.0/linux-x64/UniGetUI")]
    [InlineData("/usr/lib/dotnet/dotnet")]
    [InlineData(null)]
    public void FullRun_SkipsDevBuildsAndUnknownPaths(string? exe)
    {
        Assert.Equal(Outcome.Skipped, LinuxDesktopEntry.EnsureFor(exe, _dataHome, [_systemDataDir]));
        Assert.False(File.Exists(EntryPath));
    }

    [Fact]
    public Task FullRun_UnwritableDataHome_FailsWithoutThrowing() => _session.Dispatch(() =>
    {
        Directory.CreateDirectory(_root);
        string blocker = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocker, "");

        Assert.Equal(Outcome.Failed, LinuxDesktopEntry.EnsureFor("/opt/portable/UniGetUI", blocker, [_systemDataDir]));
    }, CancellationToken.None);

    [Theory]
    [InlineData("/home/user/.local/bin/UniGetUI.linux-x64/UniGetUI")]
    [InlineData("/home/user/My Apps/UniGetUI")]
    [InlineData("/home/user/\"quoted\"/UniGetUI")]
    [InlineData("/home/user/$HOME/`whoami`/UniGetUI")]
    [InlineData("/home/user/back\\slash/UniGetUI")]
    [InlineData("/home/user/100% done/UniGetUI")]
    [InlineData("/home/user/semi;colon=equals/UniGetUI")]
    [InlineData("/home/usér/Téléchargements/UniGetUI")]
    [InlineData("/home/user/'single'/UniGetUI")]
    [InlineData("/home/user/@EXEC@ and @ICON@/UniGetUI")]
    public void GeneratedEntry_IsValidAndExecRunsTheExactPath(string exe)
    {
        const string icon = "/home/user/.local/share/UniGetUI/DesktopEntry/unigetui.png";
        Dictionary<string, string> entry = ParseDesktopEntry(LinuxDesktopEntry.BuildEntry(_assets, exe, icon));

        Assert.Equal("Application", entry["Type"]);
        Assert.Equal("UniGetUI", entry["Name"]);
        Assert.Equal("false", entry["Terminal"]);
        Assert.EndsWith(";", entry["Categories"]);
        Assert.Equal([exe], ParseExec(UnescapeString(entry["Exec"])));
        Assert.Equal(exe, UnescapeString(entry["TryExec"]));
        Assert.Equal(icon, UnescapeString(entry["Icon"]));
    }

    [Theory]
    [InlineData("/home/user/back\\slash/UniGetUI")]
    [InlineData("/home/user/$HOME/`whoami`/UniGetUI")]
    public void MovedFolder_ReadsBackItsOwnEscapedTryExec(string exe)
    {
        Ensure(exe);
        Assert.Equal(Outcome.UpToDate, Ensure(exe));
    }

    private static Dictionary<string, string> ParseDesktopEntry(string content)
    {
        Assert.EndsWith("\n", content);
        Assert.DoesNotContain("\r", content);
        string[] lines = content.TrimEnd('\n').Split('\n');
        Assert.Equal("[Desktop Entry]", lines[0]);

        Dictionary<string, string> entries = new(StringComparer.Ordinal);
        foreach (string line in lines.Skip(1))
        {
            int separator = line.IndexOf('=');
            Assert.True(separator > 0, $"Not a key=value line: {line}");
            string key = line[..separator];
            Assert.Matches(@"^[A-Za-z0-9-]+(\[[A-Za-z_@.]+\])?$", key);
            Assert.True(entries.TryAdd(key, line[(separator + 1)..]), $"Duplicate key {key}");
        }

        return entries;
    }

    private static string UnescapeString(string value)
    {
        StringBuilder result = new();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\')
            {
                result.Append(value[i]);
                continue;
            }

            Assert.True(++i < value.Length, "Dangling backslash");
            result.Append(value[i] switch
            {
                's' => ' ',
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '\\' => '\\',
                _ => throw new Xunit.Sdk.XunitException($"Invalid escape \\{value[i]}"),
            });
        }

        return result.ToString();
    }

    private static List<string> ParseExec(string exec)
    {
        const string reserved = " \t\n\"'\\><~|&;$*?#()`";
        List<string> arguments = [];
        int i = 0;
        while (i < exec.Length)
        {
            if (exec[i] == ' ')
            {
                i++;
                continue;
            }

            StringBuilder argument = new();
            if (exec[i] == '"')
            {
                i++;
                while (true)
                {
                    Assert.True(i < exec.Length, "Unterminated quoted argument");
                    char c = exec[i++];
                    if (c == '"')
                        break;
                    if (c == '\\')
                    {
                        Assert.True(i < exec.Length, "Dangling backslash in quoted argument");
                        char escaped = exec[i++];
                        Assert.Contains(escaped, "\"`$\\");
                        argument.Append(escaped);
                    }
                    else
                    {
                        Assert.NotEqual('`', c);
                        Assert.NotEqual('$', c);
                        argument.Append(c);
                    }
                }
            }
            else
            {
                while (i < exec.Length && exec[i] != ' ')
                {
                    Assert.DoesNotContain(exec[i], reserved);
                    argument.Append(exec[i++]);
                }
            }

            arguments.Add(ExpandFieldCodes(argument.ToString()));
        }

        return arguments;
    }

    private static string ExpandFieldCodes(string argument)
    {
        StringBuilder result = new();
        for (int i = 0; i < argument.Length; i++)
        {
            if (argument[i] != '%')
            {
                result.Append(argument[i]);
                continue;
            }

            Assert.True(++i < argument.Length && argument[i] == '%', "Unexpected field code");
            result.Append('%');
        }

        return result.ToString();
    }
}
