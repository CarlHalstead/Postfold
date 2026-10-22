namespace Postfold.Core.Interfaces;

/// <summary>
/// Remote-read work that may overlap the account's serial reconciliation or mutation work.
/// Local cache writes must use the account store's normal serial writer; remote side effects are excluded.
/// The scheduler provides one additional shared read slot and includes it in pause, suspend and shutdown draining.
/// </summary>
public interface IConcurrentAccountWork : IAccountWork
{
}
