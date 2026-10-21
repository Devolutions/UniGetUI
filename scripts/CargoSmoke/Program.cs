using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UniGetUI.PackageEngine.Managers.CargoManager;

string root = Path.Join(Path.GetTempPath(), "unigetui-cargo-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Directory.CreateDirectory(Path.Join(root, "cargo-home"));
try
{
    string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
    string target = architecture + (OperatingSystem.IsWindows() ? "-pc-windows-msvc"
        : OperatingSystem.IsMacOS() ? "-apple-darwin" : "-unknown-linux-gnu");
    byte[] executable = new byte[128];
    if (OperatingSystem.IsWindows())
    {
        executable[0] = (byte)'M';
        executable[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(executable.AsSpan(0x3C), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(executable.AsSpan(64), 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(executable.AsSpan(68), architecture == "aarch64" ? (ushort)0xAA64 : (ushort)0x8664);
    }
    else if (OperatingSystem.IsLinux())
    {
        executable[0] = 0x7F;
        executable[1] = (byte)'E';
        executable[2] = (byte)'L';
        executable[3] = (byte)'F';
        executable[4] = 2;
        executable[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(executable.AsSpan(18), architecture == "aarch64" ? (ushort)183 : (ushort)62);
    }
    else
    {
        BinaryPrimitives.WriteUInt32LittleEndian(executable, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(executable.AsSpan(4), architecture == "aarch64" ? 0x100000CU : 0x1000007U);
    }
    byte[] binaryArchive;
    using (MemoryStream output = new())
    {
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        using (Stream entry = zip.CreateEntry("smoke" + (OperatingSystem.IsWindows() ? ".exe" : "")).Open())
            entry.Write(executable);
        binaryArchive = output.ToArray();
    }
    byte[] sourceArchive;
    using (MemoryStream output = new())
    {
        using (GZipStream gzip = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (TarWriter tar = new(gzip))
        {
            var manifestEntry = new PaxTarEntry(TarEntryType.RegularFile, "unigetui-smoke/ignored");
            manifestEntry.Name = "unigetui-smoke-1.0.0/Cargo.toml";
            manifestEntry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes("""
                [package]
                name = "unigetui-smoke"
                version = "1.0.0"
                [package.metadata.binstall]
                pkg-url = "https://publisher.example/{version}/smoke.zip"
                pkg-fmt = "zip"
                """));
            tar.WriteEntry(manifestEntry);
        }
        sourceArchive = output.ToArray();
    }
    var version = new CargoManifestVersion
    {
        num = "1.0.0",
        checksum = Convert.ToHexString(SHA256.HashData(sourceArchive)),
        dl_path = "/crates/unigetui-smoke/1.0.0/download",
        bin_names = ["smoke"],
    };
    var manifest = new CargoManifest
    {
        crate = new CargoManifestCrate
        {
            name = "unigetui-smoke",
            max_stable_version = "1.0.0",
            max_version = "1.0.0",
            newest_version = "1.0.0",
        },
        versions = [version],
    };
    CargoHttp.TestHandlerFactory = () => new FixtureHandler(sourceArchive, binaryArchive);
    using CargoHttp http = new();
    CargoBinaryPlan plan = await new CargoBinaryResolver(http).ResolveAsync(
        "unigetui-smoke", "1.0.0", target, manifest, Console.WriteLine, CancellationToken.None)
        ?? throw new InvalidOperationException("Published binary resolution unexpectedly selected fallback.");
    CargoBinaryResolver.VerifyHash(binaryArchive, plan.Hash);
    var binaries = CargoManagedInstaller.ExtractBinaries(binaryArchive, plan, CancellationToken.None);
    await CargoInstallTracker.InstallAsync(root, plan, binaries, CancellationToken.None);
    string listing = await Cargo("install", "--list", "--root", root);
    if (!listing.Contains("unigetui-smoke v1.0.0:", StringComparison.Ordinal))
        throw new InvalidOperationException("Real Cargo did not recognize native installation tracking.");
    string fixture = Path.Join(root, "source");
    Directory.CreateDirectory(Path.Join(fixture, "src"));
    File.WriteAllText(Path.Join(fixture, "Cargo.toml"), """
        [package]
        name = "unigetui-smoke"
        version = "1.0.1"
        edition = "2021"
        [[bin]]
        name = "smoke"
        path = "src/main.rs"
        """);
    File.WriteAllText(Path.Join(fixture, "src", "main.rs"), """fn main() { println!("cargo-smoke"); }""");
    await Cargo("generate-lockfile", "--manifest-path", Path.Join(fixture, "Cargo.toml"), "--offline");
    await Cargo("install", "--path", fixture, "--root", root, "--target", target, "--locked", "--offline");
    if (!(await Cargo("install", "--list", "--root", root)).Contains("unigetui-smoke v1.0.1", StringComparison.Ordinal))
        throw new InvalidOperationException("Cargo could not upgrade the native-installed package from source.");
    await Cargo("uninstall", "--root", root, "unigetui-smoke");
    if ((await Cargo("install", "--list", "--root", root)).Contains("unigetui-smoke", StringComparison.Ordinal))
        throw new InvalidOperationException("Real Cargo did not remove the native-installed package.");
    Console.WriteLine("PASS: published native resolver, TOML/JSON, integrity, extraction, tracking, Cargo list/source upgrade/uninstall.");
}
finally
{
    CargoHttp.TestHandlerFactory = null;
    Directory.Delete(root, recursive: true);
}

async Task<string> Cargo(params string[] arguments)
{
    using Process process = new();
    process.StartInfo = new ProcessStartInfo("cargo")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    process.StartInfo.Environment["CARGO_HOME"] = Path.Join(root, "cargo-home");
    foreach (string argument in arguments)
        process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
        throw new InvalidOperationException("Cargo smoke operation failed: " + await stderr);
    return await stdout;
}

sealed class FixtureHandler(byte[] source, byte[] binary) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? content = request.RequestUri?.AbsoluteUri switch
        {
            "https://crates.io/api/v1/crates/unigetui-smoke/1.0.0/download" => source,
            "https://publisher.example/1.0.0/smoke.zip.sha256" =>
                Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(binary)) + "  smoke.zip"),
            _ => null,
        };
        return Task.FromResult(new HttpResponseMessage(content is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content ?? []),
        });
    }
}
