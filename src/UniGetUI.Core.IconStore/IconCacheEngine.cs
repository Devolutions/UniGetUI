using System.Collections.ObjectModel;
using System.Security.Cryptography;
using PhotoSauce.MagicScaler;
using UniGetUI.Core.Classes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Core.IconEngine
{
    public enum IconValidationMethod
    {
        SHA256,
        FileSize,
        PackageVersion,
        UriSource,
    }

    public readonly struct CacheableIcon
    {
        public readonly Uri Url;
        public readonly byte[] SHA256 = [];
        public readonly string Version = "";
        public readonly long Size = -1;
        private readonly int _hashCode = -1;
        public readonly bool IsLocalPath = false;
        public readonly string LocalPath = "";
        public readonly IconValidationMethod ValidationMethod;

        /// <summary>
        /// Build a cacheable icon with SHA256 verification
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="Sha256"></param>
        public CacheableIcon(Uri uri, byte[] Sha256)
        {
            Url = uri;
            this.SHA256 = Sha256;
            ValidationMethod = IconValidationMethod.SHA256;
            _hashCode =
                uri.ToString().GetHashCode() + Sha256[0] + Sha256[1] + Sha256[2] + Sha256[3];
        }

        /// <summary>
        /// Build a cacheable icon with Version verification
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="version"></param>
        public CacheableIcon(Uri uri, string version)
        {
            Url = uri;
            Version = version;
            ValidationMethod = IconValidationMethod.PackageVersion;
            _hashCode = uri.ToString().GetHashCode() + version.GetHashCode();
        }

        /// <summary>
        /// Build a cacheable icon with Size verification
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="size"></param>
        public CacheableIcon(Uri uri, long size)
        {
            Url = uri;
            Size = size;
            ValidationMethod = IconValidationMethod.FileSize;
            _hashCode = uri.ToString().GetHashCode() + (int)size;
        }

        /// <summary>
        /// Build a cacheable icon with Uri verification
        /// </summary>
        /// <param name="uri"></param>
        public CacheableIcon(Uri uri)
        {
            Url = uri;
            ValidationMethod = IconValidationMethod.UriSource;
            _hashCode = uri.ToString().GetHashCode();
        }

        public CacheableIcon(string path)
        {
            IsLocalPath = true;
            LocalPath = path;
            Url = new Uri(path);
        }

        public override int GetHashCode()
        {
            return _hashCode;
        }
    }

    public static class IconCacheEngine
    {
        /// <summary>
        /// Returns the path to the icon file, downloading it if necessary
        /// </summary>
        /// <param name="icon">a CacheableIcon object representing the object</param>
        /// <param name="ManagerName">The name of the PackageManager</param>
        /// <param name="PackageId">the Id of the package</param>
        /// <param name="cacheInterval">the Time to store the icons on the TaskRecycler cache</param>
        /// <returns>A path to a local icon file</returns>
        public static string? GetCacheOrDownloadIcon(
            CacheableIcon? icon,
            string ManagerName,
            string PackageId,
            int cacheInterval = 30
        ) =>
            TaskRecycler<string?>.RunOrAttach(
                _getCacheOrDownloadIcon,
                icon,
                ManagerName,
                PackageId,
                cacheInterval
            );

        private static string? _getCacheOrDownloadIcon(
            CacheableIcon? _icon,
            string ManagerName,
            string PackageId
        )
        {
            if (_icon is null)
                return null;

            var icon = _icon.Value;

            if (icon.IsLocalPath)
                return icon.LocalPath;

            string iconLocation = Path.Join(
                CoreData.UniGetUICacheDirectory_Icons,
                ManagerName,
                PackageId
            );
            if (!Directory.Exists(iconLocation))
                Directory.CreateDirectory(iconLocation);
            string iconVersionFile = Path.Join(iconLocation, $"icon.version");
            string iconUriFile = Path.Join(iconLocation, $"icon.uri");

            // Get a local cache, if any
            string? cachedIconFile = GetLocalCachedFile(icon, iconLocation);

            bool isLocalCacheValid = // Verify if the cached icon exists and is valid
                cachedIconFile is not null
                && icon.ValidationMethod switch
                {
                    IconValidationMethod.FileSize => ValidateByImageSize(icon, cachedIconFile),
                    IconValidationMethod.SHA256 => ValidateBySHA256(icon, cachedIconFile),
                    IconValidationMethod.PackageVersion => ValidateByVersion(icon, iconVersionFile),
                    IconValidationMethod.UriSource => ValidateByUri(icon, iconUriFile),
                    _ => throw new InvalidDataException("Invalid icon validation method"),
                };

            // If a valid cache was found, return that cache
            if (isLocalCacheValid)
            {
                Logger.Debug(
                    $"Cached icon for id={PackageId} is valid and won't be downloaded again ({icon.ValidationMethod})"
                );
                return cachedIconFile;
            }

            if (cachedIconFile is not null)
                Logger.ImportantInfo(
                    $"Cached icon for id={PackageId} is INVALID ({icon.ValidationMethod})"
                );

            return SaveIconToCacheAndGetPath(icon, iconLocation);
        }

        private static string? GetLocalCachedFile(CacheableIcon icon, string iconLocation)
        {
            if (!Directory.Exists(iconLocation))
                return null; // The directory does not exist

            string iconFileMime = Path.Join(iconLocation, $"icon.mime");
            if (!File.Exists(iconFileMime))
                return null; // If there is no mimetype for the saved icon

            if (!MimeToExtension.TryGetValue(File.ReadAllText(iconFileMime), out string? extension))
                return null; // If the saved mimetype is not valid

            string cachedIconFile = Path.Join(iconLocation, $"icon.{extension}");
            if (!File.Exists(cachedIconFile))
                return null; // If there is no saved file cache

            string iconVersionFile = Path.Join(iconLocation, $"icon.version");
            string iconUriFile = Path.Join(iconLocation, $"icon.uri");
            if (
                icon.ValidationMethod is IconValidationMethod.PackageVersion
                && !File.Exists(iconVersionFile)
            )
                return null; // If version file does not exist and icon is versioned

            if (
                icon.ValidationMethod is IconValidationMethod.UriSource
                && !File.Exists(iconUriFile)
            )
                return null; // If uri file does not exist and icon is versioned

            return cachedIconFile;
        }

        private static string? SaveIconToCacheAndGetPath(CacheableIcon icon, string iconLocation)
        {
            try
            {
                string iconVersionFile = Path.Join(iconLocation, $"icon.version");
                string iconUriFile = Path.Join(iconLocation, $"icon.uri");

                // If the cache is determined to NOT be valid, delete cache
                DeteteCachedFiles(iconLocation);

                // After discarding the cache, regenerate it
                using HttpClient client = new(CoreTools.GenericHttpClientParameters);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);

                using var request = new HttpRequestMessage(HttpMethod.Get, icon.Url);
                using HttpResponseMessage response = client.Send(request);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warn(
                        $"Icon download attempt at {icon.Url} failed with code {response.StatusCode}"
                    );
                    return null;
                }

                string mimeType = response.Content.Headers.GetValues("Content-Type").First();
                if (!MimeToExtension.TryGetValue(mimeType, out string? extension))
                {
                    Logger.Warn(
                        $"Unknown mimetype {mimeType} for icon {icon.Url}, aborting download"
                    );
                    return null;
                }

                string cachedIconFile = Path.Join(iconLocation, $"icon.{extension}");
                string iconFileMime = Path.Join(iconLocation, $"icon.mime");
                File.WriteAllText(iconFileMime, mimeType);

                using (Stream stream = response.Content.ReadAsStream())
                using (FileStream fileStream = File.Create(cachedIconFile))
                {
                    stream.CopyTo(fileStream);
                }

                if (icon.ValidationMethod is IconValidationMethod.PackageVersion)
                    File.WriteAllText(iconVersionFile, icon.Version);

                if (icon.ValidationMethod is IconValidationMethod.UriSource)
                    File.WriteAllText(iconUriFile, icon.Url.ToString());

                // Ensure the new icon has been properly downloaded
                bool isNewCacheValid = icon.ValidationMethod switch
                {
                    IconValidationMethod.FileSize => ValidateByImageSize(icon, cachedIconFile),
                    IconValidationMethod.SHA256 => ValidateBySHA256(icon, cachedIconFile),
                    IconValidationMethod.PackageVersion => true, // The validation result would be always true
                    IconValidationMethod.UriSource => true, // The validation result would be always true
                    _ => throw new InvalidDataException("Invalid icon validation method"),
                };

                if (isNewCacheValid)
                {
                    if (
                        icon.ValidationMethod
                            is IconValidationMethod.PackageVersion
                                or IconValidationMethod.UriSource
                        && new[] { "png", "webp", "tif", "avif" }.Contains(extension)
                    )
                    {
                        DownsizeImage(cachedIconFile);
                    }

                    Logger.Debug(
                        $"Icon for Location={iconLocation} has been downloaded and verified properly (if applicable) ({icon.ValidationMethod})"
                    );
                    return cachedIconFile;
                }

                Logger.Warn(
                    $"NEWLY DOWNLOADED Icon for Location={iconLocation} Uri={icon.Url} is NOT VALID and will be discarded (verification method is {icon.ValidationMethod})"
                );
                DeteteCachedFiles(iconLocation);
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return null;
            }
        }

        /// <summary>
        /// The given image will be downsized to the expected size of an icon, if required
        /// </summary>
        private static void DownsizeImage(string cachedIconFile)
        {
            try
            {
                const int MAX_SIDE = 192;
                ImageFileInfo imageInfo = ImageFileInfo.Load(cachedIconFile);
                ImageFileInfo.FrameInfo frameInfo = imageInfo.Frames[0];
                int width = frameInfo.Width;
                int height = frameInfo.Height;

                // Calculate target size for the icon to be at max 192x192.
                if (width > MAX_SIDE || height > MAX_SIDE)
                {
                    File.Move(cachedIconFile, $"{cachedIconFile}.copy");
                    using (
                        var image = MagicImageProcessor.BuildPipeline(
                            $"{cachedIconFile}.copy",
                            new ProcessImageSettings
                            {
                                Width = MAX_SIDE,
                                Height = MAX_SIDE,
                                ResizeMode = CropScaleMode.Contain,
                            }
                        )
                    )
                    {
                        // Apply changes and save the image to disk
                        using (FileStream fileStream = File.Create(cachedIconFile))
                        {
                            image.WriteOutput(fileStream);
                        }
                        Logger.Debug(
                            $"File {cachedIconFile} was downsized from {width}x{height} to {image.Settings.Width}x{image.Settings.Height}"
                        );
                    }
                    File.Delete($"{cachedIconFile}.copy");
                }
                else
                {
                    Logger.Debug(
                        $"File {cachedIconFile} had an already appropiate size of {width}x{height}"
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"An error occurred while downsizing the image file {cachedIconFile}");
                Logger.Error(ex);
            }
        }

        /// <summary>
        /// Checks whether a cached image is valid or not depending on the size (in bytes) of the image
        /// </summary>
        private static bool ValidateByImageSize(CacheableIcon icon, string cachedIconPath)
        {
            try
            {
                FileInfo fileInfo = new FileInfo(cachedIconPath);
                return icon.Size == fileInfo.Length;
            }
            catch (Exception e)
            {
                Logger.Warn(
                    $"Failed to verify icon file size for {icon.Url} via FileSize with error {e.Message}"
                );
                return false;
            }
        }

        /// <summary>
        /// Checks whether a cached image is valid or not depending on its SHA256 hash
        /// </summary>
        private static bool ValidateBySHA256(CacheableIcon icon, string cachedIconPath)
        {
            try
            {
                using FileStream stream = File.OpenRead(cachedIconPath);
                using SHA256 sha256 = SHA256.Create();
                return (sha256.ComputeHash(stream)).SequenceEqual(icon.SHA256);
            }
            catch (Exception e)
            {
                Logger.Warn(
                    $"Failed to verify icon file size for {icon.Url} via Sha256 with error {e.Message}"
                );
                return false;
            }
        }

        /// <summary>
        /// Checks whether a cached image is valid or not depending on the package version it was pulled from
        /// </summary>
        private static bool ValidateByVersion(CacheableIcon icon, string versionPath)
        {
            try
            {
                return File.Exists(versionPath)
                    && CoreTools.VersionStringToStruct(File.ReadAllText(versionPath))
                        >= CoreTools.VersionStringToStruct(icon.Version);
            }
            catch (Exception e)
            {
                Logger.Warn(
                    $"Failed to verify icon file size for {icon.Url} via PackageVersion with error {e.Message}"
                );
                return false;
            }
        }

        /// <summary>
        /// Checks whether a cached image is valid or not depending on the URI it was pulled from
        /// </summary>
        private static bool ValidateByUri(CacheableIcon icon, string uriPath)
        {
            try
            {
                return File.Exists(uriPath) && File.ReadAllText(uriPath) == icon.Url.ToString();
            }
            catch (Exception e)
            {
                Logger.Warn(
                    $"Failed to verify icon file size for {icon.Url} via UriSource with error {e.Message}"
                );
                return false;
            }
        }

        /// <summary>
        /// Deletes all the cache files for a [icon] directory
        /// </summary>
        private static void DeteteCachedFiles(string iconLocation)
        {
            try
            {
                foreach (string file in Directory.GetFiles(iconLocation))
                {
                    File.Delete(file);
                }
            }
            catch (Exception e)
            {
                Logger.Warn($"An error occurred while deleting old icon cache: {e.Message}");
            }
        }

        public static readonly ReadOnlyDictionary<string, string> MimeToExtension =
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>
                {
                    { "image/avif", "avif" },
                    { "image/gif", "gif" },
                    // {"image/bmp", "bmp"}, Should non-transparent types be allowed?
                    // {"image/jpeg", "jpg"},
                    { "image/png", "png" },
                    { "image/webp", "webp" },
                    { "image/svg+xml", "svg" },
                    { "image/vnd.microsoft.icon", "ico" },
                    { "application/octet-stream", "ico" },
                    { "image/image/x-icon", "ico" },
                    { "image/tiff", "tif" },
                }
            );

        public static readonly ReadOnlyDictionary<string, string> ExtensionToMime =
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>
                {
                    { "avif", "image/avif" },
                    { "gif", "image/gif" },
                    // {"bmp", "image/bmp"}, Should non-transparent types be allowed
                    // {"jpg", "image/jpeg"},
                    { "png", "image/png" },
                    { "webp", "image/webp" },
                    { "svg", "image/svg+xml" },
                    { "ico", "image/image/x-icon" },
                    { "tif", "image/tiff" },
                }
            );
    }
}
