using NUnit.Framework;
using Postfold.Desktop.Models.Updates;
using Postfold.Desktop.Services.Updates;
using System;
using System.Net.NetworkInformation;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class SystemUpdateConnectionTests
{
    [TestCase(3, 1, false, "Unmetered")]
    [TestCase(3, 2, false, "Metered")]
    [TestCase(3, 3, false, "Metered")]
    [TestCase(3, 1, true, "Metered")]
    [TestCase(3, 0, false, "Unknown")]
    [TestCase(3, 4, false, "Unknown")]
    [TestCase(0, 1, false, "Unknown")]
    [TestCase(1, 1, false, "Offline")]
    [TestCase(5, 1, false, "Offline")]
    [TestCase(2, 1, false, "Unmetered")]
    [TestCase(4, 1, false, "Unmetered")]
    public void Interpret_WhenWindowsReportsCost_RequiresExplicitUnrestrictedCost(int level, int cost, bool constrained, string expected)
    {
        // Act
        UpdateConnection actual = SystemUpdateConnection.Interpret(level, cost, constrained);

        // Assert
        Assert.That(actual, Is.EqualTo(Enum.Parse<UpdateConnection>(expected)));
    }

    [Test]
    [Platform("Win")]
    public void Dispose_WhenNativeNotificationsAreRegistered_ReleasesRegistrationAndInvalidatesCost()
    {
        // Arrange
        using SystemUpdateConnection connection = new SystemUpdateConnection();
        TestContext.Progress.WriteLine("Native Windows network cost: " + connection.Current);

        // Act
        connection.Dispose();
        connection.Dispose();

        // Assert
        Assert.That(connection.Current, Is.EqualTo(NetworkInterface.GetIsNetworkAvailable() ? UpdateConnection.Unknown : UpdateConnection.Offline));
    }
}
