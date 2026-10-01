namespace UniGetUI.Interface.Enums
{
    /// <summary>
    /// Represents the visual status of a package on a list
    /// </summary>
    public enum PackageTag
    {
        Default,
        AlreadyInstalled,
        IsUpgradable,
        Pinned,
        OnQueue,
        BeingProcessed,
        Failed,
        Unavailable,
    }

    public enum IconType
    {
        AddTo = '\uE900',
        Android = '\uE901',
        Backward = '\uE902',
        Bucket = '\uE903',
        Buggy = '\uE904',
        Checksum = '\uE905',
        Chocolatey = '\uE906',
        ClipboardList = '\uE907',
        Close_Round = '\uE908',
        Collapse = '\uE909',
        Console = '\uE90A',
        Copy = '\uE90B',
        Cross = '\uE90C',
        Delete = '\uE90D',
        Disk = '\uE90E',
        DotNet = '\uE90F',
        Download = '\uE910',
        Empty = '\uE911',
        Expand = '\uE912',
        Experimental = '\uE913',
        Forward = '\uE914',
        GOG = '\uE915',
        Help = '\uE916',
        History = '\uE917',
        Home = '\uE918',
        Id = '\uE919',
        Info_Round = '\uE91A',
        Installed = '\uE91B',
        Installed_Filled = '\uE91C',
        Interactive = '\uE91D',
        Launch = '\uE91E',
        Loading = '\uE91F',
        Loading_Filled = '\uE920',
        LocalPc = '\uE921',
        Megaphone = '\uE922',
        MsStore = '\uE923',
        Node = '\uE924',
        OpenFolder = '\uE925',
        Options = '\uE926',
        Package = '\uE927',
        Pin = '\uE928',
        Pin_Filled = '\uE929',
        PowerShell = '\uE92A',
        Python = '\uE92B',
        Reload = '\uE92C',
        SandClock = '\uE92D',
        SaveAs = '\uE92E',
        Scoop = '\uE92F',
        Search = '\uE930',
        Settings = '\uE931',
        Share = '\uE932',
        Skip = '\uE933',
        Steam = '\uE934',
        SysTray = '\uE935',
        UAC = '\uE936',
        Undelete = '\uE937',
        Update = '\uE938',
        Upgradable = '\uE939',
        Upgradable_Filled = '\uE93A',
        UPlay = '\uE93B',
        Version = '\uE93C',
        Warning = '\uE93D',
        Warning_Filled = '\uE93E',
        Warning_Round = '\uE93F',
        WinGet = '\uE940',
        Rust = '\uE941',
        Vcpkg = '\uE942',
        Homebrew = '\uE943',
        Apt = '\uE944',
        Dnf = '\uE945',
        Pacman = '\uE946',
        Snap = '\uE947',
        Flatpak = '\uE948',
        Bun = '\uE949',
    }

    public class NotificationArguments
    {
        public const string Show = "openUniGetUI";
        public const string ShowOnUpdatesTab = "openUniGetUIOnUpdatesTab";
        public const string UpdateAllPackages = "updateAll";
        public const string ReleaseSelfUpdateLock = "releaseSelfUpdateLock";
    }

    public enum BundleReportSeverity
    {
        Info = 0,
        High = 1,
    }

    public enum BundleSourceStatus
    {
        Default = 0,
        Known = 1,
        Unknown = 2,
    }

    public struct BundleReportEntry
    {
        public readonly string Field;
        public readonly string Label;
        public readonly string Value;
        public readonly string Line;
        public readonly BundleReportSeverity Severity;
        public readonly bool Allowed;
        public readonly bool StrippedBySetting;

        public BundleReportEntry(
            string field,
            string label,
            string value,
            string line,
            BundleReportSeverity severity,
            bool allowed,
            bool strippedBySetting = false)
        {
            Field = field;
            Label = label;
            Value = value;
            Line = line;
            Severity = severity;
            Allowed = allowed;
            StrippedBySetting = strippedBySetting;
        }

        public readonly bool LineCarriesTheValue => Line != Label;
    }

    public readonly record struct BundleReportSubject(
        string Id,
        string Name,
        string ManagerName,
        string Source)
    {
        public string DisplayName => Name.Length is 0 ? Id : Name;

        public string Key => $"{ManagerName}\\{Source}\\{Id}";
    }

    public sealed class BundleReportPackage
    {
        public BundleReportSubject Subject { get; }
        public List<BundleReportEntry> Entries { get; } = [];

        public BundleReportPackage(BundleReportSubject subject)
        {
            Subject = subject;
        }

        public bool HasHighSeverityFindings
            => Entries.Any(entry => entry.Severity is BundleReportSeverity.High);
    }

    public struct BundleReport
    {
        public bool IsEmpty = false;
        public Dictionary<string, BundleReportPackage> Contents = new();

        public BundleReport() { }

        public readonly IEnumerable<BundleReportEntry> AllEntries
            => Contents.Values.SelectMany(package => package.Entries);

        public readonly bool HasHighSeverityFindings
            => AllEntries.Any(entry => entry.Severity is BundleReportSeverity.High);

        public readonly bool HasStrippedFindings
            => AllEntries.Any(entry => !entry.Allowed);

        public readonly bool HasSettingControlledStripping
            => AllEntries.Any(entry => entry.StrippedBySetting);

        public readonly int HighSeverityCount
            => AllEntries.Count(entry => entry.Severity is BundleReportSeverity.High);

        public readonly int InformationalCount
            => AllEntries.Count(entry => entry.Severity is BundleReportSeverity.Info);
    }
}
