using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NdiMerger.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string s &&
            s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is Visibility.Visible;
        if (parameter is string s &&
            s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible;
    }
}
