using NUnit.Framework;
using System;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Postfold.Foundation.Tests.Tests.Desktop;

public sealed class ApplicationManifestTests
{
    [Test]
    [TestCase("Postfold.Desktop", "Postfold.exe")]
    [TestCase("Postfold.Foundation.Tests", "Postfold.Foundation.Tests.exe")]
    public void Executable_WhenBuiltForWindows_EmbedsCompatibilityForNativeChildWindows(string project, string filename)
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows executable resources require a Windows apphost build.");
            return;
        }
        DirectoryInfo? root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Postfold.slnx")))
        {
            root = root.Parent;
        }
        Assert.That(root, Is.Not.Null);
        string output = Path.GetRelativePath(
            Path.Combine(root!.FullName, "Postfold.Foundation.Tests"),
            TestContext.CurrentContext.TestDirectory
        );
        string executable = Path.Combine(root.FullName, project, output, filename);
        using FileStream file = File.OpenRead(executable);
        using PEReader reader = new PEReader(file);

        // Act
        DirectoryEntry directory = reader.PEHeaders.PEHeader!.ResourceTableDirectory;
        string resources = Encoding.UTF8.GetString(reader.GetSectionData(directory.RelativeVirtualAddress)
            .GetContent(0, directory.Size).AsSpan());

        // Assert
        Assert.That(resources, Does.Contain("urn:schemas-microsoft-com:compatibility.v1"));
        Assert.That(resources, Does.Contain("<supportedOS Id=\"{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}\""));
        Assert.That(resources, Does.Contain("requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\""));
    }
}
