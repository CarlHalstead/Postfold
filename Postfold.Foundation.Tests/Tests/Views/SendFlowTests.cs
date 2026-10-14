using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Outbox;
using Postfold.UI.Resources;
using Postfold.UI.Views;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Views;

public sealed class SendFlowTests
{
    [AvaloniaTest]
    [TestCase(SubmissionState.Queued)]
    [TestCase(SubmissionState.Uncertain)]
    [TestCase(SubmissionState.Rejected)]
    [TestCase(SubmissionState.PartiallyAccepted)]
    [TestCase(SubmissionState.SentCopyPending)]
    public async Task SendAsync_WhenDeliveryNeedsAttention_PreservesOneFrozenMessageAndShowsTruthfulStatus(SubmissionState state)
    {
        // Arrange
        AccountId account = new AccountId(1);
        IMailApplication application = Substitute.For<IMailApplication>();
        IClientCatalog catalog = Substitute.For<IClientCatalog>();
        application.Catalog.Returns(catalog);
        ComposeContent content = new ComposeContent(
            "sender@synthetic.invalid",
            ["recipient@synthetic.invalid"],
            "Synthetic send",
            "Preserved text",
            DateTimeOffset.UnixEpoch,
            "synthetic@synthetic.invalid"
        );
        FrozenSubmission frozen = new FrozenSubmission(
            Guid.NewGuid(),
            account,
            new BlobReference(Guid.NewGuid(), account, 10, "synthetic", true),
            content.Sender,
            content.Recipients
        );
        content = content with
        {
            Attachments = [new ComposeAttachment("preserved.txt", "text/plain", frozen.Mime)]
        };
        application.SaveDraftAsync(account, Arg.Any<Guid>(), Arg.Any<ComposeContent>(), Arg.Any<CancellationToken>())
            .Returns(Outcome<DraftSnapshot>.Success(new DraftSnapshot(
                Guid.NewGuid(),
                account,
                1,
                0,
                0,
                frozen.Mime,
                null,
                null,
                false,
                false
            )));
        application.SendAsync(account, Arg.Any<ComposeContent>(), Arg.Any<Guid>(), null, Arg.Any<CancellationToken>())
            .Returns(Outcome<FrozenSubmission>.Success(frozen));
        application.SubmitAsync(account, frozen.Id, Arg.Any<CancellationToken>()).Returns(Failure.Unavailable);
        catalog.OutboxAsync(account, Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create(new OutboxEntry(frozen, state, 1, null)));
        ComposeWindow window = new ComposeWindow(application, account, content);
        window.Show();
        try
        {
            // Act
            await window.QueueAsync();
            await window.QueueAsync();

            // Assert
            Assert.That(window.IsVisible, Is.True);
            Assert.That(window.GetVisualDescendants().OfType<TextBox>().All(field => field.IsReadOnly || !field.IsEffectivelyEnabled), Is.True);
            Assert.That(window.GetVisualDescendants().OfType<Button>().Where(button => button.Name is "Attach" or "RemoveAttachment")
                .All(button => !button.IsEffectivelyEnabled), Is.True);
            Assert.That(window.GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.Text?.Contains(Strings.Get("SendStatus." + state), StringComparison.Ordinal) == true), Is.True);
            Button send = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Send");
            Assert.That(((TextBlock)send.Content!).Text, Is.EqualTo("Send"));
            Assert.That(send.IsVisible, Is.EqualTo(state == SubmissionState.Queued));
            await application.Received(1).SendAsync(account, Arg.Any<ComposeContent>(), Arg.Any<Guid>(), null, Arg.Any<CancellationToken>());
            await application.Received(state == SubmissionState.Queued ? 2 : 1)
                .SubmitAsync(account, frozen.Id, Arg.Any<CancellationToken>());
        }
        finally
        {
            window.ConfirmShutdown();
            window.Close();
        }
    }
}
