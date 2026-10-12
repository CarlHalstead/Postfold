using Postfold.Core.Enums;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Waits for a mailbox change signal; callers must reconcile because a signal is not authoritative mailbox state.</summary>
public interface IMailboxSignal
{
    Task<Failure> WaitAsync(string mailbox, TimeSpan maximumWait, CancellationToken cancellationToken);
}
