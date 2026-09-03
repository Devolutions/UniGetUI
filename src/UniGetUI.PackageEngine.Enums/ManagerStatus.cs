using System.Diagnostics;

namespace UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers
{
    public class ManagerStatus
    {
        public string Version = "";
        public bool Found;
        public string ExecutablePath = "";
        public string ExecutableCallArgs = "Unset";

        /// <summary>
        /// The argument vector that precedes a call's own arguments, for managers whose command line
        /// must be built with <see cref="ProcessStartInfo.ArgumentList"/> so that each argument is
        /// quoted individually instead of being concatenated into one string. Empty for managers
        /// that still use the concatenated <see cref="ExecutableCallArgs"/> form.
        /// </summary>
        public IReadOnlyList<string> OperationCallArgs = [];

        /// <summary>
        /// Puts <paramref name="arguments"/> on <paramref name="startInfo"/>, using the argument
        /// vector when this manager has one and falling back to the concatenated form otherwise.
        /// </summary>
        public void ApplyArguments(ProcessStartInfo startInfo, params string[] arguments)
        {
            if (OperationCallArgs.Count is 0 && !string.IsNullOrWhiteSpace(ExecutableCallArgs))
            {
                startInfo.Arguments = ExecutableCallArgs + " " + string.Join(" ", arguments);
                return;
            }

            startInfo.Arguments = string.Empty;
            startInfo.ArgumentList.Clear();
            foreach (string argument in OperationCallArgs)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
    }
}
