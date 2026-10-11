using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Protocols;
using Postfold.Storage;

namespace Postfold.Engine.Tests;

public sealed class MimeTests
{
    [Test]
    public async Task FreezeAsync_WhenAttachmentHasTraversalName_StreamsBytesAndKeepsNameAsMetadata()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        byte[] expected = Encoding.UTF8.GetBytes("synthetic attachment bytes");
        using MemoryStream original = new MemoryStream(expected);
        BlobReference attachment = await profile.Blobs.WriteAsync(EngineFixture.Account, original, 4096,
            protect: true, CancellationToken.None);
        MimePipeline pipeline = new MimePipeline(65536, 20, profile.Blobs);
        ComposeContent content = new ComposeContent("sender@synthetic.invalid", ["recipient@synthetic.invalid"],
            "attachment", "plain body", DateTimeOffset.UnixEpoch, "attachment@synthetic.invalid",
            [new ComposeAttachment("../../outside.exe", "application/octet-stream", attachment)]);
        using MemoryStream frozen = new MemoryStream();

        // Act
        Outcome<bool> freeze = await pipeline.FreezeAsync(content, frozen, CancellationToken.None);
        frozen.Position = 0;
        Outcome<MimeSnapshot> parsed = await pipeline.ParseAsync(frozen, CancellationToken.None);
        frozen.Position = 0;
        using MemoryStream downloaded = new MemoryStream();
        Outcome<bool> copied = await pipeline.CopyAttachmentAsync(frozen, parsed.Value!.Attachments[0].Part,
            downloaded, CancellationToken.None);

        // Assert
        Assert.That(freeze.IsSuccess, Is.True);
        Assert.That(copied.IsSuccess, Is.True);
        Assert.That(downloaded.ToArray(), Is.EqualTo(expected));
        Assert.That(parsed.Value.Attachments[0].Name, Is.EqualTo("../../outside.exe"));
        Assert.That(Directory.GetFiles(directory, "outside.exe", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public async Task ParseAsync_WhenAlternativeAndEmbeddedAttachmentExist_SelectsPlainTextAndPreservesEmbeddedMessage()
    {
        // Arrange
        string mime = "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=mixed\r\n\r\n" +
            "--mixed\r\nContent-Type: multipart/alternative; boundary=alt\r\n\r\n" +
            "--alt\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nplain version\r\n" +
            "--alt\r\nContent-Type: text/html\r\n\r\n<b>html version</b>\r\n--alt--\r\n" +
            "--mixed\r\nContent-Type: message/rfc822\r\nContent-Disposition: attachment; filename=forward.eml\r\n\r\n" +
            "From: sender@synthetic.invalid\r\nSubject: forwarded\r\n\r\nforwarded content\r\n--mixed--\r\n";
        MimePipeline pipeline = new MimePipeline(65536, 20);
        using MemoryStream source = new MemoryStream(Encoding.UTF8.GetBytes(mime));

        // Act
        Outcome<MimeSnapshot> result = await pipeline.ParseAsync(source, CancellationToken.None);
        source.Position = 0;
        using MemoryStream output = new MemoryStream();
        Outcome<bool> copied = await pipeline.CopyAttachmentAsync(source,
            result.Value!.Attachments.Single().Part, output, CancellationToken.None);

        // Assert
        Assert.That(result.Value.PlainText, Does.Contain("plain version"));
        Assert.That(result.Value.Html, Does.Contain("html version"));
        Assert.That(copied.IsSuccess, Is.True);
        Assert.That(Encoding.UTF8.GetString(output.ToArray()), Does.Contain("forwarded content"));
    }
}
