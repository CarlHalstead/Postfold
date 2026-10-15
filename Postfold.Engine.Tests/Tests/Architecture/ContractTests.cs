using NUnit.Framework;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Architecture;

public sealed class ContractTests
{
    [Test]
    public void Identity_WhenAccountMailboxOrGenerationDiffers_IsDistinct()
    {
        // Arrange
        ImapIdentity identity = new ImapIdentity(new AccountId(1), "INBOX", 7, 1);

        // Act
        ImapIdentity[] distinct =
        [
            identity with { Account = new AccountId(2) },
            identity with { Mailbox = "Archive" },
            identity with { UidValidity = 8 },
            identity with { Uid = 2 }
        ];

        // Assert
        Assert.That(distinct.All(item => item != identity), Is.True);
        Assert.That(identity.Account.GetType(), Is.Not.EqualTo(typeof(MessageId)));
    }

    [Test]
    public void PublicContracts_WhenCoreIsLoaded_ReferenceOnlyFrameworkAssemblies()
    {
        // Arrange
        System.Reflection.Assembly core = typeof(IMailboxReader).Assembly;

        // Act
        string[] references = core.GetReferencedAssemblies().Select(name => name.Name!).ToArray();

        // Assert
        Assert.That(references.All(name => name.StartsWith("System.", StringComparison.Ordinal) ||
            name is "System" or "mscorlib" or "netstandard"), Is.True);
        Assert.That(core.GetExportedTypes().Any(type => type.Name.Contains("Sqlite", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task ReadRolesAsync_WhenCancelled_PropagatesCancellationRatherThanTypedFailure()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await
                // Act
                Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () =>
            await profile.ReadRolesAsync(EngineFixture.Account, cancellation.Token));
        FolderRoles? missing = await profile.ReadRolesAsync(EngineFixture.Account, CancellationToken.None);

        // Assert
        Assert.That(missing, Is.Null);
    }
}
