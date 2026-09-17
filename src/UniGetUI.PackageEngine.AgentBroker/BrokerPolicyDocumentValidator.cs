using System.Text.Json;
using Devolutions.Now.Policy.Model;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.AgentBroker;

internal static class BrokerPolicyDocumentValidator
{
    public static bool HasRequiredData(PolicyDocument? policy)
    {
        if (policy?.Metadata is null
            || !IsResourceId(policy.Metadata.Id)
            || !IsRequiredString(policy.Metadata.Publisher, 128)
            || policy.Rules is null
            || policy.Rules.Count > 1024)
        {
            return false;
        }

        foreach (PolicyRule? rule in policy.Rules)
        {
            if (rule is null
                || !IsResourceId(rule.Id)
                || rule.Priority > int.MaxValue)
            {
                return false;
            }
        }

        try
        {
            _ = PolicySerializer.Serialize(policy);
            return true;
        }
        catch (JsonException ex)
        {
            Logger.Warn($"[AgentBroker] Policy failed shared contract validation: {ex.Message}");
            return false;
        }
    }

    private static bool IsRequiredString(string? value, int maxLength)
    {
        if (value is null)
        {
            return false;
        }

        int length = value.EnumerateRunes().Take(maxLength + 1).Count();
        return length is > 0 && length <= maxLength;
    }

    private static bool IsResourceId(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > 128
            || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.AsSpan(1).ContainsAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._:-") is false;
    }
}
