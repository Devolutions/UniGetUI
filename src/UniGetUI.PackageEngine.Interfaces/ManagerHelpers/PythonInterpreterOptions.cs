namespace UniGetUI.PackageEngine.Interfaces.ManagerProviders
{
    /// <summary>
    /// The Python interpreter options a manager used the last time it installed or upgraded a
    /// package, so the install-options dialog can show them instead of starting from Default.
    /// <see cref="Request"/> is the same opaque value as
    /// <see cref="PythonInterpreterChoice.Request"/>, or null when no interpreter was requested.
    /// </summary>
    public record PythonInterpreterOptions(
        string? Request,
        bool? ManagedPython,
        bool NoPythonDownloads
    );
}
