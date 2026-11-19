using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class HeldBackUpdateNoticeTests
{
    [Fact]
    public void ReturnsNothingForAnUpdateThatIsNotHeldBack()
    {
        var package = new PackageBuilder().WithVersion("1.0.0").WithNewVersion("2.0.0").Build();

        Assert.Null(HeldBackUpdateNotice.Build(package));
    }

    [Fact]
    public void AnUpdateThatNeverAppliedNamesBothVersionsAndTheAttempts()
    {
        var package = new PackageBuilder()
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .HeldBack(HeldBackUpdateReason.NeverApplied, attempts: 4)
            .Build();

        string? notice = HeldBackUpdateNotice.Build(package);

        Assert.NotNull(notice);
        Assert.Contains("1.0.0", notice);
        Assert.Contains("2.0.0", notice);
        Assert.Contains("4", notice);
        Assert.Contains(package.Manager.DisplayName, notice);
        Assert.DoesNotContain("{", notice);
    }

    [Fact]
    public void EachReasonExplainsItselfDifferently()
    {
        string?[] notices = Enum.GetValues<HeldBackUpdateReason>()
            .Select(reason =>
                HeldBackUpdateNotice.Build(
                    new PackageBuilder().WithVersion("1.0.0").WithNewVersion("2.0.0").HeldBack(reason).Build()
                )
            )
            .ToArray();

        Assert.All(notices, notice => Assert.Contains("2.0.0", notice!));
        Assert.Equal(notices.Length, notices.Distinct().Count());
    }
}
