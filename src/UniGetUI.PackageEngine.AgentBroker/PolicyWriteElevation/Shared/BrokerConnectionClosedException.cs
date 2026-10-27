namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

internal sealed class BrokerConnectionClosedException(string message, Exception? innerException = null)
    : IOException(message, innerException);
