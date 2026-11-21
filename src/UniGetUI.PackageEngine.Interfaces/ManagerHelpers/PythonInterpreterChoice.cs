namespace UniGetUI.PackageEngine.Interfaces.ManagerProviders
{
    /// <summary>
    /// One interpreter entry offered by a manager with the SupportsCustomPythonInterpreters
    /// capability. <see cref="Request"/> is the opaque string the manager's command line takes
    /// to target it, <see cref="Installed"/> tells whether it is already on disk and
    /// <see cref="Downloadable"/> whether the manager can fetch it on demand.
    /// </summary>
    public record PythonInterpreterChoice(
        string Request,
        string DisplayName,
        bool Installed,
        bool Downloadable
    );
}
