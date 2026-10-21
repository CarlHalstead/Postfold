using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Outbox;
using Postfold.Protocols.Adapters;
using Postfold.Protocols.Policies;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Protocols.Services;

public sealed class SmtpSubmitter : IMessageSubmitter
{
    private readonly AccountId _account;
    private readonly MailEndpoint _endpoint;
    private readonly ITransportCredentials _credentials;
    private readonly long _maximumBytes;

    public SmtpSubmitter(AccountId account, MailEndpoint endpoint, ICredentialStore credentials)
        : this(account, endpoint, credentials, 33554432)
    {
    }

    public SmtpSubmitter(AccountId account, MailEndpoint endpoint, ICredentialStore credentials, long maximumBytes)
        : this(account, endpoint, new PasswordTransportCredentials(credentials), maximumBytes)
    {
    }

    public SmtpSubmitter(AccountId account, MailEndpoint endpoint, ITransportCredentials credentials, long maximumBytes)
    {
        _account = account;
        _endpoint = endpoint;
        _credentials = credentials;
        _maximumBytes = maximumBytes;
    }

    internal X509Certificate2? FixtureAuthority { get; set; }

    public async Task<SubmissionOutcome> SubmitAsync(
        FrozenSubmission submission,
        Stream mime,
        CancellationToken cancellationToken
    )
    {
        if (submission.Account != _account || submission.Mime.Account != _account ||
            submission.Mime.Length <= 0 || submission.Mime.Length > _maximumBytes ||
            !MailboxAddress.TryParse(submission.Sender, out MailboxAddress? sender) ||
            submission.Recipients.IsDefaultOrEmpty ||
            submission.Recipients.Any(address => !MailboxAddress.TryParse(address, out _)))
        {
            return Result(
                submission,
                SubmissionState.Rejected,
                SubmissionPhase.BeforeConnect,
                RecipientState.Rejected,
                Failure.Validation
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        Outcome<Credential> credential = await _credentials.ResolveAsync(_account, _endpoint, cancellationToken)
            .ConfigureAwait(false);
        if (!credential.IsSuccess)
        {
            return Result(
                submission,
                SubmissionState.Rejected,
                SubmissionPhase.BeforeConnect,
                RecipientState.NotAccepted,
                credential.Failure == Failure.NotFound ? Failure.NeedsAuthentication : credential.Failure
            );
        }

        using EvidenceSmtpClient client = new EvidenceSmtpClient();
        ConfigureFixtureTrust(client);

        client.Timeout = 30000;
        try
        {
            using MemoryStream frozen = new MemoryStream();
            using BoundedStream input = new BoundedStream(mime, _maximumBytes);
            await input.CopyToAsync(frozen, cancellationToken).ConfigureAwait(false);
            frozen.Position = 0;
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(frozen, cancellationToken).ConfigureAwait(false));
            if (frozen.Length != submission.Mime.Length || hash != submission.Mime.Sha256)
            {
                return Result(
                    submission,
                    SubmissionState.Rejected,
                    SubmissionPhase.BeforeConnect,
                    RecipientState.Rejected,
                    Failure.Validation
                );
            }

            frozen.Position = 0;
            using MimeMessage message = await MimeMessage.LoadAsync(frozen, cancellationToken).ConfigureAwait(false);
            await client.ConnectAsync(
                _endpoint.Host,
                _endpoint.Port,
                _endpoint.Tls == TlsMode.Implicit ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
                cancellationToken
            ).ConfigureAwait(false);
            if (credential.Value!.BearerToken)
            {
                if (!client.AuthenticationMechanisms.Contains("XOAUTH2"))
                {
                    return Result(submission, SubmissionState.Rejected, client.Phase, RecipientState.NotAccepted, Failure.PolicyBlocked);
                }

                await client.AuthenticateAsync(
                    new SaslMechanismOAuth2(_endpoint.Username, credential.Value.Secret),
                    cancellationToken
                ).ConfigureAwait(false);
            }
            else
            {
                await client.AuthenticateAsync(_endpoint.Username, credential.Value.Secret, cancellationToken)
                    .ConfigureAwait(false);
            }

            FormatOptions options = FormatOptions.Default.Clone();
            options.NewLineFormat = NewLineFormat.Dos;
            client.Phase = SubmissionPhase.DataStarted;
            await client.SendAsync(
                options,
                message,
                sender,
                submission.Recipients.Select(MailboxAddress.Parse),
                cancellationToken,
                client
            ).ConfigureAwait(false);
            client.Phase = SubmissionPhase.Acknowledged;
            ImmutableArray<RecipientEvidence> recipients = submission.Recipients.Select(address =>
            {
                RecipientEvidence evidence = client.Evidence.GetValueOrDefault(address) ??
                    new RecipientEvidence(address, RecipientState.NotAccepted, null);
                return evidence.State == RecipientState.Rejected ? evidence :
                    evidence with
                    {
                        State = RecipientState.Accepted
                    };
            }).ToImmutableArray();
            return new SubmissionOutcome(recipients.All(item => item.State == RecipientState.Accepted) ?
                SubmissionState.Accepted : SubmissionState.PartiallyAccepted, client.Phase, recipients, Failure.None);
        }
        catch (MailKit.Security.AuthenticationException exception)
        {
            return Result(
                submission,
                SubmissionState.Rejected,
                client.Phase,
                RecipientState.NotAccepted,
                AuthenticationFailure(exception)
            );
        }
        catch (SmtpCommandException exception)
        {
            ImmutableArray<RecipientEvidence> recipients = submission.Recipients.Select(address =>
                new RecipientEvidence(address, RecipientState.Rejected, (int)exception.StatusCode)).ToImmutableArray();
            return new SubmissionOutcome(
                SubmissionState.Rejected,
                client.Phase,
                recipients,
                (int)exception.StatusCode < 500 ? Failure.Transient : Failure.Validation
            );
        }
        catch (NotSupportedException)
        {
            return Result(submission, SubmissionState.Rejected, client.Phase, RecipientState.NotAccepted, Failure.Validation);
        }
        catch (FormatException)
        {
            return Result(submission, SubmissionState.Rejected, client.Phase, RecipientState.NotAccepted, Failure.Validation);
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or
            SmtpProtocolException or SslHandshakeException or
            System.Security.Authentication.AuthenticationException or OperationCanceledException)
        {
            bool uncertain = client.Phase >= SubmissionPhase.DataStarted;
            if (!uncertain)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ImmutableArray<RecipientEvidence> recipients = submission.Recipients.Select(address =>
            {
                RecipientEvidence? evidence = client.Evidence.GetValueOrDefault(address);
                return evidence?.State == RecipientState.Rejected ? evidence :
                    new RecipientEvidence(address, uncertain ? RecipientState.Uncertain : RecipientState.NotAccepted, null);
            }).ToImmutableArray();
            return new SubmissionOutcome(
                uncertain ? SubmissionState.Uncertain : SubmissionState.Rejected,
                client.Phase,
                recipients,
                uncertain ? Failure.NeedsReview :
                    exception is SslHandshakeException or InvalidDataException or
                        System.Security.Authentication.AuthenticationException ? Failure.Validation : Failure.Transient
            );
        }
    }

    public async Task<Failure> TestAsync(CancellationToken cancellationToken)
    {
        Outcome<Credential> credential = await _credentials.ResolveAsync(
            _account,
            _endpoint,
            cancellationToken
        ).ConfigureAwait(false);
        if (!credential.IsSuccess)
        {
            return credential.Failure == Failure.NotFound ? Failure.NeedsAuthentication : credential.Failure;
        }

        using SmtpClient client = new SmtpClient
        {
            Timeout = 30000
        };
        ConfigureFixtureTrust(client);
        try
        {
            await client.ConnectAsync(
                _endpoint.Host,
                _endpoint.Port,
                _endpoint.Tls == TlsMode.Implicit ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
                cancellationToken
            ).ConfigureAwait(false);
            if (credential.Value!.BearerToken)
            {
                if (!client.AuthenticationMechanisms.Contains("XOAUTH2"))
                {
                    return Failure.PolicyBlocked;
                }

                await client.AuthenticateAsync(
                    new SaslMechanismOAuth2(_endpoint.Username, credential.Value.Secret),
                    cancellationToken
                ).ConfigureAwait(false);
            }
            else
            {
                await client.AuthenticateAsync(_endpoint.Username, credential.Value.Secret, cancellationToken)
                    .ConfigureAwait(false);
            }
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return Failure.None;
        }
        catch (AuthenticationException exception)
        {
            return AuthenticationFailure(exception);
        }
        catch (SslHandshakeException)
        {
            return Failure.TlsRejected;
        }
        catch (System.Security.Authentication.AuthenticationException)
        {
            return Failure.TlsRejected;
        }
        catch (NotSupportedException)
        {
            return Failure.TlsRejected;
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or
            MailKit.ProtocolException or MailKit.CommandException)
        {
            return Failure.Transient;
        }
    }

    private static Failure AuthenticationFailure(AuthenticationException exception)
    {
        // Exchange uses this enhanced status for several policy failures. It does not prove
        // revocation or a specific disabled setting; never expose the accompanying server prose.
        return exception.Message.StartsWith("535: 5.7.139 ", StringComparison.Ordinal) ?
            Failure.PolicyBlocked : Failure.NeedsAuthentication;
    }

    private static SubmissionOutcome Result(
        FrozenSubmission submission,
        SubmissionState state,
        SubmissionPhase phase,
        RecipientState recipientState,
        Failure failure
    )
    {
        return new SubmissionOutcome(state, phase, submission.Recipients.Select(address =>
            new RecipientEvidence(address, recipientState, null)).ToImmutableArray(), failure);
    }

    private void ConfigureFixtureTrust(SmtpClient client)
    {
        if (FixtureAuthority is not null)
        {
            client.ServerCertificateValidationCallback = (_, certificate, _, errors) =>
                FixtureCertificateTrust.Validate(FixtureAuthority, certificate, errors);
        }
    }
}
