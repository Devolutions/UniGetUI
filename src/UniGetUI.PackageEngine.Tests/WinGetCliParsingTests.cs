#if WINDOWS
using UniGetUI.Core.Data;
using UniGetUI.PackageEngine.Managers.WingetManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;

namespace UniGetUI.PackageEngine.Tests;

[Collection(WinGetManagerTestCollection.Name)]
public sealed class WinGetCliParsingTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        "WinGetCliParsingTests",
        Guid.NewGuid().ToString("N")
    );

    public WinGetCliParsingTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
    }

    public void Dispose()
    {
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static string[] Lines(string output) =>
        output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.None);

    [Fact]
    public void ParseInstalledPackagesReadsJapaneseLocalizedTable()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                名前                   ID                                                                      バージョン     利用可能 ソース
                -----------------------------------------------------------------------------------------------------------------------------
                7-Zip 24.09 (x64)      7zip.7zip                                                               24.09          25.00    winget
                サクラエディタ         SakuraEditor.SakuraEditor                                               2.4.2                   winget
                Copilot                ARP\Machine\X86\Microsoft Copilot                                       152.0.4191.66
                Xbox Identity Provider MSIX\Microsoft.XboxIdentityProvider_12.130.16001.0_arm64__8wekyb3d8bbwe 12.130.16001.0
                利用可能なアップグレードが 1 件あります。
                """
            )
        );

        Assert.Equal(4, packages.Count);
        PackageAssert.Matches(packages[0], "7-Zip 24.09 (x64)", "7zip.7zip", "24.09");
        PackageAssert.Matches(
            packages[1],
            "サクラエディタ",
            "SakuraEditor.SakuraEditor",
            "2.4.2"
        );
        PackageAssert.Matches(
            packages[2],
            "Copilot",
            @"ARP\Machine\X86\Microsoft Copilot",
            "152.0.4191.66"
        );
        PackageAssert.Matches(
            packages[3],
            "Xbox Identity Provider",
            @"MSIX\Microsoft.XboxIdentityProvider_12.130.16001.0_arm64__8wekyb3d8bbwe",
            "12.130.16001.0"
        );

        Assert.Equal("winget", packages[0].Source.Name);
        Assert.Same(manager.LocalPcSource, packages[2].Source);
        Assert.Same(manager.MicrosoftStoreSource, packages[3].Source);
    }

    [Fact]
    public void ParseInstalledPackagesReadsEnglishTable()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                Name                   Id                                                                      Version        Available Source
                ------------------------------------------------------------------------------------------------------------------------------
                7-Zip 24.09 (x64)      7zip.7zip                                                               24.09          25.00     winget
                Copilot                ARP\Machine\X86\Microsoft Copilot                                       152.0.4191.66
                Xbox Identity Provider MSIX\Microsoft.XboxIdentityProvider_12.130.16001.0_arm64__8wekyb3d8bbwe 12.130.16001.0
                """
            )
        );

        Assert.Equal(3, packages.Count);
        PackageAssert.Matches(packages[0], "7-Zip 24.09 (x64)", "7zip.7zip", "24.09");
        PackageAssert.Matches(
            packages[1],
            "Copilot",
            @"ARP\Machine\X86\Microsoft Copilot",
            "152.0.4191.66"
        );
        Assert.Same(manager.MicrosoftStoreSource, packages[2].Source);
    }

    [Fact]
    public void ParseInstalledPackagesReadsUntranslatedResourceKeyHeaders()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                SearchName        SearchId  SearchVersion AvailableHeader SearchSource
                ----------------------------------------------------------------------
                7-Zip 24.09 (x64) 7zip.7zip 24.09         25.00           winget
                """
            )
        );

        PackageAssert.Matches(
            Assert.Single(packages),
            "7-Zip 24.09 (x64)",
            "7zip.7zip",
            "24.09"
        );
    }

    [Fact]
    public void ParseAvailableUpdatesReadsJapaneseLocalizedTable()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseAvailableUpdates(
            manager,
            Lines(
                """
                名前              ID                        バージョン 利用可能 ソース
                ----------------------------------------------------------------------
                7-Zip 24.09 (x64) 7zip.7zip                 24.09      25.00    winget
                サクラエディタ    SakuraEditor.SakuraEditor 2.4.2      2.4.3    winget
                2 個のアップグレードが利用可能です。
                """
            )
        );

        Assert.Equal(2, packages.Count);
        PackageAssert.Matches(packages[0], "7-Zip 24.09 (x64)", "7zip.7zip", "24.09", "25.00");
        PackageAssert.Matches(
            packages[1],
            "サクラエディタ",
            "SakuraEditor.SakuraEditor",
            "2.4.2",
            "2.4.3"
        );
    }

    [Fact]
    public void ParseAvailableUpdatesReadsHeadersWhoseColumnNameContainsASpace()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseAvailableUpdates(
            manager,
            Lines(
                """
                이름              장치 ID             버전  사용 가능 원본
                ------------------------------------------------------------
                7-Zip 24.09 (x64) 7zip.7zip           24.09 25.00     winget
                메모장            Notepad++.Notepad++ 8.9.7 8.9.8     winget
                사용 가능한 업그레이드 2개
                """
            )
        );

        Assert.Equal(2, packages.Count);
        PackageAssert.Matches(packages[0], "7-Zip 24.09 (x64)", "7zip.7zip", "24.09", "25.00");
        PackageAssert.Matches(packages[1], "메모장", "Notepad++.Notepad++", "8.9.7", "8.9.8");
        Assert.Equal("winget", packages[1].Source.Name);
    }

    [Fact]
    public void ParseAvailableUpdatesKeepsSingleSpacedColumns()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseAvailableUpdates(
            manager,
            Lines(
                """
                Name   Id               Version     Available Source
                ----------------------------------------------------
                Claude Anthropic.Claude 1.24012.0.0 1.44121.2 winget
                1 upgrades available.
                """
            )
        );

        PackageAssert.Matches(
            Assert.Single(packages),
            "Claude",
            "Anthropic.Claude",
            "1.24012.0.0",
            "1.44121.2"
        );
    }

    [Fact]
    public void ParseFoundPackagesReadsJapaneseLocalizedTableWithMatchColumn()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseFoundPackages(
            manager,
            Lines(
                """
                名前                         ID                         バージョン   一致             ソース
                --------------------------------------------------------------------------------------------
                Microsoft Visual Studio Code Microsoft.VisualStudioCode 1.136.1      モニカー: vscode winget
                Codium                       Alex313031.Codium          1.93.1.24277 タグ: vscode     winget
                """
            )
        );

        Assert.Equal(2, packages.Count);
        PackageAssert.Matches(
            packages[0],
            "Microsoft Visual Studio Code",
            "Microsoft.VisualStudioCode",
            "1.136.1"
        );
        PackageAssert.Matches(packages[1], "Codium", "Alex313031.Codium", "1.93.1.24277");
        Assert.Equal("winget", packages[0].Source.Name);
    }

    [Fact]
    public void ParseFoundPackagesReadsRowsWithAnEmptyMatchColumn()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseFoundPackages(
            manager,
            Lines(
                """
                Name             Id                   Version              Match        Source
                -------------------------------------------------------------------------------
                VLC              XPDM1ZW6815MQM       Unknown                           msstore
                VLC UWP          9NBLGGH4VVNH         Unknown                           msstore
                VLC media player VideoLAN.VLC         3.0.21               Moniker: vlc winget
                """
            )
        );

        Assert.Equal(3, packages.Count);
        PackageAssert.Matches(packages[0], "VLC", "XPDM1ZW6815MQM", "Unknown");
        Assert.Equal("msstore", packages[0].Source.Name);
        PackageAssert.Matches(packages[2], "VLC media player", "VideoLAN.VLC", "3.0.21");
        Assert.Equal("winget", packages[2].Source.Name);
    }

    [Fact]
    public void ParseInstalledPackagesReadsCapturedJapaneseWinGetOutput()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                名前                                  ID                                    バージョン
                ---------------------------------------------------------------------------------------------
                インテル® グラフィックス・コマンド・… AppUp.IntelGraphicsExperience_8j3eq9… 1.100.3370.0
                Ubuntu                                CanonicalGroupLimited.UbuntuonWindow… 2004.2021.222.0
                CrystalDiskMark                       CrystalDewWorld.CrystalDiskMark       8.0.4
                Docker Desktop                        Docker.DockerDesktop                  3.5.2
                ELAN Touchpad 12.11.3.2_X64_Beta      Elantech                              12.11.3.2
                Microsoft Edge                        Microsoft.Edge                        92.0.902.55
                Microsoft Edge Update                 Microsoft Edge Update                 1.3.145.49
                """
            )
        );

        Assert.Equal(7, packages.Count);
        PackageAssert.Matches(
            packages[0],
            "インテル® グラフィックス・コマンド・…",
            "AppUp.IntelGraphicsExperience_8j3eq9…",
            "1.100.3370.0"
        );
        PackageAssert.Matches(
            packages[1],
            "Ubuntu",
            "CanonicalGroupLimited.UbuntuonWindow…",
            "2004.2021.222.0"
        );
        PackageAssert.Matches(
            packages[4],
            "ELAN Touchpad 12.11.3.2_X64_Beta",
            "Elantech",
            "12.11.3.2"
        );
        PackageAssert.Matches(
            packages[6],
            "Microsoft Edge Update",
            "Microsoft Edge Update",
            "1.3.145.49"
        );
        Assert.Same(manager.LocalPcSource, packages[6].Source);
    }

    [Fact]
    public void ParseAvailableUpdatesReadsCapturedJapaneseWinGetOutput()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseAvailableUpdates(
            manager,
            Lines(
                """
                名前            ID                              バージョン 利用可能 ソース
                --------------------------------------------------------------------------
                CrystalDiskInfo CrystalDewWorld.CrystalDiskInfo 8.6.1      8.12.4   winget
                CrystalDiskMark CrystalDewWorld.CrystalDiskMark 8.0.2      8.0.4    winget
                """
            )
        );

        Assert.Equal(2, packages.Count);
        PackageAssert.Matches(
            packages[0],
            "CrystalDiskInfo",
            "CrystalDewWorld.CrystalDiskInfo",
            "8.6.1",
            "8.12.4"
        );
        PackageAssert.Matches(
            packages[1],
            "CrystalDiskMark",
            "CrystalDewWorld.CrystalDiskMark",
            "8.0.2",
            "8.0.4"
        );
        Assert.Equal("winget", packages[0].Source.Name);
    }

    [Fact]
    public void ParseAvailableUpdatesReadsCjkPackageNamesUnderEnglishHeaders()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseAvailableUpdates(
            manager,
            Lines(
                """
                Name                    Id                   Version     Available   Source
                ---------------------------------------------------------------------------
                Google 日本語入力       Google.JapaneseIME   2.31.5840.0 2.32.5990.0 winget
                Zoom Workplace (64-bit) Zoom.Zoom            6.6.19369   6.6.19875   winget
                Dropbox                 Dropbox.Dropbox      235.4.5905  236.3.5770  winget
                OBS Studio              OBSProject.OBSStudio 32.0.1      32.0.2      winget
                4 upgrades available.
                """
            )
        );

        Assert.Equal(4, packages.Count);
        PackageAssert.Matches(
            packages[0],
            "Google 日本語入力",
            "Google.JapaneseIME",
            "2.31.5840.0",
            "2.32.5990.0"
        );
        PackageAssert.Matches(
            packages[3],
            "OBS Studio",
            "OBSProject.OBSStudio",
            "32.0.1",
            "32.0.2"
        );
    }

    [Fact]
    public void ParseInstalledPackagesReadsTableWithoutAnAvailableColumn()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                Name              Id                Version Source
                --------------------------------------------------
                7-Zip 24.09 (x64) 7zip.7zip         24.09   winget
                Contoso Tool      Programs\Contoso  1.0.0
                """
            )
        );

        Assert.Equal(2, packages.Count);
        PackageAssert.Matches(packages[0], "7-Zip 24.09 (x64)", "7zip.7zip", "24.09");
        Assert.Equal("winget", packages[0].Source.Name);
        PackageAssert.Matches(packages[1], "Contoso Tool", @"Programs\Contoso", "1.0.0");
        Assert.Same(manager.LocalPcSource, packages[1].Source);
    }

    [Fact]
    public void ParseInstalledPackagesTreatsAPaddedBlankSourceCellAsLocal()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                "Name              Id                Version Source\n"
                    + "--------------------------------------------------\n"
                    + "Contoso Tool      Programs\\Contoso  1.0.0         \n"
            )
        );

        Assert.Same(manager.LocalPcSource, Assert.Single(packages).Source);
    }

    [Fact]
    public void ParseAvailableUpdatesReadsASecondTableWithoutASourceColumn()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseAvailableUpdates(
            manager,
            Lines(
                """
                Name              Id        Version Available Source
                ----------------------------------------------------
                7-Zip 24.09 (x64) 7zip.7zip 24.09   25.00     winget
                1 upgrades available.

                The following packages have an upgrade available, but require explicit targeting for upgrade:
                Name            Id               Version Available
                ------------------------------------------------------
                Fabrikam Widget Fabrikam.Widget  4.1.0   4.2.0
                """
            )
        );

        Assert.Equal(2, packages.Count);
        PackageAssert.Matches(packages[0], "7-Zip 24.09 (x64)", "7zip.7zip", "24.09", "25.00");
        Assert.Equal("winget", packages[0].Source.Name);
        PackageAssert.Matches(packages[1], "Fabrikam Widget", "Fabrikam.Widget", "4.1.0", "4.2.0");
        Assert.Same(manager.DefaultSource, packages[1].Source);
    }

    [Fact]
    public void ParseAvailableUpdatesIgnoresLocalizedTrailingMessages()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseAvailableUpdates(
            manager,
            Lines(
                """
                名前            ID                              バージョン 利用可能 ソース
                --------------------------------------------------------------------------
                CrystalDiskInfo CrystalDewWorld.CrystalDiskInfo 8.6.1      8.12.4   winget
                1 個のパッケージにはアップグレードを妨げるピンが設定されています。'winget pin' コマンドを使用してピンを表示および編集してください。'--include-pinned' 引数を使用すると、さらに多くの結果が表示される場合があります。
                """
            )
        );

        PackageAssert.Matches(
            Assert.Single(packages),
            "CrystalDiskInfo",
            "CrystalDewWorld.CrystalDiskInfo",
            "8.6.1",
            "8.12.4"
        );
    }

    [Fact]
    public void ParseInstalledPackagesRecoversFromUnderestimatedCharacterWidths()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                Name    Id           Version
                ----------------------------
                🎮 Game Contoso.Game 1.0.0
                """
            )
        );

        PackageAssert.Matches(Assert.Single(packages), "🎮 Game", "Contoso.Game", "1.0.0");
    }

    [Fact]
    public void ParseInstalledPackagesMeasuresGraphemeClustersAsOneCell()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                Name      Id             Version
                --------------------------------
                👨‍👩‍👧‍👦 Family Contoso.Family 1.0.0
                """
            )
        );

        PackageAssert.Matches(
            Assert.Single(packages),
            "👨‍👩‍👧‍👦 Family",
            "Contoso.Family",
            "1.0.0"
        );
    }

    [Fact]
    public void ParseAvailableUpdatesHandlesAMultiwordHeaderWithoutASourceColumn()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseAvailableUpdates(
            manager,
            Lines(
                """
                이름              장치 ID   버전  사용 가능
                ---------------------------------------------
                7-Zip 24.09 (x64) 7zip.7zip 24.09 2026.2.16.0
                """
            )
        );

        PackageAssert.Matches(
            Assert.Single(packages),
            "7-Zip 24.09 (x64)",
            "7zip.7zip",
            "24.09",
            "2026.2.16.0"
        );
        Assert.Same(manager.DefaultSource, packages[0].Source);
    }

    [Fact]
    public void ParseInstalledPackagesHandlesAMultiwordHeaderWithoutASourceColumn()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                이름              장치 ID   버전  사용 가능
                ---------------------------------------------
                7-Zip 24.09 (x64) 7zip.7zip 24.09 2026.2.16.0
                """
            )
        );

        PackageAssert.Matches(Assert.Single(packages), "7-Zip 24.09 (x64)", "7zip.7zip", "24.09");
        Assert.Same(manager.LocalPcSource, packages[0].Source);
    }

    [Fact]
    public void ParseInstalledPackagesReadsRealKoreanHeaders()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                이름              장치 ID             버전  사용 가능 원본
                ------------------------------------------------------------
                7-Zip 24.09 (x64) 7zip.7zip           24.09 25.00     winget
                메모장            Notepad++.Notepad++ 8.9.7 8.9.8     winget
                """
            )
        );

        Assert.Equal(2, packages.Count);
        PackageAssert.Matches(packages[0], "7-Zip 24.09 (x64)", "7zip.7zip", "24.09");
        PackageAssert.Matches(packages[1], "메모장", "Notepad++.Notepad++", "8.9.7");
        Assert.Equal("winget", packages[1].Source.Name);
    }

    [Fact]
    public void ParseInstalledPackagesReadsAnAvailableColumnWithoutASourceColumn()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                Name              Id               Version Available
                ----------------------------------------------------
                7-Zip 24.09 (x64) 7zip.7zip        24.09   25.00
                Contoso Tool      Programs\Contoso 1.0.0
                """
            )
        );

        Assert.Equal(2, packages.Count);
        PackageAssert.Matches(packages[0], "7-Zip 24.09 (x64)", "7zip.7zip", "24.09");
        PackageAssert.Matches(packages[1], "Contoso Tool", @"Programs\Contoso", "1.0.0");
        Assert.Same(manager.LocalPcSource, packages[0].Source);
        Assert.Same(manager.LocalPcSource, packages[1].Source);
    }

    [Fact]
    public void ParseInstalledPackagesTreatsAmbiguousWidthCharactersAsNarrow()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                Name       Id             Version
                ---------------------------------
                ㉈㉈㉈ Widget Contoso.Widget 1.0.0
                """
            )
        );

        PackageAssert.Matches(
            Assert.Single(packages),
            "㉈㉈㉈ Widget",
            "Contoso.Widget",
            "1.0.0"
        );
    }

    [Fact]
    public void ParseInstalledPackagesTreatsRepeatedWideEmojiAsTwoColumnsEach()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                Name        Id           Version
                --------------------------------
                🎮🎮🎮 Game Contoso.Game 2.0.0
                """
            )
        );

        PackageAssert.Matches(Assert.Single(packages), "🎮🎮🎮 Game", "Contoso.Game", "2.0.0");
    }

    [Fact]
    public void ParseAvailableUpdatesMergesKoreanIdHeaderForASingleRowTable()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseAvailableUpdates(
            manager,
            Lines(
                """
                이름              장치 ID   버전  사용 가능 원본
                --------------------------------------------------
                7-Zip 24.09 (x64) 7zip.7zip 24.09 25.00     winget
                사용 가능한 업그레이드 1개
                """
            )
        );

        PackageAssert.Matches(
            Assert.Single(packages),
            "7-Zip 24.09 (x64)",
            "7zip.7zip",
            "24.09",
            "25.00"
        );
    }

    [Fact]
    public void ParseInstalledPackagesIgnoresProseAlignedToTheIdColumn()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                Name              Id        Version Source
                ------------------------------------------
                7-Zip 24.09 (x64) 7zip.7zip 24.09   winget
                A pinned package: use the 'winget pin' command to view and edit pins
                """
            )
        );

        PackageAssert.Matches(Assert.Single(packages), "7-Zip 24.09 (x64)", "7zip.7zip", "24.09");
    }

    [Fact]
    public void ParseInstalledPackagesMergesKoreanIdHeaderWhenEveryIdIsShort()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                이름       장치 ID 버전
                ------------------------
                Vim Editor Vim     9.1.0
                cURL       cURL    8.5.0
                """
            )
        );

        Assert.Equal(2, packages.Count);
        PackageAssert.Matches(packages[0], "Vim Editor", "Vim", "9.1.0");
        PackageAssert.Matches(packages[1], "cURL", "cURL", "8.5.0");
        Assert.Same(manager.LocalPcSource, packages[0].Source);
    }

    [Fact]
    public void ParseInstalledPackagesMergesKoreanIdHeaderWhenAnIdAlignsWithTheContinuation()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                이름               장치 ID                    버전
                -----------------------------------------------------
                Dell SupportAssist Dell App                   3.14.1
                Visual Studio Code Microsoft.VisualStudioCode 1.136.1
                Git                Git.Git                    2.51.0
                """
            )
        );

        Assert.Equal(3, packages.Count);
        PackageAssert.Matches(packages[0], "Dell SupportAssist", "Dell App", "3.14.1");
        PackageAssert.Matches(
            packages[1],
            "Visual Studio Code",
            "Microsoft.VisualStudioCode",
            "1.136.1"
        );
        PackageAssert.Matches(packages[2], "Git", "Git.Git", "2.51.0");
    }

    [Fact]
    public void ParseInstalledPackagesIgnoresOutputWithoutATable()
    {
        var manager = new WinGet();

        IReadOnlyList<Package> packages = WinGetCliHelper.ParseInstalledPackages(
            manager,
            Lines(
                """
                インストールされたパッケージが見つかりませんでした。
                """
            )
        );

        Assert.Empty(packages);
    }

    [Theory]
    [InlineData("-------------------------", true)]
    [InlineData("   ------   ", true)]
    [InlineData("--", false)]
    [InlineData("7-Zip 24.09 (x64) 7zip.7zip 24.09", false)]
    [InlineData("", false)]
    public void IsSeparatorLineOnlyAcceptsDashRuns(string line, bool expected)
    {
        Assert.Equal(expected, WinGetTableLayout.IsSeparatorLine(line));
    }
}
#endif
