using Avalonia;
using Avalonia.Data.Converters;
using Postfold.Core.Enums;
using Postfold.UI.Resources;
using System;
using System.Globalization;

namespace Postfold.UI.Converters;

public sealed class StateTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is "IsUnavailable")
        {
            return value is ContentAvailability availability && availability != ContentAvailability.Available;
        }
        return value is Enum state ? Strings.Describe(state) : "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return AvaloniaProperty.UnsetValue;
    }
}
