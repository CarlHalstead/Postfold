using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Mime;
using Postfold.Protocols.Services;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Mime;

public sealed class HtmlMimeTests
{
    [Test]
    public async Task ReadHtmlAsync_WhenOnlyPlainTextExists_EscapesItInTheSameHtmlReader()
    {
        // Arrange
        using MemoryStream source = new MemoryStream(Encoding.UTF8.GetBytes(
            "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n<script>alert('x')</script> & text\r\nnext line"
        ));

        // Act
        Outcome<MimeHtmlDocument> result = await new MimePipeline(1048576, 100).ReadHtmlAsync(source, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Html, Does.StartWith("<pre>&lt;script&gt;"));
        Assert.That(result.Value.Html, Does.Contain("&amp; text"));
        Assert.That(result.Value.Html, Does.Contain("next line</pre>"));
        Assert.That(result.Value.Images, Is.Empty);
    }

    [Test]
    public async Task ReadHtmlAsync_WhenCidOccursInDifferentAlternatives_UsesOnlySelectedRelatedScope()
    {
        // Arrange
        string mime = "MIME-Version: 1.0\r\nContent-Type: multipart/alternative; boundary=outer\r\n\r\n" +
            "--outer\r\n" + Related("old", "AQ==") + "\r\n--outer\r\n" + Related("new", "Ag==") +
            "\r\n--outer--\r\n";
        using MemoryStream source = new MemoryStream(Encoding.ASCII.GetBytes(mime));
        MimePipeline pipeline = new MimePipeline(1048576, 100);

        // Act
        Outcome<MimeHtmlDocument> result = await pipeline.ReadHtmlAsync(source, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Html, Does.Contain("new"));
        Assert.That(result.Value.Html, Does.Not.Contain("old"));
        Assert.That(result.Value.Images, Has.Length.EqualTo(1));
        Assert.That(result.Value.Images[0].ContentId, Is.EqualTo("shared@synthetic.invalid"));
        Assert.That(result.Value.Images[0].Bytes, Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public async Task ReadHtmlAsync_WhenHtmlExistsOnlyInAttachedMessage_DoesNotRenderAttachment()
    {
        // Arrange
        string mime = "MIME-Version: 1.0\r\nContent-Type: message/rfc822\r\n" +
            "Content-Disposition: attachment; filename=attached.eml\r\n\r\n" +
            "MIME-Version: 1.0\r\nContent-Type: text/html\r\n\r\n<p>Attached private content</p>";
        using MemoryStream source = new MemoryStream(Encoding.ASCII.GetBytes(mime));

        // Act
        Outcome<MimeHtmlDocument> result = await new MimePipeline(1048576, 100).ReadHtmlAsync(source, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.NotFound));
    }

    [Test]
    public async Task ReadHtmlAsync_WhenHtmlDecodeExceedsBound_RejectsDocument()
    {
        // Arrange
        string mime = "MIME-Version: 1.0\r\nContent-Type: text/html\r\n\r\n" + new string('x', 262145);
        using MemoryStream source = new MemoryStream(Encoding.ASCII.GetBytes(mime));

        // Act
        Outcome<MimeHtmlDocument> result = await new MimePipeline(1048576, 100).ReadHtmlAsync(source, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
    }

    private static string Related(string text, string bytes)
    {
        return "Content-Type: multipart/related; boundary=" + text + "\r\n\r\n--" + text +
            "\r\nContent-Type: text/html\r\n\r\n<p>" + text + "</p><img src='cid:shared@synthetic.invalid'>\r\n--" + text +
            "\r\nContent-Type: image/png\r\nContent-ID: <shared@synthetic.invalid>\r\n" +
            "Content-Transfer-Encoding: base64\r\n\r\n" + bytes + "\r\n--" + text + "--\r\n";
    }
}
