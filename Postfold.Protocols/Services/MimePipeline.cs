using MimeKit;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Protocols.Services;

public sealed class MimePipeline : IMimePipeline, IMimeDraftReader, IMimeDraftWriter
{
    private readonly long _maximumBytes;
    private readonly int _maximumParts;
    private readonly IBlobStore? _blobs;

    public MimePipeline(long maximumBytes, int maximumParts)
        : this(maximumBytes, maximumParts, null)
    {
    }

    public MimePipeline(long maximumBytes, int maximumParts, IBlobStore? blobs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumParts);
        _maximumBytes = maximumBytes;
        _maximumParts = maximumParts;
        _blobs = blobs;
    }

    public async Task<Outcome<MimeSnapshot>> ParseAsync(Stream source, CancellationToken cancellationToken)
    {
        try
        {
            using MimeMessage message = await LoadAsync(source, cancellationToken).ConfigureAwait(false);
            List<MimeEntity> parts = Enumerate(message.Body ?? throw new InvalidDataException("Missing MIME body."));
            ImmutableArray<AttachmentSnapshot>.Builder attachments = ImmutableArray.CreateBuilder<AttachmentSnapshot>();
            long decoded = 0;
            for (int index = 0; index < parts.Count; index++)
            {
                if (parts[index] is MimePart part && part.Content is not null)
                {
                    using BoundedStream counter = new BoundedStream(Stream.Null, _maximumBytes - decoded);
                    await part.Content.DecodeToAsync(counter, cancellationToken).ConfigureAwait(false);
                    decoded += counter.Length;
                    if (part.IsAttachment)
                    {
                        attachments.Add(new AttachmentSnapshot(
                            index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            part.FileName ?? "",
                            part.ContentType.MimeType,
                            counter.Length
                        ));
                    }
                }
                else if (parts[index] is MessagePart nested && nested.IsAttachment && nested.Message is not null)
                {
                    using BoundedStream counter = new BoundedStream(Stream.Null, _maximumBytes - decoded);
                    await nested.Message.WriteToAsync(counter, cancellationToken).ConfigureAwait(false);
                    decoded += counter.Length;
                    attachments.Add(new AttachmentSnapshot(
                        index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        nested.ContentDisposition?.FileName ?? "attached-message.eml",
                        "message/rfc822",
                        counter.Length
                    ));
                }
            }

            InternetAddressList reply = message.ReplyTo.Count > 0 ? message.ReplyTo : message.From;
            ImmutableArray<string> replyRecipients = reply.Mailboxes.Select(item => item.Address).Take(101).ToImmutableArray();
            if (replyRecipients.Length > 100)
            {
                return Outcome<MimeSnapshot>.Fail(Failure.Validation);
            }

            return Outcome<MimeSnapshot>.Success(new MimeSnapshot(
                message.Subject ?? "",
                message.From.ToString(),
                message.MessageId,
                message.TextBody,
                message.HtmlBody,
                attachments.ToImmutable(),
                replyRecipients
            ));
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException)
        {
            return Outcome<MimeSnapshot>.Fail(Failure.Validation);
        }
    }

    public async Task<Outcome<ComposeContent>> ReadComposeAsync(
        AccountId account,
        Stream source,
        CancellationToken cancellationToken
    )
    {
        if (_blobs is null)
        {
            return Outcome<ComposeContent>.Fail(Failure.Unavailable);
        }

        try
        {
            using MimeMessage message = await LoadAsync(source, cancellationToken).ConfigureAwait(false);
            List<MimeEntity> parts = Enumerate(message.Body ?? throw new InvalidDataException("Missing MIME body."));
            ImmutableArray<ComposeAttachment>.Builder attachments = ImmutableArray.CreateBuilder<ComposeAttachment>();
            long decoded = 0;
            foreach (MimeEntity entity in parts)
            {
                if (entity is MessagePart nested && nested.IsAttachment)
                {
                    return Outcome<ComposeContent>.Fail(Failure.NeedsReview);
                }

                if (entity is not MimePart part || !part.IsAttachment || part.Content is null)
                {
                    continue;
                }

                using MemoryStream content = new MemoryStream();
                using (BoundedStream bounded = new BoundedStream(content, _maximumBytes - decoded))
                {
                    await part.Content.DecodeToAsync(bounded, cancellationToken).ConfigureAwait(false);
                }

                decoded += content.Length;
                content.Position = 0;
                BlobReference blob = await _blobs.WriteAsync(
                    account,
                    content,
                    _maximumBytes,
                    true,
                    cancellationToken
                ).ConfigureAwait(false);
                attachments.Add(new ComposeAttachment(part.FileName ?? "attachment", part.ContentType.MimeType, blob));
            }

            return Outcome<ComposeContent>.Success(new ComposeContent(
                message.From.Mailboxes.FirstOrDefault()?.Address ?? "",
                message.To.Mailboxes.Select(item => item.Address).ToImmutableArray(),
                message.Subject ?? "",
                message.TextBody ?? "",
                message.Date,
                message.MessageId ?? Guid.NewGuid().ToString("N") + "@postfold.invalid",
                attachments.ToImmutable()
            ));
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException)
        {
            return Outcome<ComposeContent>.Fail(Failure.Validation);
        }
    }

    public async Task<Outcome<MimeHtmlDocument>> ReadHtmlAsync(Stream source, CancellationToken cancellationToken)
    {
        try
        {
            using MimeMessage message = await LoadAsync(source, cancellationToken).ConfigureAwait(false);
            MimeEntity body = message.Body ?? throw new InvalidDataException("Missing MIME body.");
            _ = Enumerate(body);
            (TextPart? Html, MultipartRelated? Scope) selected = SelectHtml(body, null);
            if (selected.Html is null || selected.Html.Content is null)
            {
                return Outcome<MimeHtmlDocument>.Fail(Failure.NotFound);
            }

            using MemoryStream htmlBytes = new MemoryStream();
            using (BoundedStream bounded = new BoundedStream(htmlBytes, 262144))
            {
                await selected.Html.Content.DecodeToAsync(bounded, cancellationToken).ConfigureAwait(false);
            }

            string html = selected.Html.Text;
            if (System.Text.Encoding.UTF8.GetByteCount(html) > 262144)
            {
                return Outcome<MimeHtmlDocument>.Fail(Failure.Validation);
            }

            ImmutableArray<InlineImage>.Builder images = ImmutableArray.CreateBuilder<InlineImage>();
            HashSet<string> identifiers = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            if (selected.Scope is MultipartRelated related)
            {
                foreach (MimeEntity entity in related)
                {
                    if (entity is not MimePart part || part.IsAttachment || part.Content is null ||
                        !part.ContentType.MimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(part.ContentId))
                    {
                        continue;
                    }

                    if (part.ContentId.Length > 256 || !identifiers.Add(part.ContentId) || images.Count == 32)
                    {
                        return Outcome<MimeHtmlDocument>.Fail(Failure.Validation);
                    }

                    using MemoryStream decoded = new MemoryStream();
                    using (BoundedStream bounded = new BoundedStream(decoded, Math.Min(4194304, 16777216 - total)))
                    {
                        await part.Content.DecodeToAsync(bounded, cancellationToken).ConfigureAwait(false);
                    }

                    total += decoded.Length;
                    images.Add(new InlineImage(part.ContentId, decoded.ToArray()));
                }
            }

            return Outcome<MimeHtmlDocument>.Success(new MimeHtmlDocument(html, images.ToImmutable()));
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException)
        {
            return Outcome<MimeHtmlDocument>.Fail(Failure.Validation);
        }
    }

    public async Task<Outcome<bool>> CopyAttachmentAsync(
        Stream source,
        string part,
        Stream destination,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using MimeMessage message = await LoadAsync(source, cancellationToken).ConfigureAwait(false);
            List<MimeEntity> parts = Enumerate(message.Body ?? throw new InvalidDataException("Missing MIME body."));
            if (!int.TryParse(part, out int index) || index < 0 || index >= parts.Count)
            {
                return Outcome<bool>.Fail(Failure.NotFound);
            }

            using BoundedStream bounded = new BoundedStream(destination, _maximumBytes);
            if (parts[index] is MimePart attachment && attachment.IsAttachment && attachment.Content is not null)
            {
                await attachment.Content.DecodeToAsync(bounded, cancellationToken).ConfigureAwait(false);
            }
            else if (parts[index] is MessagePart nested && nested.IsAttachment && nested.Message is not null)
            {
                await nested.Message.WriteToAsync(bounded, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return Outcome<bool>.Fail(Failure.NotFound);
            }

            return Outcome<bool>.Success(true);
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException)
        {
            return Outcome<bool>.Fail(Failure.Validation);
        }
    }

    public Task<Outcome<bool>> FreezeAsync(
        ComposeContent content,
        Stream destination,
        CancellationToken cancellationToken
    )
    {
        return WriteComposeAsync(content, destination, false, cancellationToken);
    }

    public Task<Outcome<bool>> WriteDraftAsync(
        ComposeContent content,
        Stream destination,
        CancellationToken cancellationToken
    )
    {
        return WriteComposeAsync(content, destination, true, cancellationToken);
    }

    private static (TextPart? Html, MultipartRelated? Scope) SelectHtml(MimeEntity entity, MultipartRelated? scope)
    {
        if (entity.IsAttachment || entity is MessagePart)
        {
            return (null, null);
        }

        if (entity is TextPart text)
        {
            return text.IsHtml ? (text, scope) : (null, null);
        }

        if (entity is MultipartRelated related)
        {
            return related.Root is MimeEntity root ? SelectHtml(root, related) : (null, null);
        }

        if (entity is Multipart multipart)
        {
            for (int offset = 0; offset < multipart.Count; offset++)
            {
                int index = multipart is MultipartAlternative ? multipart.Count - offset - 1 : offset;
                (TextPart? Html, MultipartRelated? Scope) selected = SelectHtml(multipart[index], scope);
                if (selected.Html is not null)
                {
                    return selected;
                }
            }
        }

        return (null, null);
    }

    private async Task<Outcome<bool>> WriteComposeAsync(
        ComposeContent content,
        Stream destination,
        bool draft,
        CancellationToken cancellationToken
    )
    {
        MailboxAddress? sender = null;
        ImmutableArray<string> recipients = content.Recipients.IsDefault ? [] : content.Recipients;
        if (content.Sender.Contains('\r') || content.Sender.Contains('\n') ||
            ((!draft || content.Sender.Length > 0) && (!MailboxAddress.TryParse(content.Sender, out sender) ||
                !string.Equals(sender.Address, content.Sender, StringComparison.OrdinalIgnoreCase))) ||
            (!draft && recipients.IsEmpty) || recipients.Length > 1000 ||
            content.Subject.Contains('\r') || content.Subject.Contains('\n') ||
            content.MessageId.Contains('\r') || content.MessageId.Contains('\n') ||
            recipients.Any(address => address.Contains('\r') || address.Contains('\n')))
        {
            return Outcome<bool>.Fail(Failure.Validation);
        }

        using MimeMessage message = new MimeMessage();
        if (sender is not null)
        {
            message.From.Add(sender);
        }

        foreach (string address in recipients)
        {
            if (!MailboxAddress.TryParse(address, out MailboxAddress? recipient) ||
                !string.Equals(recipient.Address, address, StringComparison.OrdinalIgnoreCase))
            {
                return Outcome<bool>.Fail(Failure.Validation);
            }

            message.To.Add(recipient);
        }

        message.Subject = content.Subject;
        message.Date = content.Date;
        message.MessageId = content.MessageId;
        message.Body = new TextPart("plain")
        {
            Text = content.PlainText + (string.IsNullOrEmpty(content.Signature) ? "" : "\n-- \n" + content.Signature)
        };
        List<Stream> owned = new List<Stream>();
        try
        {
            if (!content.Attachments.IsDefaultOrEmpty)
            {
                if (_blobs is null || content.Attachments.Length >= _maximumParts)
                {
                    return Outcome<bool>.Fail(Failure.Validation);
                }

                Multipart multipart = new Multipart("mixed");
                multipart.Add(message.Body);
                foreach (ComposeAttachment attachment in content.Attachments)
                {
                    if (attachment.Name.Contains('\r') || attachment.Name.Contains('\n') ||
                        attachment.Name.Contains('\0') || !ContentType.TryParse(attachment.MediaType, out ContentType? type) ||
                        attachment.Content.Length > _maximumBytes)
                    {
                        return Outcome<bool>.Fail(Failure.Validation);
                    }

                    Stream source = await _blobs.OpenAsync(attachment.Content, cancellationToken).ConfigureAwait(false);
                    owned.Add(source);
                    MimePart part = new MimePart(type)
                    {
                        FileName = attachment.Name,
                        Content = new MimeContent(source),
                        ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                        ContentTransferEncoding = ContentEncoding.Base64
                    };
                    multipart.Add(part);
                }

                message.Body = multipart;
            }

            message.Prepare(EncodingConstraint.SevenBit);
            using BoundedStream bounded = new BoundedStream(destination, _maximumBytes);
            FormatOptions options = FormatOptions.Default.Clone();
            options.NewLineFormat = NewLineFormat.Dos;
            await message.WriteToAsync(options, bounded, cancellationToken).ConfigureAwait(false);
            return Outcome<bool>.Success(true);
        }
        catch (InvalidDataException)
        {
            return Outcome<bool>.Fail(Failure.Validation);
        }
        finally
        {
            foreach (Stream stream in owned)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<MimeMessage> LoadAsync(Stream source, CancellationToken cancellationToken)
    {
        using BoundedStream bounded = new BoundedStream(source, _maximumBytes);
        using MemoryStream buffer = new MemoryStream();
        await bounded.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        ParserOptions options = ParserOptions.Default.Clone();
        options.MaxMimeDepth = 32;
        return await MimeMessage.LoadAsync(options, buffer, cancellationToken).ConfigureAwait(false);
    }

    private List<MimeEntity> Enumerate(MimeEntity root)
    {
        List<MimeEntity> result = new List<MimeEntity>();
        Stack<(MimeEntity Entity, int Depth)> pending = new Stack<(MimeEntity, int)>();
        pending.Push((root, 0));
        while (pending.TryPop(out (MimeEntity Entity, int Depth) item))
        {
            if (result.Count >= _maximumParts || item.Depth > 32)
            {
                throw new InvalidDataException("MIME nesting or part limit exceeded.");
            }

            result.Add(item.Entity);
            if (item.Entity is Multipart multipart)
            {
                for (int index = multipart.Count - 1; index >= 0; index--)
                {
                    pending.Push((multipart[index], item.Depth + 1));
                }
            }
            else if (item.Entity is MessagePart nested && nested.Message?.Body is not null)
            {
                pending.Push((nested.Message.Body, item.Depth + 1));
            }
        }

        return result;
    }
}
