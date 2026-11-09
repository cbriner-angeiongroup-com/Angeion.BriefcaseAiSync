namespace BriefcaseSync.Core;

public sealed class DestinationRejectedException : Exception
{
    public DestinationRejectedException() : base("Vendor definitively rejected the intake; source correction is required.") { }
}
