namespace UniGetUI.PackageEngine.Interfaces.ManagerProviders
{
    /// <summary>
    /// Receives the output of an operation that a package manager performs inside UniGetUI, in place
    /// of the standard output and error streams of a process.
    /// </summary>
    public interface IOperationOutput
    {
        /// <summary>
        /// Reports a line of regular output.
        /// </summary>
        public void Info(string line);

        /// <summary>
        /// Reports a line of error output.
        /// </summary>
        public void Error(string line);

        /// <summary>
        /// Reports a diagnostic line that is only shown in the detailed operation log.
        /// </summary>
        public void Verbose(string line);

        /// <summary>
        /// Replaces the message shown to the user when the operation fails.
        /// </summary>
        public void SetFailureMessage(string message);
    }
}
