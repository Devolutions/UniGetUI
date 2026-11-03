using System.Collections.Concurrent;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Operations;

public enum InstallerHashStatus
{
    Verified,
    Mismatch,
    MismatchKept,
    NotPublished,
    Unreadable,
}

public sealed record InstallerVerification(
    InstallerHashStatus HashStatus,
    string? HashAlgorithm,
    SignatureCheck? Signature
)
{
    internal const int Capacity = 256;
    private static readonly ConcurrentDictionary<string, InstallerVerification> _results = new();
    private static readonly ConcurrentQueue<string> _recordOrder = new();

    public static InstallerVerification? Find(Uri? installerUrl, string? installerHash) =>
        installerUrl is not null && _results.TryGetValue(Key(installerUrl, installerHash), out var result)
            ? result
            : null;

    public string Describe()
    {
        string hash = HashStatus switch
        {
            InstallerHashStatus.Verified => CoreTools.Translate("Hash verified ({0})", HashAlgorithm ?? ""),
            InstallerHashStatus.Mismatch => CoreTools.Translate("Hash mismatch, the download was discarded"),
            InstallerHashStatus.MismatchKept => CoreTools.Translate("Hash mismatch, kept because the hash check was skipped"),
            InstallerHashStatus.NotPublished => CoreTools.Translate("No hash published, not verified"),
            _ => CoreTools.Translate("Unrecognized hash format, not verified"),
        };

        string? signature = Signature switch
        {
            null => null,
            { IsValid: true } valid => CoreTools.Translate(
                "Signed by {0}",
                valid.Signer ?? CoreTools.Translate("an unknown publisher")
            ),
            { IsUnsignedFileType: true } => null,
            { Status: unchecked((int)0x800B0100) } => CoreTools.Translate("Not signed"),
            { } untrusted => CoreTools.Translate("Signature not trusted ({0})", $"0x{untrusted.Status:X8}"),
        };

        return signature is null ? hash : $"{hash} · {signature}";
    }

    internal static void Record(Uri installerUrl, string? installerHash, InstallerVerification result)
    {
        string key = Key(installerUrl, installerHash);
        if (_results.TryAdd(key, result))
            _recordOrder.Enqueue(key);
        else
            _results[key] = result;

        while (_results.Count > Capacity && _recordOrder.TryDequeue(out string? oldest))
            _results.TryRemove(oldest, out _);
    }

    private static string Key(Uri installerUrl, string? installerHash) =>
        installerUrl.AbsoluteUri + "\n" + (installerHash ?? "").Trim();
}
