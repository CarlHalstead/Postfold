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
