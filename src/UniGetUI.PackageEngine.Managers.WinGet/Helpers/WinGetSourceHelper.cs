using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Managers.WingetManager
{
    internal sealed class WinGetSourceHelper : BaseSourceHelper
    {
        private readonly string[][] _sourceTypes =
        [
            ["--type", "Microsoft.PreIndexed.Package"],
            ["--type", "Microsoft.Rest"],
        ];
        private readonly Dictionary<string, int> _attemptedSourceTypes = new();

        public WinGetSourceHelper(WinGet manager)
            : base(manager) { }

        public override string[] GetAddSourceParameters(IManagerSource source)
        {
            List<string> args =
            [
                "source",
                "add",
                "--name",
                source.Name,
                "--arg",
                source.Url.ToString(),
                "--accept-source-agreements",
                "--disable-interactivity",
            ];

            if (source.Name != "winget")
            {
                int sourceIndex = _attemptedSourceTypes.GetValueOrDefault(source.Name);
                args.AddRange(_sourceTypes[sourceIndex]);
            }

            return args.ToArray();
        }

        public override string[] GetRemoveSourceParameters(IManagerSource source)
        {
            return ["source", "remove", "--name", source.Name, "--disable-interactivity"];
        }

        protected override OperationVeredict _getAddSourceOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        )
        {
            // If operation succeeded, or the source already exists
            if ((uint)ReturnCode is 0 or 0x8A15000C)
                return OperationVeredict.Success;

            // Failed? Let's guess another source type and try again
            int sourceIndex = _attemptedSourceTypes.GetValueOrDefault(source.Name);
            if (sourceIndex + 1 >= _sourceTypes.Length)
            {
                // If we have tested all available sources?
                _attemptedSourceTypes[source.Name] = 0;
                return OperationVeredict.Failure;
            }

            // Attempt another source type
            _attemptedSourceTypes[source.Name] = sourceIndex + 1;
            return OperationVeredict.AutoRetry;
        }

        protected override OperationVeredict _getRemoveSourceOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        )
        {
            return ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
        }

        protected override IReadOnlyList<IManagerSource> GetSources_UnSafe()
        {
            return WinGetHelper.Instance.GetSources_UnSafe();
        }
    }
}
