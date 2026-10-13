using Avalonia.Data.Converters;
using Postfold.UI.Resources;
using System;
using System.Globalization;

namespace Postfold.UI.Converters;

public sealed class AttachmentTypeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string media = value as string ?? "";
        return media.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" :
            media.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? Strings.Get("AttachmentType.Image") :
            media.Contains("spreadsheet", StringComparison.OrdinalIgnoreCase) || media.Equals("text/csv", StringComparison.OrdinalIgnoreCase) ?
                Strings.Get("AttachmentType.Spreadsheet") :
            media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? Strings.Get("AttachmentType.Text") : Strings.Get("AttachmentType.File");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
