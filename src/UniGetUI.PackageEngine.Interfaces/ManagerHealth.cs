using System.Text.Json.Serialization;

namespace UniGetUI.PackageEngine.Interfaces
{
    [JsonConverter(typeof(JsonStringEnumConverter<ManagerListingKind>))]
    public enum ManagerListingKind
    {
        Search,
        Installed,
        Updates,
    }

    [JsonConverter(typeof(JsonStringEnumConverter<ManagerHealthState>))]
    public enum ManagerHealthState
    {
        Unknown,
        Loading,
        Healthy,
        Failing,
        Malfunctioning,
        NotFound,
        Disabled,
    }

    public sealed class ManagerListingHealth
    {
        public const int MaxRecentDurations = 10;

        public DateTime? LastSuccessUtc { get; set; }
        public DateTime? LastFailureUtc { get; set; }
        public string? LastFailureReason { get; set; }
        public bool LastFailureWasTimeout { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int? LastPackageCount { get; set; }
        public DateTime? LastFastRepairUtc { get; set; }
        public string? LastFastRepairReason { get; set; }
        public List<long> RecentDurationsMs { get; set; } = [];

        public ManagerListingHealth Clone() =>
            new()
            {
                LastSuccessUtc = LastSuccessUtc,
                LastFailureUtc = LastFailureUtc,
                LastFailureReason = LastFailureReason,
                LastFailureWasTimeout = LastFailureWasTimeout,
                ConsecutiveFailures = ConsecutiveFailures,
                LastPackageCount = LastPackageCount,
                LastFastRepairUtc = LastFastRepairUtc,
                LastFastRepairReason = LastFastRepairReason,
                RecentDurationsMs = [.. RecentDurationsMs],
            };
    }

    public sealed class ManagerHealthRecord
    {
        public ManagerListingHealth Search { get; set; } = new();
        public ManagerListingHealth Installed { get; set; } = new();
        public ManagerListingHealth Updates { get; set; } = new();

        public ManagerListingHealth this[ManagerListingKind kind] =>
            kind switch
            {
                ManagerListingKind.Search => Search,
                ManagerListingKind.Installed => Installed,
                _ => Updates,
            };

        public ManagerHealthRecord Clone() =>
            new()
            {
                Search = Search.Clone(),
                Installed = Installed.Clone(),
                Updates = Updates.Clone(),
            };
    }

    public sealed class ManagerHealthReport
    {
        public required IPackageManager Manager { get; init; }
        public required ManagerHealthState State { get; init; }
        public required ManagerHealthRecord Record { get; init; }
        public ManagerListingKind? FailingListing { get; init; }
        public string? Reason { get; init; }
        public int ConsecutiveFailures { get; init; }
        public bool NeedsAttention { get; init; }
    }

    public static class ManagerMaintenanceActionIds
    {
        public const string ScoopCleanup = "cleanup-scoop";
    }

    public sealed class ManagerMaintenanceAction
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string Description { get; init; }
        public bool RequiresConfirmation { get; init; } = true;
        public bool RequiresElevation { get; init; }
        public bool ReloadsManager { get; init; } = true;
        public bool AvailableWhenNotReady { get; init; }
        public required Func<Task> RunAsync { get; init; }
    }
}
