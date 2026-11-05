using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

public sealed class SourceFactoryTests
{
    [Fact]
    public void ReloadingSourcesRepairsAPlaceholderHandedOutBeforeTheBucketWasKnown()
    {
        var manager = new PackageManagerBuilder().Build();
        var helper = (TestSourceHelper)manager.SourcesHelper;

        IManagerSource placeholder = helper.Factory.GetSourceOrDefault("extras");
        Assert.True(helper.Factory.IsPlaceholder(placeholder));

        helper.SetSources(
            [
                new SourceBuilder()
                    .WithManager(manager)
                    .WithName("extras")
                    .WithUrl("https://github.com/ScoopInstaller/Extras")
                    .Build(),
            ]
        );
        helper._getSources();

        Assert.Equal(new Uri("https://github.com/ScoopInstaller/Extras"), placeholder.Url);
        Assert.False(helper.Factory.IsPlaceholder(placeholder));
    }

    [Fact]
    public void PlaceholderSurvivesAReloadThatDoesNotListItsSource()
    {
        var manager = new PackageManagerBuilder().Build();
        var helper = (TestSourceHelper)manager.SourcesHelper;

        IManagerSource placeholder = helper.Factory.GetSourceOrDefault("extras");
        helper._getSources();

        helper.SetSources(
            [
                new SourceBuilder()
                    .WithManager(manager)
                    .WithName("extras")
                    .WithUrl("https://github.com/ScoopInstaller/Extras")
                    .Build(),
            ]
        );
        helper._getSources();

        Assert.Equal(new Uri("https://github.com/ScoopInstaller/Extras"), placeholder.Url);
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
}
