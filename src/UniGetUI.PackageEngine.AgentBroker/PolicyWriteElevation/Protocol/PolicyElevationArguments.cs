using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>
/// The complete command line contract of the elevated helper.
/// </summary>
/// <remarks>
/// Every value here is routing information that is already visible to any local observer
/// (a pipe name, process ids, a session id). No draft, store token, validation receipt,
/// credential or other secret is ever placed on the command line, and no temporary file is
/// used to hand data over: the authenticated pipe is the only data channel.
/// </remarks>
public sealed record PolicyElevationLaunchArguments(
    string ProtocolVersion,
    string PipeName,
    int ParentProcessId,
    long ParentCreationTimeUtcTicks,
    uint SessionId)
{
    /// <summary>Renders the arguments in the exact order the helper expects.</summary>
    public string Format()
    {
        if (!IsValid(this, out string? error))
        {
            throw new ArgumentException(error, nameof(ProtocolVersion));
        }

        return string.Join(
            ' ',
            PolicyElevationProtocol.ProtocolArgument,
            ProtocolVersion,
            PolicyElevationProtocol.PipeArgument,
            PipeName,
            PolicyElevationProtocol.ParentProcessIdArgument,
            ParentProcessId.ToString(CultureInfo.InvariantCulture),
            PolicyElevationProtocol.ParentCreationTimeArgument,
            ParentCreationTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
            PolicyElevationProtocol.SessionArgument,
            SessionId.ToString(CultureInfo.InvariantCulture));
    }

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        [NotNullWhen(true)] out PolicyElevationLaunchArguments? parsed,
        [NotNullWhen(false)] out string? error)
    {
        parsed = null;
        error = null;

        if (arguments is null || arguments.Count is not 10)
        {
            error = "The elevated policy helper expects exactly five named arguments.";
            return false;
        }

        string? protocolVersion = null;
        string? pipeName = null;
        int? parentProcessId = null;
        long? parentCreationTime = null;
        uint? sessionId = null;

        for (int i = 0; i < arguments.Count; i += 2)
        {
            string name = arguments[i];
            string value = arguments[i + 1];

            switch (name)
            {
                case PolicyElevationProtocol.ProtocolArgument when protocolVersion is null:
                    protocolVersion = value;
                    break;

                case PolicyElevationProtocol.PipeArgument when pipeName is null:
                    pipeName = value;
                    break;

                case PolicyElevationProtocol.ParentProcessIdArgument when parentProcessId is null:
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                    {
                        error = "The parent process id argument was not a positive integer.";
                        return false;
                    }

                    parentProcessId = pid;
                    break;

                case PolicyElevationProtocol.ParentCreationTimeArgument when parentCreationTime is null:
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long created))
                    {
                        error = "The parent creation time argument was not a positive integer.";
                        return false;
                    }

                    parentCreationTime = created;
                    break;

                case PolicyElevationProtocol.SessionArgument when sessionId is null:
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint session))
                    {
                        error = "The session argument was not a non-negative integer.";
                        return false;
                    }

                    sessionId = session;
                    break;

                default:
                    error = $"Unexpected or duplicated argument '{name}'.";
                    return false;
            }
        }

        if (protocolVersion is null
            || pipeName is null
            || parentProcessId is null
            || parentCreationTime is null
            || sessionId is null)
        {
            error = "The elevated policy helper is missing one or more required arguments.";
            return false;
        }

        var candidate = new PolicyElevationLaunchArguments(
            protocolVersion,
            pipeName,
            parentProcessId.Value,
            parentCreationTime.Value,
            sessionId.Value);

        if (!IsValid(candidate, out error))
        {
            return false;
        }

        parsed = candidate;
        return true;
    }

    public static bool IsValidPipeName(string? pipeName)
    {
        if (pipeName is null
            || pipeName.Length !=
                PolicyElevationProtocol.PipeNamePrefix.Length + PolicyElevationProtocol.PipeNameEntropyCharacters
            || !pipeName.StartsWith(PolicyElevationProtocol.PipeNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in pipeName.AsSpan(PolicyElevationProtocol.PipeNamePrefix.Length))
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValid(
        PolicyElevationLaunchArguments candidate,
        [NotNullWhen(false)] out string? error)
    {
        if (!string.Equals(candidate.ProtocolVersion, PolicyElevationProtocol.Version, StringComparison.Ordinal))
        {
            error = "The elevated policy helper was invoked with an unsupported protocol version.";
            return false;
        }

        if (!IsValidPipeName(candidate.PipeName))
        {
            error = "The elevated policy helper was invoked with a malformed pipe name.";
            return false;
        }

        if (candidate.ParentProcessId <= 0)
        {
            error = "The elevated policy helper was invoked with an invalid parent process id.";
            return false;
        }

        if (candidate.ParentCreationTimeUtcTicks <= 0)
        {
            error = "The elevated policy helper was invoked with an invalid parent creation time.";
            return false;
        }

        error = null;
        return true;
    }
}
