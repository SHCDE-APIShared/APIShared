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
    /// <summary>Status-aware local visibility converter. Prefix the existing converter inputs with root model, state filter and state revision; the original converter and tuple layout remain compatible.</summary>
    public sealed class ModSettingsSearchStateVisibilityConverter : IMultiValueConverter
    {
        /// <summary>Combines the original text filter with current checkbox/slider state. Section rows remain visible only when a child setting matches both constraints.</summary>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 9) return Visibility.Collapsed;
            ModSettingsSearchStateFilter filter = values[1] is ModSettingsSearchStateFilter selected ? selected : ModSettingsSearchStateFilter.All;
            if (filter == ModSettingsSearchStateFilter.All)
                return ModSettingsSearchVisibilityConverter.ConvertCore(values, 3);
            object model = values[0];
            for (int index = 6; index + 2 < values.Length; index += 5)
            {
                if (ModSettingsSearchCatalog.MatchesPresentation(model, values[index]?.ToString(), values[3]?.ToString(),
                    values[4] is bool tips && tips, values[5]?.ToString(), filter)) return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }
        /// <summary>Reverse conversion is unsupported.</summary>
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
#endif
