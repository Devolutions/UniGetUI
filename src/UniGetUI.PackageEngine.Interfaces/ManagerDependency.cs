namespace UniGetUI.PackageEngine.Classes.Manager.Classes
{
    public readonly struct ManagerDependency
    {
        public readonly string Name;
        public readonly string InstallFileName;
        public readonly string InstallArguments;
        public readonly Func<Task<bool>> IsInstalled;
        public readonly string FancyInstallCommand;

        public ManagerDependency(
            string name,
            string installFileName,
            string installArguments,
            string fancyInstallCommand,
            Func<Task<bool>> isInstalled
        )
        {
            Name = name;
            InstallFileName = installFileName;
            InstallArguments = installArguments;
            IsInstalled = isInstalled;
            FancyInstallCommand = fancyInstallCommand;
        }
    }
}
