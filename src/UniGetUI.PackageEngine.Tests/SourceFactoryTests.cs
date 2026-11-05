using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

public sealed class SourceFactoryTests
{
    private const string ExtrasUrl = "https://github.com/ScoopInstaller/Extras";

    [Fact]
    public void ReloadingSourcesRepairsAPlaceholderHandedOutBeforeTheBucketWasKnown()
    {
        var (manager, helper) = CreateManager();

        IManagerSource placeholder = helper.Factory.GetSourceOrDefault("extras");
        Assert.True(helper.Factory.IsPlaceholder(placeholder));

        helper.SetSources([Extras(manager)]);
        helper._getSources();

        Assert.Equal(new Uri(ExtrasUrl), placeholder.Url);
        Assert.False(helper.Factory.IsPlaceholder(placeholder));
    }

    [Fact]
    public void PlaceholderSurvivesAReloadThatDoesNotListItsSource()
    {
        var (manager, helper) = CreateManager();

        IManagerSource placeholder = helper.Factory.GetSourceOrDefault("extras");
        helper._getSources();

        Assert.Same(placeholder, helper.Factory.GetSourceOrDefault("extras"));

        helper.SetSources([Extras(manager)]);
        helper._getSources();

        Assert.Equal(new Uri(ExtrasUrl), placeholder.Url);
    }

    [Fact]
    public void ReloadRepairsEveryPackageHoldingAPlaceholderAcrossSeveralReloads()
    {
        var (manager, helper) = CreateManager();

        IManagerSource first = helper.Factory.GetSourceOrDefault("extras");
        helper._getSources();
        IManagerSource second = helper.Factory.GetSourceOrDefault("extras");
        helper._getSources();

        helper.SetSources([Extras(manager)]);
        helper._getSources();

        Assert.Equal(new Uri(ExtrasUrl), first.Url);
        Assert.Equal(new Uri(ExtrasUrl), second.Url);
    }

    [Fact]
    public void ReloadDropsARealSourceThatIsNoLongerListed()
    {
        var (manager, helper) = CreateManager();
        helper.SetSources([Extras(manager)]);
        helper._getSources();
        Assert.NotNull(helper.Factory.GetSourceIfExists("extras"));

        helper.SetSources([]);
        helper._getSources();

        Assert.Null(helper.Factory.GetSourceIfExists("extras"));
    }

    [Fact]
    public void KnownSourceIsReturnedWithoutCreatingAPlaceholder()
    {
        var manager = new PackageManagerBuilder()
            .WithSources(manager =>
            [
                new SourceBuilder()
                    .WithManager(manager)
                    .WithName("main")
                    .WithUrl("https://github.com/ScoopInstaller/Main")
                    .Build(),
            ])
            .Build();

        IManagerSource source = manager.SourcesHelper.Factory.GetSourceOrDefault("main");

        Assert.Equal(new Uri("https://github.com/ScoopInstaller/Main"), source.Url);
        Assert.False(manager.SourcesHelper.Factory.IsPlaceholder(source));
    }

    private static (TestPackageManager Manager, TestSourceHelper Helper) CreateManager()
    {
        var manager = new PackageManagerBuilder().Build();
        return (manager, (TestSourceHelper)manager.SourcesHelper);
    }

    private static IManagerSource Extras(IPackageManager manager) =>
        new SourceBuilder().WithManager(manager).WithName("extras").WithUrl(ExtrasUrl).Build();
}
