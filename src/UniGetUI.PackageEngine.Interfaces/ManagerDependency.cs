namespace UniGetUI.PackageEngine.Classes.Manager.Classes
{
    public readonly struct ManagerDependency
    {
        public readonly string Name;
        public readonly string InstallFileName;
        public readonly string InstallArguments;
        public readonly Func<Task<bool>> IsInstalled;
        public readonly string FancyInstallCommand;

        private readonly Func<(string FileName, string Arguments)>? _resolveCommand;

        public ManagerDependency(
            string name,
            string installFileName,
            string installArguments,
            string fancyInstallCommand,
            Func<Task<bool>> isInstalled,
            Func<(string FileName, string Arguments)>? resolveCommand = null
        )
        {
            Name = name;
            InstallFileName = installFileName;
            InstallArguments = installArguments;
            IsInstalled = isInstalled;
            FancyInstallCommand = fancyInstallCommand;
            _resolveCommand = resolveCommand;
        }

        public (string FileName, string Arguments) GetInstallCommand() =>
            _resolveCommand?.Invoke() ?? (InstallFileName, InstallArguments);
    }
}
