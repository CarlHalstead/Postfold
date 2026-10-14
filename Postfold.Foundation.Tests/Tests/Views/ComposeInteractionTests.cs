using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.UI.Resources;
using Postfold.UI.Views;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Views;

public sealed class ComposeInteractionTests
{
    [AvaloniaTest]
    public async Task RemoveAttachment_WhenItsCloseButtonIsClicked_RemovesOnlyThatAttachmentAndSavesDraft()
    {
        // Arrange
        AccountId account = new AccountId(1);
        BlobReference blob = new BlobReference(Guid.NewGuid(), account, 20, "synthetic", true);
        ComposeAttachment first = new ComposeAttachment("first.pdf", "application/pdf", blob);
        ComposeAttachment second = new ComposeAttachment("second.txt", "text/plain", blob with
        {
            Id = Guid.NewGuid()
        });
        IMailApplication application = Substitute.For<IMailApplication>();
        application.Catalog.Returns(Substitute.For<IClientCatalog>());
        application.SaveDraftAsync(account, Arg.Any<Guid>(), Arg.Any<ComposeContent>(), Arg.Any<CancellationToken>())
            .Returns(Outcome<DraftSnapshot>.Success(new DraftSnapshot(Guid.NewGuid(), account, 1, 0, 0, blob, null, null, false, false)));
        ComposeContent content = new ComposeContent(
            "owner@synthetic.invalid",
            [],
            "Subject",
            "Body",
            DateTimeOffset.UnixEpoch,
            "synthetic@synthetic.invalid",
            [first, second]
        );
        ComposeWindow window = new ComposeWindow(application, account, content);
        window.Show();
        try
        {
            window.UpdateLayout();
            Button remove = window.GetVisualDescendants().OfType<Button>().Single(button =>
                Avalonia.Automation.AutomationProperties.GetName(button) == Strings.Get("RemoveAttachment") + " first.pdf");

            // Act
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.PrepareShutdownAsync();

            // Assert
            ItemsControl attachments = window.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == "ComposeAttachments");
            Assert.That(attachments.Items.Cast<ComposeAttachment>(), Is.EqualTo(new[] { second }));
            Assert.That(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Strings.Get("ComposeAttachments")), Is.True);
            Assert.That(
                window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Attach").Content,
                Is.TypeOf<Avalonia.Controls.Shapes.Path>()
            );
            await application.Received().SaveDraftAsync(
                account,
                Arg.Any<Guid>(),
                Arg.Is<ComposeContent>(saved => saved.Attachments.Length == 1 && saved.Attachments[0] == second),
                Arg.Any<CancellationToken>()
            );
        }
        finally
        {
            window.ConfirmShutdown();
            window.Close();
        }
    }

    [AvaloniaTest]
    [TestCase("fr", "friend@synthetic.invalid", false)]
    [TestCase("first@synthetic.invalid; fr", "first@synthetic.invalid; friend@synthetic.invalid", false)]
    [TestCase("fr; first@synthetic.invalid", "friend@synthetic.invalid; first@synthetic.invalid", true)]
    public async Task Compose_WhenARecipientIsSuggested_PreservesOtherRecipientsAndUsesTheKnownSender(
        string input,
        string completed,
        bool editFirst
    )
    {
        // Arrange
        AccountId account = new AccountId(1);
        IMailApplication application = Substitute.For<IMailApplication>();
        IClientCatalog catalog = Substitute.For<IClientCatalog>();
        application.Catalog.Returns(catalog);
        catalog.RecognisedRecipientsAsync(account, "fr", Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("friend@synthetic.invalid", "first@synthetic.invalid"));
        application.SaveDraftAsync(account, Arg.Any<Guid>(), Arg.Any<ComposeContent>(), Arg.Any<CancellationToken>())
            .Returns(Outcome<DraftSnapshot>.Fail(Failure.Unavailable));
        ComposeContent content = new ComposeContent(
            "owner@synthetic.invalid",
            [],
            "Subject",
            "Body",
            DateTimeOffset.UnixEpoch,
            "synthetic@synthetic.invalid"
        );
        ComposeWindow window = new ComposeWindow(application, account, content);
        window.Show();
        try
        {
            AutoCompleteBox field = window.GetVisualDescendants().OfType<AutoCompleteBox>().Single();
            field.Text = input;
            TextBox editor = field.GetVisualDescendants().OfType<TextBox>().First();
            editor.CaretIndex = editFirst ? 2 : input.Length;

            // Act
            IEnumerable<object> suggestions = await field.AsyncPopulator!(input, CancellationToken.None);
            field.Text = field.TextSelector!(input, "alternate@synthetic.invalid");
            field.Text = field.TextSelector!(input, "friend@synthetic.invalid");
            await window.FlushAsync();

            // Assert
            Assert.That(field.Text, Is.EqualTo(completed));
            Assert.That(suggestions, Does.Contain("friend@synthetic.invalid"));
            if (input.Contains(';'))
            {
                Assert.That(suggestions, Does.Not.Contain("first@synthetic.invalid"));
            }
            Assert.That(window.GetVisualDescendants().OfType<TextBox>().Any(text => text.Name is "Sender" or "Signature"), Is.False);
            Assert.That(window.GetVisualDescendants().OfType<Expander>(), Is.Empty);
            await application.Received().SaveDraftAsync(
                account,
                Arg.Any<Guid>(),
                Arg.Is<ComposeContent>(saved => saved.Sender == "owner@synthetic.invalid" &&
                    saved.Recipients.SequenceEqual(completed.Split(';', StringSplitOptions.TrimEntries))),
                Arg.Any<CancellationToken>()
            );
        }
        finally
        {
            window.BeginShutdown();
            await window.PrepareShutdownAsync();
            window.ConfirmShutdown();
            window.Close();
        }
    }
}
