using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Postfold.UI.Resources;

/// <summary>Opaque high-contrast resources use the native Fluent controls and preserve visible state distinctions.</summary>
public static class MailTheme
{
    public static readonly ThemeVariant HighContrast = new ThemeVariant("PostfoldHighContrast", ThemeVariant.Dark);

    public static void Install(Application application)
    {
        application.Resources.ThemeDictionaries[ThemeVariant.Light] = Palette("#FFFFFF", "#F1F5FA", "#0075FF", "#FFFFFF", "#E6F0FF", "#D7DEE8", "#526079", "#FFFFFF", "#151B26");
        application.Resources.ThemeDictionaries[ThemeVariant.Dark] = Palette("#171B22", "#1F2530", "#438EFF", "#FFFFFF", "#243F65", "#4D596B", "#BDC7D8", "#222A36", "#F4F6FB");
        ResourceDictionary contrast = Palette("#000000", "#000000", "#FFFF00", "#000000", "#003A70", "#FFFFFF", "#FFFFFF", "#000000", "#FFFFFF");
        foreach (string key in new[]
        {
            "SystemControlForegroundBaseHighBrush", "SystemControlForegroundBaseMediumBrush",
            "SystemControlForegroundBaseMediumLowBrush", "SystemControlForegroundBaseLowBrush"
        })
        {
            contrast[key] = new SolidColorBrush(Colors.White);
        }
        contrast["SystemControlBackgroundAltHighBrush"] = new SolidColorBrush(Colors.Black);
        contrast["SystemControlBackgroundChromeMediumLowBrush"] = new SolidColorBrush(Colors.Black);
        contrast["SystemControlHighlightAccentBrush"] = new SolidColorBrush(Colors.Yellow);
        contrast["SystemControlHighlightAltBaseHighBrush"] = new SolidColorBrush(Colors.White);
        contrast["SystemControlHighlightListAccentLowBrush"] = new SolidColorBrush(Color.Parse("#003A70"));
        application.Resources.ThemeDictionaries[HighContrast] = contrast;
    }

    public static ThemeVariant Resolve(string theme, IPlatformSettings? platform)
    {
        if (platform?.GetColorValues().ContrastPreference == ColorContrastPreference.High)
        {
            return HighContrast;
        }
        return theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private static ResourceDictionary Palette(params string[] colours)
    {
        ResourceDictionary resources = new ResourceDictionary();
        string[] keys = ["MailSurfaceBrush", "MailNavigationBrush", "MailAccentBrush", "MailAccentTextBrush",
            "MailSelectionBrush", "MailLineBrush", "MailMetadataBrush", "MailButtonBrush", "MailTextBrush"];
        for (int index = 0; index < keys.Length; index++)
        {
            resources[keys[index]] = new SolidColorBrush(Color.Parse(colours[index]));
        }
        resources["SystemControlHighlightAccentBrush"] = resources["MailAccentBrush"];
        return resources;
    }

}
