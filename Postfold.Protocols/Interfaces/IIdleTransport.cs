using Postfold.Core.Interfaces;

namespace Postfold.Protocols.Interfaces;

/// <summary>Exposes an IMAP idle signal without making it authoritative reconciliation state.</summary>
internal interface IIdleTransport : IMailboxSignal
{
}
