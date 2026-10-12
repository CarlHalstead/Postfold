using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Protocols.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Protocols.Services;

internal sealed class ImapIdleSignal : IMailboxSignal
{
    private readonly IIdleTransport _transport;

    internal ImapIdleSignal(IIdleTransport transport)
    {
        _transport = transport;
    }

    public Task<Failure> WaitAsync(string mailbox, TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        return _transport.WaitAsync(mailbox, maximumWait, cancellationToken);
    }
}
