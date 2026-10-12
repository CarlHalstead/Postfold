using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.UI.Models;
using Postfold.UI.Policies;
using System;
using System.Collections.Generic;

namespace Postfold.Foundation.Tests.Tests.HtmlReading;

public sealed class HtmlPlanTests
{
    [Test]
    public void Plan_WhenContentContainsActiveAndRemoteResources_ReturnsOnlyInertGeneratedImageReferences()
    {
        // Arrange
        Guid context = Guid.NewGuid();
        string html = "<script>alert(1)</script><base href='file:///C:/'><iframe src='https://tracker.invalid'></iframe>" +
            "<style>@import 'https://tracker.invalid/style';</style><form><input></form>" +
            "<p style='background:url(https://tracker.invalid)' onclick='attack()'>Safe text</p>" +
            "<img src='https://tracker.invalid/pixel' srcset='https://tracker.invalid/second 2x' onerror='attack()'>" +
            "<img src='cid:owned@synthetic.invalid'><img src='cid:other@synthetic.invalid'>" +
            "<img src='file:///C:/private.txt'><img src='data:image/svg+xml,active'>";

        // Act
        Outcome<HtmlRenderPlan> result = HtmlPolicy.Plan(
            html,
            context,
            new Dictionary<string, string>
            {
                ["owned@synthetic.invalid"] = "owned123"
            }
        );

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.RemoteImages, Has.Length.EqualTo(1));
        Assert.That(result.Value.CidResourceIds, Is.EqualTo(new[] { "owned123" }));
        Assert.That(result.Value.Html, Does.Contain("https://postfold.invalid/" + context.ToString("N") + "/owned123"));
        Assert.That(result.Value.Html, Does.Not.Contain("tracker.invalid"));
        Assert.That(result.Value.Html, Does.Not.Contain("onclick"));
        Assert.That(result.Value.Html, Does.Not.Contain("onerror"));
        Assert.That(result.Value.Html, Does.Not.Contain("<script"));
        Assert.That(result.Value.Html, Does.Not.Contain("<iframe"));
        Assert.That(result.Value.Html, Does.Not.Contain("file:"));
        Assert.That(result.Value.Html, Does.Not.Contain("data:image"));
        Assert.That(result.Value.Html, Does.Contain("script-src 'none'"));
    }

    [Test]
    public void Plan_WhenMessageContextChanges_ExpiresGeneratedResourceUrls()
    {
        // Arrange
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Dictionary<string, string> resources = new Dictionary<string, string>
        {
            ["same"] = "image123"
        };

        // Act
        HtmlRenderPlan previous = HtmlPolicy.Plan("<img src='cid:same'>", first, resources).Value!;
        HtmlRenderPlan current = HtmlPolicy.Plan("<img src='cid:same'>", second, resources).Value!;

        // Assert
        Assert.That(previous.Html, Does.Contain(first.ToString("N")));
        Assert.That(current.Html, Does.Contain(second.ToString("N")));
        Assert.That(current.Html, Does.Not.Contain(first.ToString("N")));
    }

    [Test]
    public void Plan_WhenImageCountExceedsBound_RejectsInsteadOfCreatingUnboundedRequests()
    {
        // Arrange
        string html = string.Concat(System.Linq.Enumerable.Repeat("<img src='https://synthetic.invalid/pixel'>", 33));

        // Act
        Outcome<HtmlRenderPlan> result = HtmlPolicy.Plan(html, Guid.NewGuid(), new Dictionary<string, string>());

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
    }
}
