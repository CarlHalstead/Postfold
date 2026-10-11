using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Desktop;

namespace Postfold.Engine.Tests;

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
    public async Task ReadAsync_WhenCancelled_PropagatesCancellationRatherThanTypedFailure()
    {
        // Arrange
        SessionCredentialStore store = new SessionCredentialStore();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await

                // Act
                Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () =>
            await store.ReadAsync("synthetic", cancellation.Token));
        Outcome<Credential> missing = await store.ReadAsync("synthetic", CancellationToken.None);

        // Assert
        Assert.That(missing.Failure, Is.EqualTo(Failure.NotFound));
        Assert.That(missing.Value, Is.Null);
    }
}
