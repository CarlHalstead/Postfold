using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Postfold.UI;

public sealed class MessageStatusConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool state)
        {
            return "";
        }

        return parameter as string == "Read" ? Strings.Get(state ? "ReadTrue" : "ReadFalse") :
            state ? Strings.Get("FlaggedTrue") : "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return AvaloniaProperty.UnsetValue;
    }
}
