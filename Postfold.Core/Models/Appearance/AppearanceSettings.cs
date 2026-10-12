namespace Postfold.Core.Models.Appearance;

public sealed record AppearanceSettings(string Theme, string Preview, string Density, string Font, double FontSize)
{
    public static readonly AppearanceSettings Default = new AppearanceSettings("System", "Right", "Comfortable", "", 14);

    public bool IsValid => Theme is "System" or "Light" or "Dark" && Preview is "Right" or "Bottom" or "Hidden" &&
        Density is "Comfortable" or "Compact" && Font is not null && Font.Length <= 200 && FontSize is >= 12 and <= 28;
}
