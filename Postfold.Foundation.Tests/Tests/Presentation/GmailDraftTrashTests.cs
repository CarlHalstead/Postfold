using NUnit.Framework;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Search;
using Postfold.Desktop.Composition;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using Postfold.UI.ViewModels;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Presentation;

public sealed class GmailDraftTrashTests
{
    [Test]
    public async Task TrashCommand_WhenDraftLabelRemains_RemovesDraftImmediatelyAndAfterHistorySyncAndRestart()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        provider.Messages["one"] = ["DRAFT", "UNREAD"];
        await using (DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(directory, provider))
        {
            await using MainWindowViewModel model = new MainWindowViewModel(runtime.Application);
            await model.InitialiseAsync();
            await model.SynchroniseAccountAsync(EngineFixture.Account);
            MailboxSnapshot drafts = model.Mailboxes.Single(item => item.RemoteKey == "DRAFT");
            model.Scope = model.Scopes.Single(item => item.Account == drafts.Account && item.Folder == drafts.Id);
            await model.RefreshAsync(false);
            await model.OpenMessageAsync(model.Messages.Single());

            // Act
            await model.TrashCommand.ExecuteAsync(null);

            // Assert
            Assert.That(provider.Messages["one"], Does.Contain("TRASH"));
            Assert.That(provider.Messages["one"], Does.Contain("DRAFT"));
            Assert.That(model.Messages, Is.Empty);
            provider.History = "{\"history\":[{\"id\":\"121\",\"labelsAdded\":[{\"message\":{\"id\":\"one\"},\"labelIds\":[\"TRASH\"]}]}],\"historyId\":\"121\"}";
            await model.SynchroniseAccountAsync(drafts.Account);
            await model.RefreshAsync(false);
            Assert.That(model.Messages, Is.Empty);
            Assert.That((await runtime.Catalog.MailboxUnreadCountsAsync(drafts.Account, CancellationToken.None)).Single(item => item.Folder == drafts.Id).Count, Is.Zero);
        }
        await using DesktopMailRuntime reopened = await GmailDraftReviewFixture.OpenAsync(directory, provider);
        MailboxSnapshot draftFolder = (await reopened.Catalog.MailboxesAsync(CancellationToken.None)).Single(item => item.RemoteKey == "DRAFT");
        MailboxSnapshot trashFolder = (await reopened.Catalog.MailboxesAsync(CancellationToken.None)).Single(item => item.RemoteKey == "TRASH");
        Assert.That((await reopened.Application.SearchAsync(new SearchRequest(draftFolder.Account, false, "", draftFolder.Id, false, 10), CancellationToken.None)).Value!.Messages, Is.Empty);
        Assert.That((await reopened.Application.SearchAsync(new SearchRequest(trashFolder.Account, false, "", trashFolder.Id, false, 10), CancellationToken.None)).Value!.Messages.Select(item => item.Provider.Key), Is.EqualTo(new[] { "one" }));
        Assert.That(provider.Sends + provider.DraftWrites + provider.DraftDeletes, Is.Zero);
    }
}
