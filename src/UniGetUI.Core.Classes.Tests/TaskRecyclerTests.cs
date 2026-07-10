using System.Diagnostics.CodeAnalysis;

namespace UniGetUI.Core.Classes.Tests;

public class TaskRecyclerTests
{
    private int MySlowMethod1()
    {
        Thread.Sleep(1000);
        return new Random().Next();
    }

    private sealed class TestClass
    {
        public TestClass() { }

        [SuppressMessage(
            "Performance",
            "CA1822:Mark members as static",
            Justification = "Instance methods are required to validate TaskRecycler instance-bound delegate behavior."
        )]
        public string SlowMethod2()
        {
            Thread.Sleep(1000);
            return new Random().Next().ToString();
        }

        [SuppressMessage(
            "Performance",
            "CA1822:Mark members as static",
            Justification = "Instance methods are required to validate TaskRecycler instance-bound delegate behavior."
        )]
        public string SlowMethod3()
        {
            Thread.Sleep(1000);
            return new Random().Next().ToString();
        }
    }

    private int MySlowMethod4(int argument)
    {
        Thread.Sleep(1000);
        return new Random().Next() + (argument - argument);
    }

    [Fact]
    public async Task TestTaskRecycler_Static_Int()
    {
        // The same static method should be cached, and therefore the return value should be the same
        var task1 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod1);
        var task2 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod1);
        int result1 = await task1;
        int result2 = await task2;
        Assert.Equal(result1, result2);

        // The same static method should be cached, and therefore the return value should be the same, but different from previous runs
        var task3 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod1);
        var task4 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod1);
        int result4 = await task4;
        int result3 = await task3;
        Assert.Equal(result3, result4);

        // Ensure the last call was not permanently cached
        Assert.NotEqual(result1, result3);
    }

    [Fact]
    public async Task TestTaskRecycler_Static_Int_WithCache()
    {
        // The same static method should be cached, and therefore the return value should be the same
        var task1 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod1, 2);
        var task2 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod1, 2);
        int result1 = await task1;
        int result2 = await task2;
        Assert.Equal(result1, result2);

        // The same static method should be cached, and therefore the return value should be the same,
        // and equal to previous runs due to 3 seconds cache
        var task3 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod1, 2);
        var task4 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod1, 2);
        int result4 = await task4;
        int result3 = await task3;
        Assert.Equal(result3, result4);
        Assert.Equal(result1, result3);

        // Wait for caches to clear
        Thread.Sleep(3000);

        // The same static method should be cached, but cached runs should have been removed. This results should differ from previous ones
        var task5 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod1, 2);
        var task6 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod1, 2);
        int result5 = await task6;
        int result6 = await task5;
        Assert.Equal(result5, result6);
        Assert.NotEqual(result4, result5);

        // Clear cache
        TaskRecycler<int>.RemoveFromCache(MySlowMethod1);

        // The same static method should be cached, but cached runs should have been cleared manually. This results should differ from previous ones
        var task7 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod1, 2);
        var task8 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod1, 2);
        int result7 = await task7;
        int result8 = await task8;
        Assert.Equal(result7, result8);
        Assert.NotEqual(result6, result7);
    }

    [Fact]
    public async Task TestTaskRecycler_StaticWithArgument_Int()
    {
        // The same static method should be cached, and therefore the return value should be the same
        var task1 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod4, 2);
        var task2 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod4, 2);
        var task3 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod4, 3);
        int result1 = await task1;
        int result2 = await task2;
        int result3 = await task3;
        Assert.Equal(result1, result2);
        Assert.NotEqual(result1, result3);

        // The same static method should be cached, and therefore the return value should be the same, but different from previous runs
        var task4 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod4, 2);
        var task5 = TaskRecycler<int>.RunOrAttachAsync(MySlowMethod4, 3);
        int result4 = await task4;
        int result5 = await task5;
        Assert.NotEqual(result4, result5);
        Assert.NotEqual(result1, result4);
        Assert.NotEqual(result3, result5);
    }

    [Fact]
    public async Task FaultedTaskIsEvictedBeforeTheNextCall()
    {
        int calls = 0;
        Func<int> method = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("Expected first attempt failure");
            return 42;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TaskRecycler<int>.RunOrAttachAsync(method)
        );

        int result = await TaskRecycler<int>.RunOrAttachAsync(method);

        Assert.Equal(42, result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TestTaskRecycler_Class_String()
    {
        var class1 = new TestClass();
        var class2 = new TestClass();

        // The SAME method from the SAME instance should be cached,
        // and therefore the return value should be the same
        var task1 = TaskRecycler<string>.RunOrAttachAsync(class1.SlowMethod2);
        var task2 = TaskRecycler<string>.RunOrAttachAsync(class1.SlowMethod2);
        string result1 = await task1;
        string result2 = await task2;
        Assert.Equal(result1, result2);

        var class1_copy = class1;

        // The SAME method from the SAME instance, even when called
        // from different variable names should be cached, and therefore the return value should be the same
        var task5 = TaskRecycler<string>.RunOrAttachAsync(class1_copy.SlowMethod2);
        var task6 = TaskRecycler<string>.RunOrAttachAsync(class1.SlowMethod2);
        string result5 = await task5;
        string result6 = await task6;
        Assert.Equal(result5, result6);

        // The SAME method from two DIFFERENT instances should NOT be
        // cached, so the results should differ
        var task3 = TaskRecycler<string>.RunOrAttachAsync(class1.SlowMethod2);
        var task4 = TaskRecycler<string>.RunOrAttachAsync(class2.SlowMethod2);
        string result4 = await task4;
        string result3 = await task3;
        Assert.NotEqual(result3, result4);

        // Ensure the last call was not permanently cached
        Assert.NotEqual(result1, result3);

        // The SAME method from two DIFFERENT instances should NOT be
        // cached, so the results should differ
        var task7 = TaskRecycler<string>.RunOrAttachAsync(class1.SlowMethod3);
        var task8 = TaskRecycler<string>.RunOrAttachAsync(class2.SlowMethod2);
        string result7 = await task7;
        string result8 = await task8;
        Assert.NotEqual(result7, result8);
    }
}
