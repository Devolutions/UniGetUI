namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal static class NativeWinGetCollection
{
    internal static T[] Copy<T>(IReadOnlyList<T> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        T[] copy = new T[collection.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = collection[index];
        }

        return copy;
    }
}
