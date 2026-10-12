using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Postfold.UI;

public sealed class StateTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Enum state ? Strings.Describe(state) : "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return AvaloniaProperty.UnsetValue;
    }
}
