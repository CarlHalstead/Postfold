namespace Postfold.Core.Interfaces;

/// <summary>Controls whether network access and optional background work are permitted for the current runtime.</summary>
public interface IWorkPolicy
{
    bool AllowNetwork { get; }
    bool AllowOptionalWork { get; }
}
