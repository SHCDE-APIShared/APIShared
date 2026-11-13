using APIShared.ModSettings;
using APIShared.Internal;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
#if !API_SHARED_PRESET_TESTS
using Noesis;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
#endif

#if !API_SHARED_PRESET_TESTS
namespace APIShared.ModSettings
{
    /// <summary>One-way XAML converter for setting/section search visibility using the shared matching policy.</summary>
    public sealed class ModSettingsSearchVisibilityConverter : IMultiValueConverter
    {
        /// <summary>Returns Visible or Collapsed from the search bindings; incomplete input (fewer than six values) is Collapsed. Bind filter, tooltip inclusion, exact key, then entry key/title/tooltip and optional section key/title.</summary>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => ConvertCore(values, 0);

        internal static object ConvertCore(object[] values, int offset)
        {
            string filterText = ReadString(values, offset);
            bool includeToolTips = values != null && values.Length > offset + 1 && values[offset + 1] is bool value && value;
            string exactKey = ReadString(values, offset + 2);
            if (values == null || values.Length < offset + 6)
                return Visibility.Collapsed;
            for (int index = offset + 3; index + 2 < values.Length; index += 5)
            {
                if (ModSettingsSearchMatcher.IsMatch(
                    filterText,
                    includeToolTips,
                    exactKey,
                    ReadString(values, index),
                    ReadString(values, index + 1),
                    ReadString(values, index + 2),
                    ReadString(values, index + 3),
                    ReadString(values, index + 4)))
                {
                    return Visibility.Visible;
                }
            }
            return Visibility.Collapsed;
        }

        /// <summary>Reverse conversion is unsupported and throws NotSupportedException.</summary>
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        private static string ReadString(object[] values, int index) =>
            values != null && index < values.Length && values[index] != null
                ? values[index].ToString()
                : string.Empty;
    }
}
#endif
