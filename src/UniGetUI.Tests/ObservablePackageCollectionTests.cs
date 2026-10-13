using Avalonia.Collections;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.Tests;

public sealed class ObservablePackageCollectionTests
{
    private sealed class Row(int key, string name)
    {
        public int Key { get; } = key;
        public string Name { get; } = name;
    }

    private static readonly IComparer<Row> ByKey = Comparer<Row>.Create((a, b) => a.Key.CompareTo(b.Key));

    [Fact]
    public void ApplyChanges_RemovesOnlyTheGivenInstance()
    {
        var first = new Row(1, "first");
        var twin = new Row(1, "twin");
        var list = new AvaloniaList<Row> { first, twin, new Row(2, "other") };

        ObservablePackageCollection.ApplyChanges(list, [twin], [], ByKey);

        Assert.Equal(["first", "other"], list.Select(r => r.Name));
    }

    [Fact]
    public void ApplyChanges_IgnoresRemovalsThatAreNotListed()
    {
        var list = new AvaloniaList<Row> { new Row(1, "a"), new Row(2, "b") };

        ObservablePackageCollection.ApplyChanges(list, [new Row(1, "a")], [], ByKey);

        Assert.Equal(["a", "b"], list.Select(r => r.Name));
    }

    [Fact]
    public void ApplyChanges_InsertsAfterEqualKeys()
    {
        var list = new AvaloniaList<Row> { new Row(1, "a"), new Row(2, "b1"), new Row(2, "b2"), new Row(3, "c") };

        ObservablePackageCollection.ApplyChanges(list, [], [new Row(2, "new"), new Row(0, "head"), new Row(9, "tail")], ByKey);

        Assert.Equal(["head", "a", "b1", "b2", "new", "c", "tail"], list.Select(r => r.Name));
    }

    [Fact]
    public void ApplyChanges_RaisesOneNotificationPerChangeAndNoReset()
    {
        var removed = new Row(2, "b");
        var list = new AvaloniaList<Row> { new Row(1, "a"), removed, new Row(3, "c") };
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => actions.Add(e.Action);

        ObservablePackageCollection.ApplyChanges(list, [removed], [new Row(2, "b2")], ByKey);

        Assert.Equal(
            [System.Collections.Specialized.NotifyCollectionChangedAction.Remove, System.Collections.Specialized.NotifyCollectionChangedAction.Add],
            actions);
    }

    [Fact]
    public void ApplyChanges_MatchesAFullStableSort()
    {
        var random = new Random(5505);
        for (int iteration = 0; iteration < 500; iteration++)
        {
            var source = Enumerable.Range(0, random.Next(0, 40)).Select(i => new Row(random.Next(0, 8), $"s{i}")).ToList();
            var list = new AvaloniaList<Row>(source.OrderBy(r => r, ByKey));

            var removed = source.Where(_ => random.Next(4) == 0).ToList();
            var added = Enumerable.Range(0, random.Next(0, 6)).Select(i => new Row(random.Next(0, 8), $"n{i}")).ToList();

            ObservablePackageCollection.ApplyChanges(list, removed, added, ByKey);

            var expected = source.Except(removed).Concat(added).OrderBy(r => r, ByKey);
            Assert.Equal(expected.Select(r => r.Name), list.Select(r => r.Name));
        }
    }
}
