using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using NUnit.Framework;

namespace Postfold.Foundation.Tests;

public sealed class ProjectBoundaryTests
{
    [TestCase("Postfold.Core", new string[0])]
    [TestCase("Postfold.Protocols", new[] { "Postfold.Core" })]
    [TestCase("Postfold.Storage", new[] { "Postfold.Core" })]
    [TestCase("Postfold.Sync", new[] { "Postfold.Core" })]
    [TestCase("Postfold.UI", new[] { "Postfold.Core" })]
    [TestCase("Postfold.Desktop", new[]
    {
        "Postfold.Core", "Postfold.Protocols", "Postfold.Storage", "Postfold.Sync", "Postfold.UI"
    })]
    public void ProjectReferences_WhenFoundationIsBuilt_RespectApprovedGraph(string projectName, string[] expected)
    {
        // Arrange
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(root, projectName, projectName + ".csproj");
        XDocument project = XDocument.Load(projectPath);
        using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, projectName, "obj", "project.assets.json")));

        // Act
        string[] references = project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!))
            .ToArray();
        string[] evaluatedReferences = assets.RootElement.GetProperty("project").GetProperty("restore")
            .GetProperty("frameworks").GetProperty("net10.0").GetProperty("projectReferences")
            .EnumerateObject().Select(reference => Path.GetFileNameWithoutExtension(reference.Name)).ToArray();

        // Assert
        Assert.That(references, Is.EquivalentTo(expected));
        Assert.That(evaluatedReferences, Is.EquivalentTo(expected));
    }

    [TestCase("Postfold.Core")]
    [TestCase("Postfold.Sync")]
    public void PackageReferences_WhenFrameworkOnlyLayerIsBuilt_ContainNoExternalDependencies(string projectName)
    {
        // Arrange
        string root = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(root, projectName, projectName + ".csproj"));

        // Act
        IEnumerable<XElement> packages = project.Descendants("PackageReference");

        // Assert
        Assert.That(packages, Is.Empty);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Postfold.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The foundation tests must run from a Postfold source checkout.");
    }
}
