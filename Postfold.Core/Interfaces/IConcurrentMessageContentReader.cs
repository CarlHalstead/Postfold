namespace Postfold.Core.Interfaces;

/// <summary>
/// Supports message-content reads alongside reconciliation without sharing mutable protocol-session state.
/// The caller still bounds concurrency, owns cancellation and drains reads before disconnecting the account.
/// </summary>
public interface IConcurrentMessageContentReader : IMessageContentReader
{
}
