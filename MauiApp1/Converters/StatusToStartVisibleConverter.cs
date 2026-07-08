using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WebApplication1.Enums;

namespace MauiApp1.Converters
{
    public class StatusToStartVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // If value is an int
            if (value is int intVal)
                return intVal == 0; // 0 = Assigned

            // If value is a string representation of an int
            if (int.TryParse(value?.ToString(), out int intStatus))
                return intStatus == 0;

            if (value is Status.Assigned)
                return true;

            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
