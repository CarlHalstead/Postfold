using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Protocols;

namespace Postfold.Foundation.Tests;

public sealed class ReplyAddressTests
{
    [Test]
    [TestCase("Reply-To: Reply target <reply@synthetic.invalid>\r\n", "reply@synthetic.invalid")]
    [TestCase("", "sender@synthetic.invalid")]
    public async Task ParseAsync_WhenReplyTargetDiffersFromDisplayedSender_UsesReplyToOrFromAddress(
        string replyHeader, string expected)
    {
        // Arrange
        string text = "From: Displayed sender <sender@synthetic.invalid>\r\n" + replyHeader +
            "Subject: Reply fixture\r\nContent-Type: text/plain\r\n\r\nQuoted text\r\n";
        using MemoryStream source = new MemoryStream(Encoding.UTF8.GetBytes(text));
        MimePipeline mime = new MimePipeline(1048576, 100);

        // Act
        Outcome<MimeSnapshot> result = await mime.ParseAsync(source, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.ReplyRecipients, Is.EqualTo(new[] { expected }));
        Assert.That(result.Value.Participants, Does.Contain("Displayed sender"));
    }
}
