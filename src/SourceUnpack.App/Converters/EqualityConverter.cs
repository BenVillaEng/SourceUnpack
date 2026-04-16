using System;
using System.Globalization;
using System.Windows.Data;

namespace SourceUnpack.App.Converters
{
    public class EqualityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isEqual = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
            
            if (targetType == typeof(System.Windows.Visibility))
                return isEqual ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            
            return isEqual;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.Equals(true) == true ? parameter : System.Windows.Data.Binding.DoNothing;
        }
    }
}
