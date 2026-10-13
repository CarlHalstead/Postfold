using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Postfold.UI.Converters;

public sealed class AttachmentSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long size || size < 0)
        {
            return "";
        }

        return size >= 1048576 ? (size / 1048576d).ToString("0.#", culture) + " MiB" :
            size >= 1024 ? (size / 1024d).ToString("0.#", culture) + " KiB" : size.ToString(culture) + " B";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
