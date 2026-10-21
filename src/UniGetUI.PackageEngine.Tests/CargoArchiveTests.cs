using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using UniGetUI.PackageEngine.Managers.CargoManager;

namespace UniGetUI.PackageEngine.Tests;

public sealed class CargoArchiveTests
{
    [Theory]
    [InlineData("zip")]
    [InlineData("tar")]
    [InlineData("tgz")]
    public void ReadReturnsOnlySelectedBytesAndNormalizesOrdinaryDotPrefix(string format)
    {
        (string, byte[])[] entries = [("./release/tool", [3, 4, 5]), ("release/README", [9])];
        byte[] bytes = Build(format, entries);
        var files = CargoArchive.Read(bytes, format, path => path == "release/tool", default);
        Assert.Equal("release/tool", Assert.Single(files).Key);
        Assert.Equal([3, 4, 5], files["release/tool"]);
    }

    [Theory]
    [InlineData("../tool")]
    [InlineData("release/../../tool")]
    [InlineData("/absolute/tool")]
    [InlineData("C:/drive/tool")]
    [InlineData("release\\tool")]
    [InlineData("release//tool")]
    [InlineData("release/tool.")]
    [InlineData("release/tool ")]
    [InlineData("release/CON")]
    [InlineData("release/NUL.exe")]
    public void ReadRejectsUnsafePathsEvenForUnselectedEntries(string name)
    {
        foreach (string format in new[] { "zip", "tgz" })
        {
            byte[] bytes = Build(format, (name, [1]));
            Assert.Throws<InvalidDataException>(() =>
                CargoArchive.Read(bytes, format, _ => false, default));
        }
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("tgz")]
    public void ReadRejectsCaseInsensitiveAndNormalizedDuplicatePaths(string format)
    {
        Assert.Throws<InvalidDataException>(() => CargoArchive.Read(
            Build(format, ("release/tool", [1]), ("release/TOOL", [2])), format, _ => true, default));
        Assert.Throws<InvalidDataException>(() => CargoArchive.Read(
            Build(format, ("release/tool", [1]), ("./release/tool", [2])), format, _ => false, default));
    }

    [Fact]
    public void ReadRejectsZipSymlinksAndTarLinks()
    {
        using var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("tool");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using Stream content = entry.Open();
            content.WriteByte(1);
        }
        Assert.Throws<InvalidDataException>(() =>
            CargoArchive.Read(zipBytes.ToArray(), "zip", _ => false, default));
        foreach (TarEntryType type in new[] { TarEntryType.SymbolicLink, TarEntryType.HardLink })
        {
            using var tarBytes = new MemoryStream();
            using (var writer = new TarWriter(tarBytes, leaveOpen: true))
                writer.WriteEntry(new PaxTarEntry(type, "tool") { LinkName = "../outside" });
            Assert.Throws<InvalidDataException>(() =>
                CargoArchive.Read(tarBytes.ToArray(), "tar", _ => false, default));
        }
    }

    [Fact]
    public void ReadEnforcesEntryLimitAndDeclaredExpandedLimitWithoutLargeAllocation()
    {
        var entries = Enumerable.Range(0, CargoArchive.EntryLimit)
            .Select(i => ("entry-" + i, Array.Empty<byte>())).ToArray();
        Assert.Empty(CargoArchive.Read(CargoTestData.Zip(entries), "zip", _ => false, default));
        Assert.Contains("too many entries", Assert.Throws<InvalidDataException>(() => CargoArchive.Read(
            CargoTestData.Zip([.. entries, ("one-too-many", [])]), "zip", _ => false, default)).Message);
        byte[] oversized = CargoTestData.Zip(("tool", [1]));
        SetZipDeclaredSize(oversized, (uint)CargoArchive.ExpandedLimit + 1);
        Assert.Contains("allowed size", Assert.Throws<InvalidDataException>(() =>
            CargoArchive.Read(oversized, "zip", _ => false, default)).Message);
    }

    [Fact]
    public void ReadRejectsTruncatedPayloadAndHonorsCancellation()
    {
        byte[] truncated = CargoTestData.Zip(("tool", [1, 2, 3]));
        SetZipDeclaredSize(truncated, 4);
        Assert.Throws<InvalidDataException>(() => CargoArchive.Read(truncated, "zip", _ => true, default));
        using var tarBytes = new MemoryStream();
        using (var writer = new TarWriter(tarBytes, leaveOpen: true))
        using (var content = new MemoryStream(new byte[1024]))
            writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "tool") { DataStream = content });
        Assert.Throws<InvalidDataException>(() => CargoArchive.Read(tarBytes.ToArray()[..600], "tar", _ => true, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            CargoArchive.Read(CargoTestData.Zip(("tool", [1])), "zip", _ => true, cancellation.Token));
        Assert.Throws<NotSupportedException>(() => CargoArchive.Read([], "xz", _ => true, default));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("tgz")]
    public void SelectedEntryLimitAllowsExactBoundaryAndRejectsNextByte(string format)
    {
        byte[] bytes = Build(format, ("tool", [1, 2, 3]), ("unselected", [4, 5, 6, 7]));
        var files = CargoArchive.Read(bytes, format, n => n == "tool", default, selectedEntryLimit: 3);
        Assert.Equal([1, 2, 3], files["tool"]);
        Assert.Single(files);
        Assert.Throws<InvalidDataException>(() =>
            CargoArchive.Read(bytes, format, _ => true, default, selectedEntryLimit: 3));
        Assert.Throws<InvalidDataException>(() =>
            CargoArchive.Read(bytes, format, n => n == "tool", default, selectedEntryLimit: 2));
    }

    [Fact]
    public void CumulativeExpandedSizeRejectsCompressedBombWithoutRetainingPayload()
    {
        // Reuse one 4 MiB buffer; both ZIPs remain small. The unselected entries
        // must still be drained and counted, without retaining 512 MiB in memory.
        byte[] content = new byte[4 * 1024 * 1024];
        int count = (int)(CargoArchive.ExpandedLimit / content.Length);
        var entries = Enumerable.Range(0, count).Select(i => ("entry-" + i, content)).ToArray();
        Assert.Empty(CargoArchive.Read(CargoTestData.Zip(entries), "zip", _ => false, default));
        Assert.Contains("allowed size", Assert.Throws<InvalidDataException>(() =>
            CargoArchive.Read(CargoTestData.Zip([.. entries, ("over-limit", content)]),
                "zip", _ => false, default)).Message);
    }

    [Theory]
    [InlineData("tool.ZIP", "zip")]
    [InlineData("tool.tar.gz", "tgz")]
    [InlineData("tool.tgz", "tgz")]
    [InlineData("tool.tar", "tar")]
    [InlineData("tool.tar.xz", null)]
    [InlineData("tool.exe", null)]
    public void FormatRecognizesOnlySupportedArchiveSuffixes(string name, string? expected) =>
        Assert.Equal(expected, CargoArchive.Format(name));

    private static byte[] Build(string format, params (string Name, byte[] Bytes)[] entries) =>
        format == "zip" ? CargoTestData.Zip(entries) : CargoTestData.Tar(format == "tgz", entries);

    private static void SetZipDeclaredSize(byte[] bytes, uint size)
    {
        for (int i = 0; i <= bytes.Length - 28; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x02014B50)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), size);
                return;
            }
        throw new InvalidOperationException("Fixture ZIP central directory was not found.");
    }
}
