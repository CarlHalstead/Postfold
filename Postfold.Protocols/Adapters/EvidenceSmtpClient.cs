using MailKit;
using MailKit.Net.Smtp;
using MimeKit;
using Postfold.Core.Enums;
using Postfold.Core.Models.Outbox;
using System;
using System.Collections.Generic;

namespace Postfold.Protocols.Adapters;

internal sealed class EvidenceSmtpClient : SmtpClient, ITransferProgress
{
    internal Dictionary<string, RecipientEvidence> Evidence { get; } =
        new Dictionary<string, RecipientEvidence>(StringComparer.OrdinalIgnoreCase);
    internal SubmissionPhase Phase { get; set; } = SubmissionPhase.BeforeConnect;

    public void Report(long bytesTransferred, long totalSize)
    {
        if (bytesTransferred > 0)
        {
            Phase = SubmissionPhase.AwaitingAcknowledgement;
        }
    }

    public void Report(long bytesTransferred)
    {
        Report(bytesTransferred, 0);
    }

    protected override void OnRecipientAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
    {
        Evidence[mailbox.Address] = new RecipientEvidence(
            mailbox.Address,
            RecipientState.NotAccepted,
            (int)response.StatusCode
        );
    }

    protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
    {
        Evidence[mailbox.Address] = new RecipientEvidence(
            mailbox.Address,
            RecipientState.Rejected,
            (int)response.StatusCode
        );
    }
}
