using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Postfold.Desktop;

[assembly: AvaloniaTestApplication(typeof(Postfold.Foundation.Tests.TestAppBuilder))]

namespace Postfold.Foundation.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
