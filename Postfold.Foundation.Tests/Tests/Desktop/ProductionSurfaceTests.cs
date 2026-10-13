using NUnit.Framework;
using Postfold.Desktop;
using Postfold.Desktop.Composition;
using System;
using System.Linq;

namespace Postfold.Foundation.Tests.Tests.Desktop;

public sealed class ProductionSurfaceTests
{
    [Test]
    public void DesktopAssembly_WhenBuiltForNormalUse_ContainsNoQualificationOrPerformanceHarness()
    {
        // Arrange
        Type[] types = typeof(Program).Assembly.GetTypes();

        // Act
        string[] diagnostics = types.Where(type => type.Namespace?.Contains("Diagnostics", StringComparison.Ordinal) == true ||
            type.Name.Contains("Qualification", StringComparison.Ordinal) || type.Name.Contains("PerformanceProbe", StringComparison.Ordinal))
            .Select(type => type.FullName!).ToArray();

        // Assert
        Assert.That(diagnostics, Is.Empty);
        Assert.That(typeof(DesktopMailRuntime).GetMethods().Any(method => method.Name.StartsWith("Seed", StringComparison.Ordinal)), Is.False);
    }
}
