using System.Text.Json.Nodes;
using Xunit.Sdk;

namespace UniGetUI.PackageEngine.Serializable.Tests;

public class TestInstallOptions
{
    [Theory]
    [InlineData(false, false, "", "", "", "", "", "", false, false, false, "")]
    [InlineData(
        true,
        true,
        "testval",
        "testval",
        "testval",
        "testval",
        "testval",
        "testval",
        true,
        true,
        true,
        "testval"
    )]
    [InlineData(
        true,
        false,
        "true",
        "helloWorld",
        "testval",
        "heheheheeheh",
        "--parse\n-int",
        "12",
        false,
        true,
        false,
        "4.4.0-beta2"
    )]
    public void ToAndFromJsonNode(
        bool a,
        bool b,
        string c,
        string d,
        string e,
        string f,
        string g,
        string h,
        bool i,
        bool j,
        bool k,
        string l
    )
    {
        var originalObject1 = new InstallOptions()
        {
            SkipHashCheck = a,
            Architecture = h,
            CustomInstallLocation = c,
            CustomParameters_Install = [d, e, f],
            CustomParameters_Update = [d, e, f, f, f, d],
            CustomParameters_Uninstall = [e, f, f],
            InstallationScope = g,
            InteractiveInstallation = b,
            PreRelease = i,
            RunAsAdministrator = j,
            SkipMinorUpdates = k,
            Version = l,
        };

        Assert.Equal(a, originalObject1.DiffersFromDefault());

        var object2 = new InstallOptions();
        string contents = originalObject1.AsJsonString();
        Assert.NotEmpty(contents);
        var jsonContent = JsonNode.Parse(contents);
        Assert.NotNull(jsonContent);
        object2.LoadFromJson(jsonContent);
        AssertAreEqual(originalObject1, object2);

        var object3 = new InstallOptions(originalObject1.AsJsonNode());
        AssertAreEqual(originalObject1, object3);

        var object4 = originalObject1.Copy();
        AssertAreEqual(originalObject1, object4);
    }

    [Theory]
    [InlineData("{}", false, false, "", "", "", "", "", "", false, false, false, "", false)]
    [InlineData(
        """
            {
              "SkipHashCheck": true,
              "InteractiveInstallation": true,
              "RunAsAdministrator": false,
              "Architecture": "lol",
              "InstallationScope": "",
              "CustomParameters": [
                "a"
              ]
            }
            """,
        true,
        true,
        "",
        "a",
        "",
        "",
        "",
        "lol",
        false,
        false,
        false,
        "",
        true
    )]
    [InlineData(
        """
            {
              "PreRelease": false,
              "CustomInstallLocation": "",
              "Version": "heyheyhey",
              "SkipMinorUpdates": true,
              "UNKNOWN_VAL1": true,
              "UNKNOWN_VAL2": null,
              "UNKNOWN_VAL3": 22,
              "UNKNOWN_VAL4": "hehe"
            }
            """,
        false,
        false,
        "",
        "",
        "",
        "",
        "",
        "",
        false,
        false,
        true,
        "heyheyhey",
        true
    )]
    public void FromJson(
        string JSON,
        bool hash,
        bool inter,
        string installLoc,
        string arg1,
        string arg2,
        string arg3,
        string scope,
        string arch,
        bool pre,
        bool admin,
        bool skipMin,
        string ver,
        bool mod
    )
    {
        Assert.NotEmpty(JSON);
        var jsonContent = JsonNode.Parse(JSON);
        Assert.NotNull(jsonContent);
        var o2 = new InstallOptions(jsonContent);

        var list = new List<string>() { arg1, arg2, arg3 }.Where(x => x.Any());

        Assert.Equal(mod, o2.OverridesNextLevelOpts);
        Assert.Equal(mod, o2.DiffersFromDefault());
        Assert.Equal(hash, o2.SkipHashCheck);
        Assert.Equal(arch, o2.Architecture);
        Assert.Equal(installLoc, o2.CustomInstallLocation);
        Assert.Equal(
            list.Where(x => x.Any()).ToList(),
            o2.CustomParameters_Install.Where(x => x.Any()).ToList()
        );
        Assert.Equal(
            list.Where(x => x.Any()).ToList(),
            o2.CustomParameters_Update.Where(x => x.Any()).ToList()
        );
        Assert.Equal(
            list.Where(x => x.Any()).ToList(),
            o2.CustomParameters_Uninstall.Where(x => x.Any()).ToList()
        );
        Assert.Equal(scope, o2.InstallationScope);
        Assert.Equal(inter, o2.InteractiveInstallation);
        Assert.Equal(pre, o2.PreRelease);
        Assert.Equal(admin, o2.RunAsAdministrator);
        Assert.Equal(skipMin, o2.SkipMinorUpdates);
        Assert.Equal(ver, o2.Version);
    }

    [Fact]
    public void SkipMajorUpdatesRoundTripsAndDefaultsToOff()
    {
        Assert.False(new InstallOptions().SkipMajorUpdates);
        Assert.False(new InstallOptions(JsonNode.Parse("""{ "SkipMinorUpdates": true }""")!).SkipMajorUpdates);

        var options = new InstallOptions { SkipMajorUpdates = true };
        Assert.True(options.DiffersFromDefault());
        Assert.True(JsonNode.Parse(options.AsJsonString())!["SkipMajorUpdates"]!.GetValue<bool>());

        var reloaded = new InstallOptions();
        reloaded.LoadFromJson(JsonNode.Parse(options.AsJsonString())!);
        Assert.True(reloaded.SkipMajorUpdates);
        Assert.True(options.Copy().SkipMajorUpdates);
    }

    [Theory]
    [InlineData(13345)]
    [InlineData(219574)]
    [InlineData(-3453)]
    [InlineData(15820753)]
    [InlineData(9026)]
    [InlineData(12783)]
    [InlineData(87432574)]
    [InlineData(34)]
    [InlineData(86578312)]
    public void RandomPropertyAssignTester(int seed)
    {
        InstallOptions o1 = GenerateRandom(seed);
        InstallOptions o2 = o1.Copy();
        Assert.True(o2.DiffersFromDefault());
        AssertAreEqual(o1, o2);
        var c1 = o1.AsJsonString();
        var c2 = o2.AsJsonString();
        Assert.Equal(c1, c2);
        InstallOptions o3 = new();
        o3.LoadFromJson(JsonNode.Parse(c1) ?? throw new ArgumentException("null"));
        AssertAreEqual(o1, o2);
        AssertAreEqual(o2, o3);
        AssertAreEqual(o1, o3); // Yeah, it is redundant
        Assert.Equal(c1, o3.AsJsonString());
    }

    private static InstallOptions GenerateRandom(int seed)
    {
        Random r = new(seed);

        InstallOptions o1 = new();
        foreach (var (key, _) in o1._defaultBoolValues)
            o1._boolVal[key] = r.Next(2) is 0;

        o1.OverridesNextLevelOpts = r.Next(2) is 0;

        foreach (var key in o1._stringKeys)
            o1._strVal[key] = r.Next().ToString();

        foreach (var key in o1._listKeys)
        {
            var randomList = Enumerable
                .Range(0, r.Next(0, 11))
                .Select(_ => r.Next().ToString())
                .ToList();
            o1._listVal[key] = randomList;
        }

        return o1;
    }

    internal static void AssertAreEqual(InstallOptions o1, InstallOptions o2)
    {
        Assert.Equal(o1.OverridesNextLevelOpts, o2.OverridesNextLevelOpts);

        foreach (var (key, _) in o1._defaultBoolValues)
        {
            Assert.Equal(o1._boolVal[key], o2._boolVal[key]);
        }

        foreach (var key in o1._stringKeys)
        {
            Assert.Equal(o1._strVal[key], o2._strVal[key]);
        }

        foreach (var key in o1._listKeys)
        {
            Assert.Equal(
                o1._listVal[key].Where(x => x.Any()),
                o2._listVal[key].Where(x => x.Any())
            );
        }
    }
}
