using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IMailboxSignal
{
    Task<Failure> WaitAsync(string mailbox, TimeSpan maximumWait, CancellationToken cancellationToken);
}
