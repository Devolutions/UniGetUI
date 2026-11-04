using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.Tools;

public static partial class TokenElevation
{
    private const int TokenElevationTypeClass = 18;
    private const int TokenElevationTypeLimited = 3;

    public static bool CanElevateAsCurrentAccount()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            return GetElevationType() == TokenElevationTypeLimited;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read the elevation type of the current token: {ex.Message}");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int GetElevationType()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (
            !GetTokenInformation(
                identity.AccessToken.DangerousGetHandle(),
                TokenElevationTypeClass,
                out int elevationType,
                sizeof(int),
                out _
            )
        )
            throw new InvalidOperationException(
                $"GetTokenInformation failed with error {Marshal.GetLastPInvokeError()}"
            );

        return elevationType;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength
    );
}
