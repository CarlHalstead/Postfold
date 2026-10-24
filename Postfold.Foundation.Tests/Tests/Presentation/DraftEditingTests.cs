using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Desktop.Composition;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using Postfold.UI.Controls;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Presentation;

public sealed class DraftEditingTests
{
    [AvaloniaTest]
    public async Task EditDraft_WhenClosedAndReopened_ContinuesTheSameDraftAndSendsEditedContent()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        await using MainWindowViewModel model = new MainWindowViewModel(runtime.Application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow { DataContext = model };
        main.Configure(runtime.Application, null);
        main.Show();
        try
        {
            main.OpenCompose();
            ComposeWindow original = main.OwnedWindows.OfType<ComposeWindow>().Single();
            Control<AutoCompleteBox>(original, "Recipients").Text = "recipient@synthetic.invalid";
            Control<TextBox>(original, "Subject").Text = "Original subject";
            Control<TextBox>(original, "Body").Text = "Original contents";
            Assert.That(await original.PrepareShutdownAsync(), Is.True);
            await runtime.Application.WaitForDraftSyncAsync(original.Account, original.DraftId, CancellationToken.None);
            provider.Messages["draft-message-1"] = ["DRAFT", "UNREAD"];
            provider.History = "{\"history\":[{\"id\":\"121\",\"messagesAdded\":[{\"message\":{\"id\":\"draft-message-1\"}}]}],\"historyId\":\"121\"}";
            original.ConfirmShutdown();
            original.Close();
            await main.WaitForActionsAsync();
            MailboxSnapshot drafts = model.Mailboxes.Single(item => item.Role == MailboxRole.Drafts);
            model.Scope = model.Scopes.Single(item => item.Account == drafts.Account && item.Folder == drafts.Id);
            await model.RefreshAsync(false);
            await model.OpenMessageAsync(model.Messages.Single());

            // Act
            main.FindControl<Button>("EditDraftButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();
            await main.EditSelectedDraftAsync();
            ComposeWindow editing = main.OwnedWindows.OfType<ComposeWindow>().Single();

            // Assert
            Assert.That(editing.DraftId, Is.EqualTo(original.DraftId));
            Assert.That(Control<TextBox>(editing, "Subject").Text, Is.EqualTo("Original subject"));
            Assert.That(Control<TextBox>(editing, "Body").Text, Is.EqualTo("Original contents"));
            Assert.That(Control<AutoCompleteBox>(editing, "Recipients").Text, Is.EqualTo("recipient@synthetic.invalid"));
            Control<TextBox>(editing, "Body").Text = "Edited contents";
            Assert.That(await editing.PrepareShutdownAsync(), Is.True);
            await runtime.Application.WaitForDraftSyncAsync(editing.Account, editing.DraftId, CancellationToken.None);
            provider.Messages.Remove("draft-message-1");
            provider.Messages["updated-message-1"] = ["DRAFT"];
            provider.History = "{\"history\":[{\"id\":\"122\",\"messagesDeleted\":[{\"message\":{\"id\":\"draft-message-1\"}}],\"messagesAdded\":[{\"message\":{\"id\":\"updated-message-1\"}}]}],\"historyId\":\"122\"}";
            editing.ConfirmShutdown();
            editing.Close();
            await main.WaitForActionsAsync();
            await model.OpenMessageAsync(model.Messages.Single());
            Assert.That((await runtime.Application.FindDraftAsync(new ProviderIdentity(drafts.Account, "draft-message-1", null), CancellationToken.None)).Failure, Is.EqualTo(Failure.NotFound));
            main.FindControl<ListBox>("MessageList")!.Focus();
            main.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await main.WaitForActionsAsync();
            ComposeWindow sending = main.OwnedWindows.OfType<ComposeWindow>().Single();
            Assert.That(sending, Is.Not.SameAs(editing));
            Assert.That(sending.DraftId, Is.EqualTo(original.DraftId));
            Assert.That(Control<TextBox>(sending, "Body").Text, Is.EqualTo("Edited contents"));
            DraftSnapshot current = (await runtime.Catalog.DraftsAsync(original.Account, CancellationToken.None)).Single();
            Assert.That(current.VersionCount, Is.EqualTo(2));
            Assert.That(current.Remote!.Key, Is.EqualTo("draft-1"));
            Assert.That(provider.DraftWrites, Is.EqualTo(1));
            Assert.That(provider.DraftUpdates, Is.EqualTo(1));
            Control<Button>(sending, "Send").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FormControls.DrainAsync(main);
            Assert.That(provider.Sends, Is.EqualTo(1), string.Join(" | ", sending.GetVisualDescendants().OfType<TextBlock>().Select(item => item.Text)));
            string raw = provider.SentRaw!.Replace('-', '+').Replace('_', '/');
            string mime = Encoding.UTF8.GetString(Convert.FromBase64String(raw.PadRight((raw.Length + 3) / 4 * 4, '=')));
            Assert.That(mime, Does.Contain("Edited contents"));
        }
        finally
        {
            await main.FlushComposeAsync();
            main.Close();
            await FormControls.DrainAsync(main);
        }
    }

    [AvaloniaTest]
    [TestCase(1600)]
    [TestCase(900)]
    public async Task DraftContext_WhenToolbarReflows_HidesReceivedMailActionsAndRestoresThemInInbox(int width)
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        provider.Messages["one"] = ["DRAFT", "UNREAD"];
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        await using MainWindowViewModel model = new MainWindowViewModel(runtime.Application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow { DataContext = model, Width = width };
        main.Configure(runtime.Application, null);
        main.Show();
        try
        {
            // Act
            MailboxSnapshot drafts = model.Mailboxes.Single(item => item.Role == MailboxRole.Drafts);
            model.Scope = model.Scopes.Single(item => item.Account == drafts.Account && item.Folder == drafts.Id);
            await model.RefreshAsync(false);
            await model.OpenMessageAsync(model.Messages.Single());
            main.UpdateLayout();
            ListBox list = main.FindControl<ListBox>("MessageList")!;
            ListBoxItem target = (ListBoxItem)list.ContainerFromIndex(0)!;
            Point contextPoint = target.TranslatePoint(new Point(80, target.Bounds.Height / 2), main)!.Value;
            main.MouseDown(contextPoint, MouseButton.Right);
            main.MouseUp(contextPoint, MouseButton.Right);
            main.UpdateLayout();
            ContextMenu menu = list.ContextMenu!;
            Assert.That(menu.IsOpen, Is.True);

            // Assert
            foreach (string name in new[] { "ReplyButton", "ReplyAllButton", "ForwardButton", "ReadButton" })
            {
                Assert.That(main.FindControl<Button>(name)!.IsVisible, Is.False, name);
            }
            foreach (string name in new[] { "OverflowReplyAllButton", "OverflowForwardButton", "OverflowReadButton", "MessageReplyMenu", "MessageForwardMenu", "MessageReadMenu" })
            {
                Assert.That(main.FindControl<MenuItem>(name)!.IsVisible, Is.False, name);
            }
            Assert.That(main.FindControl<Button>("EditDraftButton")!.IsVisible, Is.True);
            Assert.That(AutomationProperties.GetName(main.FindControl<Button>("TrashButton")!), Is.EqualTo(Strings.Get("DeleteDraft")));
            Assert.That(main.FindControl<MenuItem>("MessageTrashMenu")!.Header, Is.EqualTo(Strings.Get("DeleteDraft")));
            Assert.That(model.ReadCommand.CanExecute(null), Is.False);
            Assert.That(provider.Messages["one"], Does.Contain("UNREAD"));
            menu.Close();
            model.Scope = model.Scopes.Single(item => item.Account == drafts.Account && item.Folder == model.Mailboxes.Single(box => box.Role == MailboxRole.Inbox).Id);
            await model.RefreshAsync(false);
            await model.OpenMessageAsync(model.Messages.Single());
            main.UpdateLayout();
            Assert.That(main.FindControl<Button>("EditDraftButton")!.IsVisible, Is.False);
            Assert.That(main.FindControl<Button>("ReplyButton")!.IsVisible, Is.True);
            Assert.That(AutomationProperties.GetName(main.FindControl<Button>("TrashButton")!), Is.EqualTo(Strings.Trash));
        }
        finally
        {
            await main.FlushComposeAsync();
            main.Close();
            await FormControls.DrainAsync(main);
        }
    }

    private static TControl Control<TControl>(Window window, string name) where TControl : Avalonia.Controls.Control
    {
        return window.GetVisualDescendants().OfType<TControl>().Single(item => item.Name == name);
    }
}
