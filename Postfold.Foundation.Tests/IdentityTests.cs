using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Engine.Tests;
using Postfold.Storage;
using Postfold.Sync;

namespace Postfold.Foundation.Tests;

public sealed class IdentityTests
{
    [Test]
    public async Task SaveAsync_WhenPrimaryIdentityChanges_PersistsOnlyForItsAccountAcrossRestart()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "login-name", "fixture");
        await profile.ConfigureAccountAsync(EngineFixture.Account, endpoint, endpoint, CancellationToken.None);
        await profile.ConfigureAccountAsync(new AccountId(2), endpoint, endpoint, CancellationToken.None);
        ClientIdentity identity = new ClientIdentity(profile);
        ComposeIdentity primary = new ComposeIdentity("sender@synthetic.invalid", "Private plain-text signature");

        // Act
        Failure saved = await identity.SaveAsync(EngineFixture.Account, primary, CancellationToken.None);
        Failure rejected = await identity.SaveAsync(EngineFixture.Account,
            new ComposeIdentity("sender@synthetic.invalid\r\nBcc: victim@synthetic.invalid", ""), CancellationToken.None);
        await using SqliteProfile restarted = (await SqliteProfile.OpenAsync(System.IO.Path.Combine(directory, "profile.db"),
            TimeProvider.System, CancellationToken.None)).Value!;
        AccountConfiguration[] accounts = (await restarted.AccountsAsync(CancellationToken.None)).ToArray();

        // Assert
        Assert.That(saved, Is.EqualTo(Failure.None));
        Assert.That(rejected, Is.EqualTo(Failure.Validation));
        Assert.That(accounts.Single(account => account.Id == EngineFixture.Account).Identity, Is.EqualTo(primary));
        Assert.That(accounts.Single(account => account.Id == new AccountId(2)).Identity, Is.Null);
        Assert.That(accounts[0].Outgoing.Username, Is.EqualTo("login-name"));
    }
}
