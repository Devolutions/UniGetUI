using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace UniGetUI.Extensions.Xcode.AppleAuth;

/// <summary>
/// Unencrypted summary of a stored secret item. Reading metadata does not unlock or decrypt the secret payload itself.
/// </summary>
public readonly record struct SecretMetadata(
    bool Exists,
    byte[]? Metadata,
    long? ModificationStamp = null);

/// <summary>Keeps small secrets under one service name. Implementations never log the values.</summary>
public interface ISecretStore
{
    byte[]? Read(string account);
    void Write(string account, ReadOnlySpan<byte> secret);
    void Delete(string account);

    bool SupportsDeferredMetadata => false;

    SecretMetadata ReadMetadata(string account)
        => throw new NotSupportedException();

    void Write(string account, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> metadata)
        => Write(account, secret);
}

/// <summary>
/// Generic-password items in the user's login Keychain, under a service that belongs to UniGetUI alone. Items of other
/// applications (Xcodes.app, the xcodes command) are never queried.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe partial class KeychainSecretStore : ISecretStore
{
    public const string DefaultService = "com.devolutions.unigetui.xcode";

    private const string SecurityPath = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string SecurityCliPath = "/usr/bin/security";
    private const int Success = 0;
    private const int ItemNotFound = -25300;
    private const int DuplicateItem = -25299;
    private const int InvalidOwnerEdit = -25244;
    private const uint Utf8 = 0x08000100;
    private const int MaximumSecretBytes = 1024 * 1024;
    private const int MaximumMetadataBytes = 16 * 1024;

    private static readonly Lazy<Constants> Native = new(() => new Constants());
    private readonly string _service;

    public KeychainSecretStore(string service = DefaultService)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The Keychain is only available on macOS.");
        _service = service;
    }

    public bool SupportsDeferredMetadata => true;

    public SecretMetadata ReadMetadata(string account)
    {
        using var query = new Query(_service, account);
        CFDictionarySetValue(query.Dictionary, Native.Value.ReturnAttributes, Native.Value.True);
        CFDictionarySetValue(query.Dictionary, Native.Value.MatchLimit, Native.Value.MatchLimitOne);
        int status = SecItemCopyMatching(query.Dictionary, out nint result);
        try
        {
            if (status == ItemNotFound)
                return new SecretMetadata(Exists: false, Metadata: null, ModificationStamp: null);
            Check("read", status);
            if (result == 0 || CFGetTypeID(result) != CFDictionaryGetTypeID())
                throw new KeychainException("The Keychain returned unexpected item attributes.", status);

            byte[]? metadata = null;
            nint generic = CFDictionaryGetValue(result, Native.Value.Generic);
            if (generic != 0 && CFGetTypeID(generic) == CFDataGetTypeID())
            {
                nint length = CFDataGetLength(generic);
                if (length > MaximumMetadataBytes)
                    throw new KeychainException("The Keychain item metadata is too large.", status);
                if (length > 0)
                    metadata = new ReadOnlySpan<byte>((void*)CFDataGetBytePtr(generic), (int)length).ToArray();
            }

            long? modificationStamp = null;
            nint modified = CFDictionaryGetValue(result, Native.Value.ModificationDate);
            if (modified != 0 && CFGetTypeID(modified) == CFDateGetTypeID())
                modificationStamp = BitConverter.DoubleToInt64Bits(CFDateGetAbsoluteTime(modified));

            return new SecretMetadata(Exists: true, Metadata: metadata, ModificationStamp: modificationStamp);
        }
        finally
        {
            if (result != 0)
                CFRelease(result);
        }
    }

    public byte[]? Read(string account)
    {
        using var query = new Query(_service, account);
        CFDictionarySetValue(query.Dictionary, Native.Value.ReturnData, Native.Value.True);
        CFDictionarySetValue(query.Dictionary, Native.Value.MatchLimit, Native.Value.MatchLimitOne);
        int status = SecItemCopyMatching(query.Dictionary, out nint result);
        try
        {
            if (status == ItemNotFound)
                return null;
            Check("read", status);
            if (result == 0 || CFGetTypeID(result) != CFDataGetTypeID())
                throw new KeychainException("The Keychain returned an unexpected item.", status);
            nint length = CFDataGetLength(result);
            if (length < 0 || length > MaximumSecretBytes)
                throw new KeychainException("The Keychain item is too large.", status);
            return new ReadOnlySpan<byte>((void*)CFDataGetBytePtr(result), (int)length).ToArray();
        }
        finally
        {
            if (result != 0)
                CFRelease(result);
        }
    }

    public void Write(string account, ReadOnlySpan<byte> secret)
        => Write(account, secret, default);

    public void Write(string account, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> metadata)
    {
        if (secret.Length > MaximumSecretBytes)
            throw new ArgumentException("The secret is too large for the Keychain.", nameof(secret));
        if (metadata.Length > MaximumMetadataBytes)
            throw new ArgumentException("The metadata is too large for the Keychain.", nameof(metadata));
        using var data = CreateData(secret);
        using CFObject? generic = metadata.IsEmpty ? null : CreateData(metadata);
        using (var query = new Query(_service, account))
        using (var values = new CFObject(CFDictionaryCreateMutable(0, 0, Native.Value.KeyCallbacks, Native.Value.ValueCallbacks)))
        {
            CFDictionarySetValue(values.Value, Native.Value.ValueData, data.Value);
            if (generic is not null)
                CFDictionarySetValue(values.Value, Native.Value.Generic, generic.Value);
            int status = SecItemUpdate(query.Dictionary, values.Value);
            if (status == Success)
                return;
            if (status == InvalidOwnerEdit && TryDeleteViaSecurityCli(account))
                status = ItemNotFound;
            if (status != ItemNotFound)
                Check("update", status);
        }
        using (var add = new Query(_service, account))
        {
            CFDictionarySetValue(add.Dictionary, Native.Value.ValueData, data.Value);
            if (generic is not null)
                CFDictionarySetValue(add.Dictionary, Native.Value.Generic, generic.Value);
            CFDictionarySetValue(add.Dictionary, Native.Value.Accessible, Native.Value.AccessibleAfterFirstUnlockThisDeviceOnly);
            int status = SecItemAdd(add.Dictionary, 0);
            if (status == DuplicateItem)
                throw new KeychainException("The Keychain item changed while it was being saved. Try again.", status);
            Check("add", status);
        }
    }

    public void Delete(string account)
    {
        using var query = new Query(_service, account);
        int status = SecItemDelete(query.Dictionary);
        if (status == InvalidOwnerEdit && TryDeleteViaSecurityCli(account))
            return;
        if (status != ItemNotFound)
            Check("delete", status);
    }

    private bool TryDeleteViaSecurityCli(string account)
    {
        if (!File.Exists(SecurityCliPath) || _service.Length == 0 || account.Length == 0
            || _service.Any(char.IsControl) || account.Any(char.IsControl))
            return false;
        try
        {
            var start = new ProcessStartInfo(SecurityCliPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.Environment.Clear();
            start.ArgumentList.Add("delete-generic-password");
            start.ArgumentList.Add("-s");
            start.ArgumentList.Add(_service);
            start.ArgumentList.Add("-a");
            start.ArgumentList.Add(account);
            using Process? process = Process.Start(start);
            if (process is null)
                return false;
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return false;
            }
            return process.ExitCode == 0 || ReadMetadata(account) is { Exists: false };
        }
        catch
        {
            return false;
        }
    }

    private static void Check(string operation, int status)
    {
        if (status != Success)
            throw new KeychainException($"Could not {operation} the Keychain item ({Describe(status)}).", status);
    }

    private static string Describe(int status)
    {
        nint message = SecCopyErrorMessageString(status, 0);
        if (message == 0)
            return $"OSStatus {status}";
        try
        {
            return $"{ToManagedString(message)}, OSStatus {status}";
        }
        finally
        {
            CFRelease(message);
        }
    }

    private static string ToManagedString(nint value)
    {
        nint length = CFStringGetLength(value);
        nint max = CFStringGetMaximumSizeForEncoding(length, Utf8) + 1;
        if (max <= 1 || max > 4096)
            return "";
        byte[] buffer = new byte[max];
        fixed (byte* pointer = buffer)
        {
            if (CFStringGetCString(value, pointer, max, Utf8) == 0)
                return "";
        }
        int end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private static CFObject CreateString(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        fixed (byte* pointer = bytes)
            return new CFObject(CFStringCreateWithBytes(0, pointer, bytes.Length, Utf8, 0));
    }

    private static CFObject CreateData(ReadOnlySpan<byte> value)
    {
        fixed (byte* pointer = value)
            return new CFObject(CFDataCreate(0, pointer, value.Length));
    }

    private sealed class Constants
    {
        internal Constants()
        {
            nint security = NativeLibrary.Load(SecurityPath);
            nint core = NativeLibrary.Load(CoreFoundationPath);
            Class = Read(security, "kSecClass");
            GenericPassword = Read(security, "kSecClassGenericPassword");
            Service = Read(security, "kSecAttrService");
            Account = Read(security, "kSecAttrAccount");
            Generic = Read(security, "kSecAttrGeneric");
            ModificationDate = Read(security, "kSecAttrModificationDate");
            ValueData = Read(security, "kSecValueData");
            ReturnData = Read(security, "kSecReturnData");
            ReturnAttributes = Read(security, "kSecReturnAttributes");
            MatchLimit = Read(security, "kSecMatchLimit");
            MatchLimitOne = Read(security, "kSecMatchLimitOne");
            Accessible = Read(security, "kSecAttrAccessible");
            AccessibleAfterFirstUnlockThisDeviceOnly = Read(security, "kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly");
            True = Read(core, "kCFBooleanTrue");
            KeyCallbacks = NativeLibrary.GetExport(core, "kCFTypeDictionaryKeyCallBacks");
            ValueCallbacks = NativeLibrary.GetExport(core, "kCFTypeDictionaryValueCallBacks");
        }

        internal nint Class { get; }
        internal nint GenericPassword { get; }
        internal nint Service { get; }
        internal nint Account { get; }
        internal nint Generic { get; }
        internal nint ModificationDate { get; }
        internal nint ValueData { get; }
        internal nint ReturnData { get; }
        internal nint ReturnAttributes { get; }
        internal nint MatchLimit { get; }
        internal nint MatchLimitOne { get; }
        internal nint Accessible { get; }
        internal nint AccessibleAfterFirstUnlockThisDeviceOnly { get; }
        internal nint True { get; }
        internal nint KeyCallbacks { get; }
        internal nint ValueCallbacks { get; }

        private static nint Read(nint library, string symbol)
        {
            nint value = Marshal.ReadIntPtr(NativeLibrary.GetExport(library, symbol));
            return value != 0 ? value : throw new KeychainException($"Missing Keychain constant {symbol}.", 0);
        }
    }

    private sealed class Query : IDisposable
    {
        private readonly CFObject _dictionary;
        private readonly CFObject _service;
        private readonly CFObject _account;

        internal Query(string service, string account)
        {
            // The dictionary retains what it holds, so the values may be released in any order.
            _dictionary = new CFObject(CFDictionaryCreateMutable(0, 0, Native.Value.KeyCallbacks, Native.Value.ValueCallbacks));
            _service = CreateString(service);
            _account = CreateString(account);
            CFDictionarySetValue(Dictionary, Native.Value.Class, Native.Value.GenericPassword);
            CFDictionarySetValue(Dictionary, Native.Value.Service, _service.Value);
            CFDictionarySetValue(Dictionary, Native.Value.Account, _account.Value);
        }

        internal nint Dictionary => _dictionary.Value;

        public void Dispose()
        {
            _dictionary.Dispose();
            _account.Dispose();
            _service.Dispose();
        }
    }

    private sealed class CFObject(nint value) : IDisposable
    {
        internal nint Value { get; private set; } = value != 0
            ? value : throw new KeychainException("Core Foundation could not allocate an object.", 0);

        public void Dispose()
        {
            if (Value == 0) return;
            CFRelease(Value);
            Value = 0;
        }
    }

    [LibraryImport(SecurityPath)] private static partial int SecItemAdd(nint attributes, nint result);
    [LibraryImport(SecurityPath)] private static partial int SecItemCopyMatching(nint query, out nint result);
    [LibraryImport(SecurityPath)] private static partial int SecItemUpdate(nint query, nint attributes);
    [LibraryImport(SecurityPath)] private static partial int SecItemDelete(nint query);
    [LibraryImport(SecurityPath)] private static partial nint SecCopyErrorMessageString(int status, nint reserved);

    [LibraryImport(CoreFoundationPath)] private static partial nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallbacks, nint valueCallbacks);
    [LibraryImport(CoreFoundationPath)] private static partial void CFDictionarySetValue(nint dictionary, nint key, nint value);
    [LibraryImport(CoreFoundationPath)] private static partial nint CFDictionaryGetValue(nint dictionary, nint key);
    [LibraryImport(CoreFoundationPath)] private static partial nuint CFDictionaryGetTypeID();
    [LibraryImport(CoreFoundationPath)] private static partial nint CFStringCreateWithBytes(nint allocator, byte* bytes, nint count, uint encoding, byte external);
    [LibraryImport(CoreFoundationPath)] private static partial nint CFStringGetLength(nint value);
    [LibraryImport(CoreFoundationPath)] private static partial nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);
    [LibraryImport(CoreFoundationPath)] private static partial byte CFStringGetCString(nint value, byte* buffer, nint size, uint encoding);
    [LibraryImport(CoreFoundationPath)] private static partial nint CFDataCreate(nint allocator, byte* bytes, nint count);
    [LibraryImport(CoreFoundationPath)] private static partial nint CFDataGetLength(nint data);
    [LibraryImport(CoreFoundationPath)] private static partial nint CFDataGetBytePtr(nint data);
    [LibraryImport(CoreFoundationPath)] private static partial nuint CFDataGetTypeID();
    [LibraryImport(CoreFoundationPath)] private static partial nuint CFDateGetTypeID();
    [LibraryImport(CoreFoundationPath)] private static partial double CFDateGetAbsoluteTime(nint date);
    [LibraryImport(CoreFoundationPath)] private static partial nuint CFGetTypeID(nint value);
    [LibraryImport(CoreFoundationPath)] private static partial void CFRelease(nint value);
}

public sealed class KeychainException(string message, int status) : Exception(message)
{
    public const int UserCanceledStatus = -128;
    public const int AuthFailedStatus = -25293;

    public int Status { get; } = status;
    public bool IsUserCanceledOrDenied => Status is UserCanceledStatus or AuthFailedStatus;
}
