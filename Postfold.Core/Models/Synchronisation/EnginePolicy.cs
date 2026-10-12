using Postfold.Core.Interfaces;

namespace Postfold.Core.Models.Synchronisation;

public sealed record EnginePolicy(bool NetworkAvailable, bool Metered, bool LowDisk) : IWorkPolicy
{
    public bool AllowNetwork => NetworkAvailable;
    public bool AllowOptionalWork => NetworkAvailable && !Metered && !LowDisk;
}
