using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.ManagerClasses.Classes
{
    public static class ManagerHealthEvaluator
    {
        public const int AttentionFailureThreshold = 2;

        public static readonly TimeSpan AttentionWindow = TimeSpan.FromDays(1);

        public static ManagerHealthReport Evaluate(IPackageManager manager)
        {
            ManagerHealthRecord record = manager.GetHealthRecord();

            if (!manager.IsEnabled())
                return Report(manager, record, ManagerHealthState.Disabled);

            if (manager.IsInitializing)
                return Report(manager, record, ManagerHealthState.Loading);

            if (!manager.IsReady())
                return Report(manager, record, ManagerHealthState.NotFound);

            var failures = Enum.GetValues<ManagerListingKind>()
                .Select(kind => (Kind: kind, Listing: record[kind]))
                .Where(entry => entry.Listing.ConsecutiveFailures > 0)
                .ToArray();

            if (failures.Length > 0)
            {
                var urgent = failures
                    .Where(entry => FailureNeedsAttention(manager, entry.Kind, entry.Listing))
                    .OrderByDescending(entry => entry.Listing.ConsecutiveFailures)
                    .ThenByDescending(entry => entry.Listing.LastFailureUtc)
                    .FirstOrDefault();
                if (urgent.Listing is null && IsMalfunctioning(manager, record))
                    return Malfunction(manager, record);

                var shown = urgent.Listing is not null
                    ? urgent
                    : failures.OrderByDescending(entry => entry.Listing.LastFailureUtc).First();

                return new ManagerHealthReport
                {
                    Manager = manager,
                    Record = record,
                    State = ManagerHealthState.Failing,
                    FailingListing = shown.Kind,
                    Reason = shown.Listing.LastFailureReason,
                    ConsecutiveFailures = shown.Listing.ConsecutiveFailures,
                    NeedsAttention = urgent.Listing is not null,
                };
            }

            if (IsMalfunctioning(manager, record))
                return Malfunction(manager, record);

            bool anySuccess = Enum.GetValues<ManagerListingKind>()
                .Any(kind => record[kind].LastSuccessUtc is not null);
            return Report(
                manager,
                record,
                anySuccess ? ManagerHealthState.Healthy : ManagerHealthState.Unknown
            );
        }

        public static IReadOnlyList<ManagerHealthReport> ManagersNeedingAttention(
            IEnumerable<IPackageManager> managers
        ) => managers.Select(Evaluate).Where(report => report.NeedsAttention).ToArray();

        public static string ListingName(ManagerListingKind kind) =>
            kind switch
            {
                ManagerListingKind.Search => CoreTools.Translate("Search"),
                ManagerListingKind.Installed => CoreTools.Translate("Installed packages"),
                _ => CoreTools.Translate("Updates"),
            };

        public static string StateName(ManagerHealthState state) =>
            state switch
            {
                ManagerHealthState.Loading => CoreTools.Translate("Loading"),
                ManagerHealthState.Healthy => CoreTools.Translate("Healthy"),
                ManagerHealthState.Failing => CoreTools.Translate("Failing"),
                ManagerHealthState.Malfunctioning => CoreTools.Translate("Malfunctioning"),
                ManagerHealthState.NotFound => CoreTools.Translate("Not found"),
                ManagerHealthState.Disabled => CoreTools.Translate("Disabled"),
                _ => CoreTools.Translate("Not checked yet"),
            };

        public static string Describe(ManagerHealthReport report) =>
            report.State switch
            {
                ManagerHealthState.Loading => CoreTools.Translate(
                    "{0} is still loading.",
                    report.Manager.DisplayName
                ),
                ManagerHealthState.Healthy => CoreTools.Translate("Its last listings completed normally."),
                ManagerHealthState.Failing => CoreTools.Translate(
                    "{0} failed {1} time(s) in a row: {2}",
                    ListingName(report.FailingListing ?? ManagerListingKind.Installed),
                    report.ConsecutiveFailures,
                    report.Reason ?? CoreTools.Translate("Unknown error")
                ),
                ManagerHealthState.Malfunctioning => CoreTools.Translate(
                    "{0} is ready but listed no installed packages, which usually means it is not working properly.",
                    report.Manager.DisplayName
                ),
                ManagerHealthState.NotFound => CoreTools.Translate(
                    "{0} is enabled but could not be found on this system.",
                    report.Manager.DisplayName
                ),
                ManagerHealthState.Disabled => CoreTools.Translate(
                    "{0} is disabled",
                    report.Manager.DisplayName
                ),
                _ => CoreTools.Translate("No listing has run since this record started."),
            };

        private static bool FailureNeedsAttention(
            IPackageManager manager,
            ManagerListingKind kind,
            ManagerListingHealth listing
        ) =>
            listing.ConsecutiveFailures
                >= (
                    kind is ManagerListingKind.Installed && DetectsMalfunctions(manager)
                        ? 1
                        : AttentionFailureThreshold
                )
            && listing.LastFailureUtc >= ManagerHealthStore.Now - AttentionWindow;

        private static bool IsMalfunctioning(IPackageManager manager, ManagerHealthRecord record) =>
            DetectsMalfunctions(manager)
            && record.Installed.ConsecutiveFailures == 0
            && record.Installed.LastSuccessUtc is not null
            && record.Installed.LastPackageCount == 0;

        private static ManagerHealthReport Malfunction(
            IPackageManager manager,
            ManagerHealthRecord record
        ) =>
            new()
            {
                Manager = manager,
                Record = record,
                State = ManagerHealthState.Malfunctioning,
                FailingListing = ManagerListingKind.Installed,
                NeedsAttention = true,
            };

        private static bool DetectsMalfunctions(IPackageManager manager) =>
            manager.ExpectsInstalledPackages
            && !(
                manager.Name.Equals("WinGet", StringComparison.OrdinalIgnoreCase)
                && Settings.Get(Settings.K.DisableWinGetMalfunctionDetector)
            );

        private static ManagerHealthReport Report(
            IPackageManager manager,
            ManagerHealthRecord record,
            ManagerHealthState state
        ) =>
            new()
            {
                Manager = manager,
                Record = record,
                State = state,
            };
    }
}
