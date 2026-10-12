using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using Postfold.Core;

namespace Postfold.UI;

public sealed class AccountNameConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, System.Type targetType, object? parameter, CultureInfo culture)
    {
        return values.Count == 2 && values[0] is AccountId account &&
            values[1] is IEnumerable<AccountConfiguration> accounts ?
            accounts.FirstOrDefault(item => item.Id == account)?.Name ?? Strings.Get("Account") : "";
    }
}
