using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace KsfCompanion.Ui
{
    sealed class NotConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is true);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is true);
    }

    /// <summary>True when the bound value equals the parameter (compared as text): which tier / sort / view button is on.</summary>
    sealed class EqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture), System.Convert.ToString(parameter, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
