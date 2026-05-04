using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.ManagerClasses.Manager
{
    public struct ManagerProperties
    {
        private const IconType DefaultIconId = (IconType)'\uE916';

        public bool IsDummy = false;
        public string Id { get; set; } = "";
        public string Name { get; set; } = "Unset";
        public string? DisplayName { get; set; }
        public string Description { get; set; } = "Unset";
        public IconType IconId { get; set; } = DefaultIconId;
        public string ColorIconId { get; set; } = "Unset";

        // public string ExecutableCallArgs { get; set; } = "Unset";
        public string ExecutableFriendlyName { get; set; } = "Unset";
        public string InstallVerb { get; set; } = "Unset";
        public string UpdateVerb { get; set; } = "Unset";
        public string UninstallVerb { get; set; } = "Unset";
        public IManagerSource[] KnownSources { get; set; } = [];
        public IManagerSource DefaultSource { get; set; } = null!;

        public ManagerProperties() { }

        public ManagerProperties(bool IsDummy)
        {
            this.IsDummy = IsDummy;
        }
    }
}
