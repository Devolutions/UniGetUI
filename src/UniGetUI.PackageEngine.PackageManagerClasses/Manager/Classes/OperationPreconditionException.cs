namespace UniGetUI.PackageEngine.Classes.Manager;

public sealed class OperationPreconditionException : Exception
{
    public OperationPreconditionException(string message)
        : base(message) { }
}
