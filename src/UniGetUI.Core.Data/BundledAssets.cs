namespace UniGetUI.Core.Data
{
    /// <summary>
    /// Read-only files that ship with the application under <c>Assets\</c> (translations, translator and
    /// contributor lists). They are read from next to the executable; a host that ships as a single file
    /// (the terminal UI embeds them in its executable) supplies them through <see cref="Provider"/> instead.
    /// </summary>
    public static class BundledAssets
    {
        /// <summary>
        /// Optional source for assets that are not on disk. Receives the path under <c>Assets</c> with '/'
        /// separators (for example <c>Languages/lang_en.json</c>) and returns null when it doesn't have it.
        /// </summary>
        public static Func<string, Stream?>? Provider { get; set; }

        /// <summary>Whether the asset exists on disk or in the <see cref="Provider"/>.</summary>
        public static bool Exists(string relativePath)
        {
            if (File.Exists(DiskPath(relativePath)))
            {
                return true;
            }

            using Stream? stream = Provider?.Invoke(relativePath);
            return stream is not null;
        }

        /// <summary>Reads the asset as text: from disk when present, otherwise from the <see cref="Provider"/>.</summary>
        /// <exception cref="FileNotFoundException">The asset is neither on disk nor in the provider.</exception>
        public static string ReadAllText(string relativePath)
        {
            string path = DiskPath(relativePath);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            using Stream stream = Provider?.Invoke(relativePath)
                                  ?? throw new FileNotFoundException("Bundled asset not found", path);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>Reads the asset's lines (like <see cref="File.ReadAllLines(string)"/>).</summary>
        public static string[] ReadAllLines(string relativePath)
        {
            string[] lines = ReadAllText(relativePath).Split(["\r\n", "\n"], StringSplitOptions.None);
            return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
        }

        /// <summary>Where the asset lives on disk (next to the executable), for messages.</summary>
        public static string DiskPath(string relativePath)
            => Path.Join([CoreData.UniGetUIExecutableDirectory, "Assets", .. relativePath.Split('/')]);
    }
}
