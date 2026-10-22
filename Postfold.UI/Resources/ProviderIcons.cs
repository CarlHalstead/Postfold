using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Postfold.UI.Resources;

/// <summary>Local vector provider marks used alongside labelled browser sign-in actions.</summary>
internal static class ProviderIcons
{
    internal static Control Create(string provider)
    {
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
        else
        {
            Add("#4285F4", "M21.6,12.23 C21.6,11.52 21.54,10.84 21.42,10.18 L12,10.18 L12,14.05 L17.38,14.05 C17.15,15.3 16.44,16.36 15.38,17.07 L15.38,19.58 L18.61,19.58 C20.5,17.84 21.6,15.28 21.6,12.23 Z");
            Add("#34A853", "M12,22 C14.7,22 16.96,21.1 18.61,19.58 L15.38,17.07 C14.48,17.67 13.33,18.03 12,18.03 C9.4,18.03 7.19,16.27 6.4,13.91 L3.06,13.91 L3.06,16.5 C4.7,19.76 8.09,22 12,22 Z");
            Add("#FBBC05", "M6.4,13.91 C6.2,13.31 6.09,12.67 6.09,12 C6.09,11.33 6.2,10.69 6.4,10.09 L6.4,7.5 L3.06,7.5 C2.38,8.85 2,10.38 2,12 C2,13.62 2.38,15.15 3.06,16.5 Z");
            Add("#EA4335", "M12,5.97 C13.47,5.97 14.79,6.47 15.83,7.45 L18.7,4.58 C16.96,2.96 14.7,2 12,2 C8.09,2 4.7,4.24 3.06,7.5 L6.4,10.09 C7.19,7.73 9.4,5.97 12,5.97 Z");
        }
        AutomationProperties.SetAccessibilityView(mark, AccessibilityView.Raw);
        return mark;

        void Add(string colour, string data)
        {
            mark.Children.Add(new Path
            {
                Data = Geometry.Parse(data),
                Fill = Brush.Parse(colour),
                StrokeThickness = 0
            });
        }
    }
}
