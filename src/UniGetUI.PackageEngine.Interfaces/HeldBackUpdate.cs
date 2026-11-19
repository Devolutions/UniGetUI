namespace UniGetUI.PackageEngine.Interfaces
{
    public enum HeldBackUpdateReason
    {
        NeverApplied,
        RefusedByPublisher,
        NotApplicable,
    }

    public sealed record HeldBackUpdate(HeldBackUpdateReason Reason, int Attempts);
}
