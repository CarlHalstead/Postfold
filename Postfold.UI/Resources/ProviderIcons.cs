using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;

namespace Postfold.UI.Resources;

/// <summary>Local provider marks used alongside labelled browser sign-in actions.</summary>
internal static class ProviderIcons
{
    // Official Google Identity asset: https://developers.google.com/identity/branding-guidelines
    private static readonly Bitmap Google = new Bitmap(AssetLoader.Open(new Uri("avares://Postfold.UI/Resources/Google.png")));

    internal static Control Create(string provider)
    {
        if (provider == "Google")
        {
            Image image = new Image
            {
                Source = Google,
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform
            };
            AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
            return image;
        }
        Canvas mark = new Canvas
        {
            Width = 24,
            Height = 24
        };
        if (provider == "Microsoft")
        {
            string[] colours = ["#F25022", "#7FBA00", "#00A4EF", "#FFB900"];
            for (int index = 0; index < colours.Length; index++)
            {
                Rectangle square = new Rectangle
                {
                    Width = 11,
                    Height = 11,
                    Fill = Brush.Parse(colours[index]),
                    StrokeThickness = 0
                };
                Canvas.SetLeft(square, index % 2 * 13);
                Canvas.SetTop(square, index / 2 * 13);
                mark.Children.Add(square);
            }
        }
        AutomationProperties.SetAccessibilityView(mark, AccessibilityView.Raw);
        return mark;
    }
}
