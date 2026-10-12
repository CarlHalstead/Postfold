using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Platform.Storage;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core;
using Postfold.UI;

namespace Postfold.Foundation.Tests;

public sealed class AttachmentSaveWindowTests
{
    [AvaloniaTest]
    public async Task SaveAttachmentAsync_WhenCancelledBeforeDestinationOpens_DoesNotTruncateOrRetrieve()
    {
        // Arrange
        IMailApplication application = Application();
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        model.Selected = Message(1);
        MainWindow window = new MainWindow { DataContext = model };
        window.Configure(application, null);
        window.Show();
        using MemoryStream destination = new MemoryStream(new byte[] { 1, 2, 3 });
        TaskCompletionSource<Stream> opened = new TaskCompletionSource<Stream>();
        IStorageFile file = Substitute.For<IStorageFile>();
        file.OpenWriteAsync().Returns(opened.Task);
        IStorageProvider picker = Substitute.For<IStorageProvider>();
        picker.SaveFilePickerAsync(Arg.Any<FilePickerSaveOptions>()).Returns(file);
        SelectAttachment(window, "report.txt");
        try
        {
            // Act
            Task saving = window.SaveAttachmentAsync(picker);
            window.OwnedWindows.Single().Close();
            opened.SetResult(destination);
            await saving;

            // Assert
            Assert.That(destination.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            await application.DidNotReceiveWithAnyArgs().SaveAttachmentAsync(default!, default!, default!, default);
            file.Received(1).Dispose();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    [TestCase("../../report.txt", "report.txt")]
    [TestCase("..\\..\\report.txt", "report.txt")]
    [TestCase("NUL.txt", "attachment-NUL.txt")]
    [TestCase("..", "attachment")]
    [TestCase("bad\u0001name?.txt", "badname.txt")]
    public async Task SaveAttachmentAsync_WhenUserSelectsDestination_StreamsOnlySelectedPartWithSafeSuggestedName(
        string hostileName, string expectedName)
    {
        // Arrange
        IMailApplication application = Application();
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        MessageSnapshot message = Message(1);
        model.Selected = message;
        MainWindow window = new MainWindow { DataContext = model };
        window.Configure(application, null);
        window.Show();
        using MemoryStream destination = new MemoryStream([1, 2, 3, 4]);
        IStorageFile file = Substitute.For<IStorageFile>();
        file.OpenWriteAsync().Returns(destination);
        IStorageProvider picker = Substitute.For<IStorageProvider>();
        picker.SaveFilePickerAsync(Arg.Any<FilePickerSaveOptions>()).Returns(file);
        application.SaveAttachmentAsync(message, "2", destination, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await call.Arg<Stream>().WriteAsync(new byte[] { 7, 8 }, call.Arg<CancellationToken>());
            return Failure.None;
        });
        SelectAttachment(window, hostileName);
        try
        {
            // Act
            await window.SaveAttachmentAsync(picker);

            // Assert
            await picker.Received(1).SaveFilePickerAsync(Arg.Is<FilePickerSaveOptions>(options =>
                options.SuggestedFileName == expectedName));
            await file.Received(1).OpenWriteAsync();
            await application.Received(1).SaveAttachmentAsync(message, "2", destination, Arg.Any<CancellationToken>());
            Assert.That(destination.ToArray(), Is.EqualTo(new byte[] { 7, 8 }));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task SaveAttachmentAsync_WhenPickerIsCancelledOrSelectionChanges_DoesNotOpenOrRetrieve(bool changeSelection)
    {
        // Arrange
        IMailApplication application = Application();
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        model.Selected = Message(1);
        MainWindow window = new MainWindow { DataContext = model };
        window.Configure(application, null);
        window.Show();
        IStorageFile file = Substitute.For<IStorageFile>();
        IStorageProvider picker = Substitute.For<IStorageProvider>();
        picker.SaveFilePickerAsync(Arg.Any<FilePickerSaveOptions>()).Returns(_ =>
        {
            if (changeSelection)
            {
                model.Selected = Message(2);
                return Task.FromResult<IStorageFile?>(file);
            }

            return Task.FromResult<IStorageFile?>(null);
        });
        SelectAttachment(window, "report.txt");
        try
        {
            // Act
            await window.SaveAttachmentAsync(picker);

            // Assert
            await file.DidNotReceive().OpenWriteAsync();
            await application.DidNotReceiveWithAnyArgs().SaveAttachmentAsync(default!, default!, default!, default);
        }
        finally
        {
            window.Close();
        }
    }

    private static void SelectAttachment(MainWindow window, string name)
    {
        ListBox list = window.FindControl<ListBox>("AttachmentList")!;
        AttachmentSnapshot attachment = new AttachmentSnapshot("2", name, "application/octet-stream", 2);
        list.ItemsSource = new[] { attachment };
        list.SelectedItem = attachment;
    }

    private static IMailApplication Application()
    {
        IMailApplication application = Substitute.For<IMailApplication>();
        application.ReadAsync(Arg.Any<MessageSnapshot>(), false, Arg.Any<CancellationToken>()).Returns(
            Outcome<MimeSnapshot>.Fail(Failure.NotFound));
        application.ReadAttachmentsAsync(Arg.Any<MessageSnapshot>(), false, Arg.Any<CancellationToken>()).Returns(
            Outcome<ImmutableArray<AttachmentSnapshot>>.Success([]));
        return application;
    }

    private static MessageSnapshot Message(long id)
    {
        AccountId account = new AccountId(1);
        return new MessageSnapshot(account, new MessageId(id), new ProviderIdentity(account, id.ToString(), null),
            "Synthetic", "fixture@synthetic.invalid", null, DateTimeOffset.UnixEpoch, 1, false, false,
            ContentAvailability.Available, PendingState.None);
    }
}
