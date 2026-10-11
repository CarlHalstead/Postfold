namespace Postfold.Core;

public sealed record EnginePolicy(bool NetworkAvailable, bool Metered, bool LowDisk) : IWorkPolicy
{
    public bool AllowNetwork => NetworkAvailable;
    public bool AllowOptionalWork => NetworkAvailable && !Metered && !LowDisk;
}
