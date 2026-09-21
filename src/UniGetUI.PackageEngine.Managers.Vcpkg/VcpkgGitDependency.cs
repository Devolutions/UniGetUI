using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.Classes;

namespace UniGetUI.PackageEngine.Managers.VcpkgManager
{
    /// <summary>
    /// The Git dependency of the vcpkg manager. vcpkg needs git to refresh its port registry, so a
    /// missing git is surfaced through the missing-dependency dialog with an install command that
    /// fits the platform: winget on Windows, Homebrew (or the Xcode command line tools) on macOS,
    /// and the distribution's package manager through the configured elevator on Linux.
    /// </summary>
    internal static class VcpkgGitDependency
    {
        internal readonly record struct InstallCommand(
            string FileName,
            string Arguments,
            string ManualCommand
        );

        internal const string Name = "Git";

        internal static string ExecutableName => OperatingSystem.IsWindows() ? "git.exe" : "git";

        private const string WindowsInstallArguments =
            "-ExecutionPolicy Bypass -NoLogo -NoProfile -Command \"& {winget install --id Git.Git --exact "
            + "--source winget --accept-source-agreements --accept-package-agreements --force}\"";

        private const string WindowsManualCommand = "winget install --id Git.Git --exact --source winget";

        // Where Homebrew lives when it is not on the PATH of a GUI-launched process.
        private static readonly string[] MacOsBrewLocations =
        [
            "/opt/homebrew/bin/brew",
            "/usr/local/bin/brew",
        ];

        // Checked in this order; the first one found on PATH is used.
        private static readonly (string Executable, string Arguments)[] LinuxPackageManagers =
        [
            ("apt-get", "install -y git"),
            ("dnf", "install -y git"),
            ("pacman", "-S --noconfirm --needed git"),
            ("zypper", "--non-interactive install git"),
        ];

        public static ManagerDependency Create()
        {
            if (OperatingSystem.IsWindows())
            {
                return new ManagerDependency(
                    Name,
                    CoreData.PowerShell5,
                    WindowsInstallArguments,
                    WindowsManualCommand,
                    IsGitInstalledAsync
                );
            }

            // The elevator is configured while the managers load, so the actual command is resolved
            // again when the user clicks Install. The manual command never depends on the elevator.
            InstallCommand? command = Resolve(
                OperatingSystem.IsMacOS(),
                IsOnPath,
                File.Exists,
                CoreData.ElevatorPath,
                CoreData.ElevatorArgs
            );

            return new ManagerDependency(
                Name,
                command?.FileName ?? "",
                command?.Arguments ?? "",
                command?.ManualCommand ?? NoPackageManagerMessage(),
                IsGitInstalledAsync,
                ResolveInstallCommand
            );
        }

        /// <summary>
        /// Chooses the install command for a non-Windows platform, or null when no supported
        /// package manager is available. Pure so that every platform's choice can be tested anywhere.
        /// </summary>
        internal static InstallCommand? Resolve(
            bool isMacOS,
            Func<string, bool> isOnPath,
            Func<string, bool> fileExists,
            string elevatorPath,
            string elevatorArgs
        )
        {
            if (isMacOS)
            {
                if (isOnPath("brew"))
                {
                    return new InstallCommand("brew", "install git", "brew install git");
                }

                foreach (string brew in MacOsBrewLocations)
                {
                    if (fileExists(brew))
                    {
                        return new InstallCommand(brew, "install git", $"{brew} install git");
                    }
                }

                return new InstallCommand("xcode-select", "--install", "xcode-select --install");
            }

            foreach (var (executable, arguments) in LinuxPackageManagers)
            {
                if (!isOnPath(executable))
                {
                    continue;
                }

                string manualCommand = $"sudo {executable} {arguments}";
                if (string.IsNullOrWhiteSpace(elevatorPath))
                {
                    return new InstallCommand(executable, arguments, manualCommand);
                }

                string elevatedArguments = string.Join(
                    ' ',
                    new[] { elevatorArgs.Trim(), executable, arguments }.Where(part => part.Length > 0)
                );
                return new InstallCommand(elevatorPath, elevatedArguments, manualCommand);
            }

            return null;
        }

        private static (string FileName, string Arguments) ResolveInstallCommand()
        {
            InstallCommand? command = Resolve(
                OperatingSystem.IsMacOS(),
                IsOnPath,
                File.Exists,
                CoreData.ElevatorPath,
                CoreData.ElevatorArgs
            );

            if (command is null)
            {
                throw new InvalidOperationException(NoPackageManagerMessage());
            }

            return (command.Value.FileName, command.Value.Arguments);
        }

        private static string NoPackageManagerMessage() =>
            CoreTools.Translate(
                "No supported package manager was found. Please install {0} manually and restart UniGetUI.",
                Name
            );

        private static bool IsOnPath(string executable) => CoreTools.Which(executable).Item1;

        private static async Task<bool> IsGitInstalledAsync() =>
            (await CoreTools.WhichAsync(ExecutableName)).Item1;
    }
}
