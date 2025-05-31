// Copyright (c) 2025 Qz3rK 
// License: MIT (https://opensource.org/licenses/MIT)

using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DefragManager
{
    public class FavoriteToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (bool)value ? Brushes.Gold : Brushes.LightGray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
