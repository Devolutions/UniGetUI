using UniGetUI.Interface.Enums;

namespace UniGetUI.PackageEngine.Interfaces
{
    public interface IManagerSource
    {
        public IconType IconId { get; }
        public bool IsVirtualManager { get; }
        public IPackageManager Manager { get; }
        public string Name { get; }
        public Uri Url { get; protected set; }
        public int? PackageCount { get; }
        public string? UpdateDate { get; }

        public string AsString { get; }
        public string AsString_DisplayName { get; }

        /// <summary>
        /// Returns a human-readable string representing the source name
        /// </summary>
        public string ToString();

        /// <summary>
        /// Replaces the current URL with the new one. Must be used only when a placeholder URL is used.
        /// </summary>
        void ReplaceUrl(Uri newUrl)
        {
            Url = newUrl;
        }

        void RefreshSourceNames();
    }
}
