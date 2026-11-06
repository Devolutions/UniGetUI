using System.Collections.Concurrent;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Classes.Manager
{
    public class SourceFactory : ISourceFactory
    {
        private readonly IPackageManager __manager;
        private readonly ConcurrentDictionary<string, IManagerSource> __reference;
        private readonly Uri __default_uri = new("https://devolutions.net/unigetui");

        public SourceFactory(IPackageManager manager)
        {
            __reference = [];
            __manager = manager;
        }

        public bool IsPlaceholder(IManagerSource source) => source.Url == __default_uri;

        public void ReplaceSources(IEnumerable<IManagerSource> sources)
        {
            IManagerSource[] loaded = sources.ToArray();
            HashSet<string> loadedNames = loaded.Select(source => source.Name).ToHashSet();

            foreach (KeyValuePair<string, IManagerSource> entry in __reference)
            {
                if (!loadedNames.Contains(entry.Key) && !IsPlaceholder(entry.Value))
                {
                    __reference.TryRemove(entry);
                }
            }

            foreach (IManagerSource source in loaded)
            {
                IManagerSource existing_source = __reference.GetOrAdd(source.Name, source);
                if (!ReferenceEquals(existing_source, source))
                {
                    RepairPlaceholder(existing_source, source);
                    __reference[source.Name] = source;
                }
            }
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

            return __reference.GetOrAdd(name, new ManagerSource(__manager, name, __default_uri));
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
            IManagerSource existing_source = __reference.GetOrAdd(source.Name, source);
            if (!ReferenceEquals(existing_source, source))
            {
                RepairPlaceholder(existing_source, source);
            }
        }

        public IManagerSource[] GetAvailableSources()
        {
            return __reference.Values.ToArray();
        }

        private void RepairPlaceholder(IManagerSource existing_source, IManagerSource source)
        {
            if (IsPlaceholder(existing_source) && !IsPlaceholder(source))
            {
                existing_source.ReplaceUrl(source.Url);
            }
        }
    }
}
