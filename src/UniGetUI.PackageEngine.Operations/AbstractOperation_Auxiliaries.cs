namespace UniGetUI.PackageOperations;

public abstract partial class AbstractOperation
{
    public static readonly List<AbstractOperation> OperationQueue = [];
    public static int MAX_OPERATIONS;

    /// <summary>
    /// Raised on the thread that completed the last operation in <see cref="OperationQueue"/>.
    /// Subscribers should not do heavy work here — fire-and-forget async is fine.
    /// </summary>
    public static event EventHandler<EventArgs>? QueueDrained;

    public static class RetryMode
    {
        public const string NoRetry = "";
        public const string Retry = "Retry";
        public const string Retry_AsAdmin = "RetryAsAdmin";
        public const string Retry_Interactive = "RetryInteractive";
        public const string Retry_SkipIntegrity = "RetryNoHashCheck";
    }

    public class OperationMetadata
    {
        /// <summary>
        /// Installation of X
        /// </summary>
        public string Title = "";

        /// <summary>
        /// X is being installed/upated/removed
        /// </summary>
        public string Status = "";

        /// <summary>
        /// X was installed
        /// </summary>
        public string SuccessTitle = "";

        /// <summary>
        /// X has been installed successfully
        /// </summary>
        public string SuccessMessage = "";

        /// <summary>
        /// X could not be installed.
        /// </summary>
        public string FailureTitle = "";

        /// <summary>
        /// X Could not be installed
        /// </summary>
        public string FailureMessage = "";

        /// <summary>
        /// Starting operation X with options Y
        /// </summary>
        public string OperationInformation = "";

        public readonly string Identifier;

        public OperationMetadata()
        {
            Identifier = new Random().NextInt64(1000000, 9999999).ToString();
        }
    }

    public class BadgeCollection
    {
        public readonly bool AsAdministrator;
        public readonly bool Interactive;
        public readonly bool SkipHashCheck;
        public readonly string? Scope;

        public BadgeCollection(bool admin, bool interactive, bool skiphash, string? scope)
        {
            AsAdministrator = admin;
            Interactive = interactive;
            SkipHashCheck = skiphash;
            Scope = scope;
        }
    }

    public enum LineType
    {
        VerboseDetails,
        ProgressIndicator,
        Information,
        Error,
    }

    public struct InnerOperation
    {
        public readonly AbstractOperation Operation;
        public readonly bool MustSucceed;

        public InnerOperation(AbstractOperation op, bool mustSucceed)
        {
            Operation = op;
            MustSucceed = mustSucceed;
            op.IsInnerOperation = true;
        }
    }
}
