namespace UniGetUI.Core.Data
{
    public static class ContributorsData
    {
        public static string[] Contributors = BundledAssets.ReadAllLines("Data/Contributors.list")
            .Where(x => x != "")
            .ToArray();
    }
}
