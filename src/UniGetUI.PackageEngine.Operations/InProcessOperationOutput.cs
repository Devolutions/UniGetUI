using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Operations
{
    /// <summary>
    /// Routes the output of an operation that a manager performs inside UniGetUI to the operation's
    /// log, as the standard output and error streams of a process would be.
    /// </summary>
    internal sealed class InProcessOperationOutput(
        Action<string, AbstractOperation.LineType> writeLine,
        AbstractOperation.OperationMetadata metadata
    ) : IOperationOutput
    {
        public void Info(string line) => writeLine(line, AbstractOperation.LineType.Information);

        public void Error(string line) => writeLine(line, AbstractOperation.LineType.Error);

        public void Verbose(string line) => writeLine(line, AbstractOperation.LineType.VerboseDetails);

        public void SetFailureMessage(string message) => metadata.FailureMessage = message;
    }
}
