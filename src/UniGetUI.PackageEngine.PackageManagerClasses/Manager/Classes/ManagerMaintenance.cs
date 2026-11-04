using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.ManagerClasses.Classes
{
    public static class ManagerMaintenance
    {
        public static ManagerMaintenanceAction? FindAction(this IPackageManager manager, string actionId) =>
            manager.GetMaintenanceActions().FirstOrDefault(action => action.Id == actionId);

        public static async Task RunAsync(this IPackageManager manager, ManagerMaintenanceAction action)
        {
            await action.RunAsync();
            if (action.ReloadsManager)
                await Task.Run(manager.Initialize);
        }
    }
}
