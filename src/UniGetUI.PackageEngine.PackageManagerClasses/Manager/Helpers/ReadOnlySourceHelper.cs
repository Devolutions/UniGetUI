using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Classes.Manager.Providers
{
    public abstract class ReadOnlySourceHelper : BaseSourceHelper
    {
        private readonly string _reason;

        protected ReadOnlySourceHelper(IPackageManager manager, string reason)
            : base(manager)
        {
            _reason = reason;
        }

        public sealed override string[] GetAddSourceParameters(IManagerSource source) =>
            throw new NotSupportedException(_reason);

        public sealed override string[] GetRemoveSourceParameters(IManagerSource source) =>
            throw new NotSupportedException(_reason);

        protected sealed override OperationVeredict _getAddSourceOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        ) => OperationVeredict.Failure;

        protected sealed override OperationVeredict _getRemoveSourceOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        ) => OperationVeredict.Failure;
    }
}
