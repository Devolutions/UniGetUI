using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using UniGetUI.PackageEngine.Managers.CargoManager;

namespace UniGetUI.PackageEngine.Tests;

internal sealed class CargoTestSandbox : IDisposable
{
    internal string Root { get; } = Path.Join(Path.GetTempPath(), "UniGetUI-CargoTests", Guid.NewGuid().ToString("N"));

    internal CargoTestSandbox() => Directory.CreateDirectory(Root);

    internal string PathFor(string name) => Path.Join(Root, name.Replace('/', Path.DirectorySeparatorChar));

    internal void Write(string name, byte[] bytes)
    {
        string path = PathFor(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    internal void WriteText(string name, string text) => Write(name, Encoding.UTF8.GetBytes(text));

    internal IDisposable IsolateEnvironment() => new CargoTestEnvironment(
        ("CARGO_HOME", PathFor("home")), ("CARGO_INSTALL_ROOT", null), ("CARGO_BUILD_TARGET", null),
        ("RUSTFLAGS", null), ("CARGO_ENCODED_RUSTFLAGS", null));

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

internal sealed class CargoTestEnvironment : IDisposable
{
    private readonly (string Name, string? Value)[] _previous;

    internal CargoTestEnvironment(params (string Name, string? Value)[] variables)
    {
        _previous = variables.Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name))).ToArray();
        foreach (var (name, value) in variables)
            Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
            Environment.SetEnvironmentVariable(name, value);
    }
}

internal sealed class CargoTestHttpFixture : IDisposable
{
    private readonly Func<HttpMessageHandler>? _previous = CargoHttp.TestHandlerFactory;
    internal Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);
    internal List<Uri> Requests { get; } = [];

    internal CargoTestHttpFixture() => CargoHttp.TestHandlerFactory = () => new Handler(this);

    internal void Bytes(string url, byte[] bytes) =>
        Routes[url] = () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    internal void Text(string url, string text) => Bytes(url, Encoding.UTF8.GetBytes(text));

    internal void Status(string url, HttpStatusCode status) => Routes[url] = () => new(status);

    internal void Redirect(string url, string location)
    {
        Routes[url] = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        };
    }

    public void Dispose() => CargoHttp.TestHandlerFactory = _previous;

    private sealed class Handler(CargoTestHttpFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri uri = request.RequestUri!;
            Assert.Equal("https", uri.Scheme);
            Assert.Equal(HttpMethod.Get, request.Method);
            fixture.Requests.Add(uri);
            if (!fixture.Routes.TryGetValue(uri.AbsoluteUri, out var respond))
                throw new InvalidOperationException("Unexpected fake Cargo request: " + uri);
            return Task.FromResult(respond());
        }
    }
}

internal static class CargoTestData
{
    internal const string Id = "fixture-tool";
    internal const string Version = "1.2.3";
    internal const string Target = "x86_64-pc-windows-msvc";
    internal const string SourceUrl = "https://crates.io/api/v1/crates/fixture-tool/1.2.3/download";
    internal const string BinaryUrl = "https://publisher.test/fixture-tool/1.2.3/fixture.zip";

    internal static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static CargoBinaryPlan Plan(string version = Version, string target = Target,
        string[]? binaries = null, string format = "zip", string? binaryPath = null) =>
        new(Id, version, target, binaries ?? ["tool"], new Uri(BinaryUrl), format, Hash([]), binaryPath);

    internal static CargoManifest Manifest(params CargoManifestVersion[] versions) => new()
    {
        crate = new()
        {
            name = Id,
            max_stable_version = Version,
            max_version = Version,
            newest_version = Version,
        },
        versions = versions,
    };

    internal static CargoManifestVersion VersionInfo(string version = Version, string checksum = "",
        bool yanked = false, string[]? binaries = null) => new()
        {
            num = version,
            checksum = checksum,
            dl_path = "/source-download",
            yanked = yanked,
            bin_names = binaries ?? ["tool"],
        };

    internal static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using Stream stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        return output.ToArray();
    }

    internal static byte[] Tar(bool gzip, params (string Name, byte[] Bytes)[] entries)
    {
        using var output = new MemoryStream();
        using (var tar = new TarWriter(output, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var content = new MemoryStream(bytes);
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = content });
            }
        if (!gzip)
            return output.ToArray();
        using var compressed = new MemoryStream();
        using (var zip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zip.Write(output.ToArray());
        return compressed.ToArray();
    }

    // Minimal inert headers: these payloads are validated/staged, never launched.
    internal static byte[] Executable(string target = Target)
    {
        byte[] bytes = new byte[128];
        bool arm = target.StartsWith("aarch64-", StringComparison.Ordinal);
        if (target.Contains("-windows-", StringComparison.Ordinal))
        {
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), 64);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), 0x4550);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), (ushort)(arm ? 0xAA64 : 0x8664));
        }
        else if (target.Contains("-linux-", StringComparison.Ordinal))
        {
            bytes[0] = 0x7F;
            bytes[1] = (byte)'E';
            bytes[2] = (byte)'L';
            bytes[3] = (byte)'F';
            bytes[4] = 2;
            bytes[5] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), (ushort)(arm ? 183 : 62));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xFEEDFACF);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), arm ? 0x100000CU : 0x1000007U);
        }
        return bytes;
    }
}
