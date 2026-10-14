using Avalonia;
using Avalonia.Headless;
using Postfold.Desktop;

[assembly: AvaloniaTestApplication(typeof(Postfold.Foundation.Tests.Helpers.TestAppBuilder))]

namespace Postfold.Foundation.Tests.Helpers;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure(() => new App
        {
            ReduceMotion = false
        }).UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
