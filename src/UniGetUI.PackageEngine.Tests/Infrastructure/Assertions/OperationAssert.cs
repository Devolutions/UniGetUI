using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;

public static class OperationAssert
{
    public static void HasVeredict(OperationVeredict actual, OperationVeredict expected)
    {
        Assert.Equal(expected, actual);
    }

    public static void HasParameters(IReadOnlyList<string> actual, params string[] expected)
    {
        Assert.Equal(expected, actual);
    }
}
