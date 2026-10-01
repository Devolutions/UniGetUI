using UniGetUI.Core.Tools;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class OperationNotificationText
{
    public static string SuccessTitle(AbstractOperation operation)
        => operation.Metadata.SuccessTitle.Length > 0
            ? operation.Metadata.SuccessTitle
            : CoreTools.Translate("Success!");

    public static string SuccessMessage(AbstractOperation operation)
    {
        string message = operation.Metadata.SuccessMessage.Length > 0
            ? operation.Metadata.SuccessMessage
            : CoreTools.Translate("Success!");

        if (!operation.SystemRestartRequired) return message;

        message = message.TrimEnd();
        if (message.Length > 0 && !".!?:;。！？".Contains(message[^1]))
            message += ".";

        return message
            + " "
            + CoreTools.Translate(
                "This operation finished, but the computer must be restarted before the changes take effect");
    }
}
