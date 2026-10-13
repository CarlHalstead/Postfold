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
        application.Resources.ThemeDictionaries[ThemeVariant.Light] = Palette("#FFFFFF", "#F1F5FA", "#0067E6", "#FFFFFF", "#E6F0FF", "#D7DEE8", "#526079", "#FFFFFF", "#151B26");
        application.Resources.ThemeDictionaries[ThemeVariant.Dark] = Palette("#171B22", "#1F2530", "#438EFF", "#101820", "#243F65", "#4D596B", "#BDC7D8", "#222A36", "#F4F6FB");
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
        resources["MailHoverBrush"] = new SolidColorBrush(Color.Parse(colours[0] == "#FFFFFF" ? "#EDF3FA" :
            colours[0] == "#000000" ? "#003A70" : "#283242"));
        resources["MailPressedBrush"] = resources["MailSelectionBrush"];
        resources["MailAvatarBrush"] = new SolidColorBrush(Color.Parse(colours[0] == "#FFFFFF" ? "#EEF1F6" :
            colours[0] == "#000000" ? "#003A70" : "#2E3643"));
        resources["MailAvatarSelectedBrush"] = new SolidColorBrush(Color.Parse(colours[0] == "#FFFFFF" ? "#E4E0FF" :
            colours[0] == "#000000" ? "#003A70" : "#3A3B64"));
        resources["MailFocusBrush"] = resources["MailAccentBrush"];
        resources["MailInputBorderBrush"] = new SolidColorBrush(Color.Parse(colours[0] == "#FFFFFF" ? "#7D8797" :
            colours[0] == "#000000" ? "#FFFFFF" : "#8795AA"));
        resources["MailDisabledBrush"] = resources["MailMetadataBrush"];
        resources["SystemAccentColor"] = Color.Parse(colours[2]);
        resources["SystemAccentColorLight1"] = Color.Parse(colours[2]);
        resources["SystemAccentColorDark1"] = Color.Parse(colours[2]);
        resources["SystemControlHighlightAccentBrush"] = resources["MailAccentBrush"];
        resources["TextControlBorderBrushFocused"] = resources["MailFocusBrush"];
        resources["CheckBoxCheckBackgroundFillChecked"] = resources["MailAccentBrush"];
        resources["CheckBoxCheckBorderBrushChecked"] = resources["MailAccentBrush"];
        resources["CheckBoxCheckGlyphForegroundChecked"] = resources["MailAccentTextBrush"];
        resources["TabItemHeaderSelectedPipeFill"] = resources["MailAccentBrush"];
        foreach (string key in new[]
        {
            "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
            "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed"
        })
        {
            resources[key] = resources["MailAccentBrush"];
        }
        return resources;
    }

}
