namespace UniGetUI.PackageEngine.Interfaces
{
    public interface ISourceFactory
    {
        /// <summary>
        /// Returns the existing source for the given name, or creates a new one if it does not exist.
        /// </summary>
        /// <param name="name">The name of the source</param>
        /// <returns>A valid ManagerSource</returns>
        public IManagerSource GetSourceOrDefault(string name);

        /// <summary>
        /// Returns the existing source for the given name, or null if it does not exist.
        /// </summary>
        public IManagerSource? GetSourceIfExists(string name);

        /// <summary>
        /// Adds a source to the factory, if the given source does not exist
        /// </summary>
        public void AddSource(IManagerSource source);

        /// <summary>
        /// Returns the available sources on the factory
        /// </summary>
        public IManagerSource[] GetAvailableSources();

        /// <summary>
        /// Resets the current state of the SourceFactory. All sources are lost
        /// </summary>
        public void Reset();
    }
}
