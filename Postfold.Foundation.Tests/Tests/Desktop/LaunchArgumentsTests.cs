using NUnit.Framework;
using Postfold.Desktop;
using System;
using System.IO;

namespace Postfold.Foundation.Tests.Tests.Desktop;

public sealed class LaunchArgumentsTests
{
    [Test]
    public void TryGetLaunchArguments_WhenNoProfileIsSpecified_PreservesDefaultAndActivation()
    {
        // Arrange
        string[] args = ["mailto:recipient@example.invalid"];

        // Act
        bool accepted = Program.TryGetLaunchArguments(args, out string? profile, out string[] activation);

        // Assert
        Assert.That(accepted, Is.True);
        Assert.That(profile, Is.Null);
        Assert.That(activation, Is.EqualTo(args));
    }

    [Test]
    public void TryGetLaunchArguments_WhenExplicitProfileExists_IsolatesProfileAndPreservesActivation()
    {
        // Arrange
        string directory = Path.GetFullPath(TestContext.CurrentContext.WorkDirectory);
        string[] args = ["--profile", directory, "mailto:recipient@example.invalid"];

        // Act
        bool accepted = Program.TryGetLaunchArguments(args, out string? profile, out string[] activation);

        // Assert
        Assert.That(accepted, Is.True);
        Assert.That(profile, Is.EqualTo(directory));
        Assert.That(activation, Is.EqualTo(new[] { "mailto:recipient@example.invalid" }));
    }

    [TestCase("--profile")]
    [TestCase("--demo")]
    [TestCase("--measure-client-auto")]
    public void TryGetLaunchArguments_WhenOptionIsUnsupportedOrIncomplete_RejectsLaunch(string option)
    {
        // Arrange
        string[] args = [option];

        // Act
        bool accepted = Program.TryGetLaunchArguments(args, out _, out _);

        // Assert
        Assert.That(accepted, Is.False);
    }

    [Test]
    public void TryGetLaunchArguments_WhenProfileIsRelativeOrMissing_RejectsLaunch()
    {
        // Arrange
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        // Act
        bool relative = Program.TryGetLaunchArguments(["--profile", "."], out _, out _);
        bool absent = Program.TryGetLaunchArguments(["--profile", missing], out _, out _);

        // Assert
        Assert.That(relative, Is.False);
        Assert.That(absent, Is.False);
    }

    [Test]
    public void TryGetLaunchArguments_WhenProfileIsFollowedByDiagnosticOption_RejectsLaunch()
    {
        // Arrange
        string directory = Path.GetFullPath(TestContext.CurrentContext.WorkDirectory);

        // Act
        bool accepted = Program.TryGetLaunchArguments(["--profile", directory, "--demo"], out _, out _);

        // Assert
        Assert.That(accepted, Is.False);
    }
}
