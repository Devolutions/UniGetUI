using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class SourceFactoryTests
{
    [Fact]
    public void PlaceholderHandedOutBeforeResetReceivesRealUrlFromAddSource()
    {
        var manager = new PackageManagerBuilder().Build();
        var factory = manager.SourcesHelper.Factory;
        IManagerSource placeholder = factory.GetSourceOrDefault("main");

        factory.Reset();
        var real = new SourceBuilder()
            .WithManager(manager)
            .WithName("main")
            .WithUrl("https://github.com/ScoopInstaller/Main")
            .Build();
        factory.AddSource(real);

        Assert.Equal(real.Url, placeholder.Url);
    }

    [Fact]
    public void PlaceholderRequestedAfterResetIsTheSameInstanceAsBefore()
    {
        var manager = new PackageManagerBuilder().Build();
        var factory = manager.SourcesHelper.Factory;
        IManagerSource before = factory.GetSourceOrDefault("main");

        factory.Reset();

        Assert.Same(before, factory.GetSourceOrDefault("main"));
    }
}
