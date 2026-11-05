using System.Collections.Concurrent;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Classes.Manager
{
    public class SourceFactory : ISourceFactory
    {
        private readonly IPackageManager __manager;
        private readonly ConcurrentDictionary<string, IManagerSource> __reference;
        private readonly ConcurrentDictionary<string, IManagerSource> __placeholders;
        private readonly Uri __default_uri = new("https://devolutions.net/unigetui");

        public SourceFactory(IPackageManager manager)
        {
            __reference = [];
            __placeholders = [];
            __manager = manager;
        }

        public bool IsPlaceholder(IManagerSource source) => source.Url == __default_uri;

        public void Reset()
        {
            __reference.Clear();
        }

        /// <summary>
        /// Returns the existing source for the given name, or creates a new one if it does not exist.
        /// </summary>
        /// <param name="name">The name of the source</param>
        /// <returns>A valid ManagerSource</returns>
        public IManagerSource GetSourceOrDefault(string name)
        {
            if (__reference.TryGetValue(name, out IManagerSource? source))
            {
                return source;
            }

            ManagerSource new_source = new(__manager, name, __default_uri);
            IManagerSource registered = __reference.GetOrAdd(name, new_source);
            if (ReferenceEquals(registered, new_source))
            {
                __placeholders.TryAdd(name, new_source);
            }

            return registered;
        }

        /// <summary>
        /// Returns the existing source for the given name, or null if it does not exist.
        /// </summary>
        public IManagerSource? GetSourceIfExists(string name)
        {
            if (__reference.TryGetValue(name, out IManagerSource? source))
            {
                return source;
            }

            return null;
        }

        public void AddSource(IManagerSource source)
        {
            if (
                source.Url != __default_uri
                && __placeholders.TryRemove(source.Name, out IManagerSource? placeholder)
                && placeholder.Url == __default_uri
            )
            {
                placeholder.ReplaceUrl(source.Url);
            }

            if (!__reference.TryAdd(source.Name, source))
            {
                IManagerSource existing_source = __reference[source.Name];
                if (existing_source.Url == __default_uri)
                {
                    existing_source.ReplaceUrl(source.Url);
                }
            }
        }

        public IManagerSource[] GetAvailableSources()
        {
            return __reference.Values.ToArray();
        }
    }
}
