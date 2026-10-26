using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace APIShared.Localization
{
    internal static class UiText
    {
        private static readonly object gate = new object();
        private static TextCatalog catalog;
        private static Func<string> language = () => CultureInfo.CurrentUICulture.Name;
        internal static void Configure(Func<string> locale, Action<string> diagnostic = null)
        {
            lock (gate)
            {
                language = locale ?? language;
                if (catalog == null) catalog = Create(diagnostic);
            }
        }
        private static TextCatalog Create(Action<string> diagnostic)
        {
            var assembly = typeof(UiText).Assembly;
            string embedded;
            using (var stream = assembly.GetManifestResourceStream("APIShared.Locales.en-US.txt"))
            {
                if (stream == null) throw new InvalidDataException("Embedded APIShared English catalog is absent.");
                using (var reader = new StreamReader(stream, Encoding.UTF8)) embedded = reader.ReadToEnd();
            }
            string root = Path.GetDirectoryName(assembly.Location);
            return new TextCatalog(embedded, locale => {
                string path = Path.Combine(root, "Locales", locale + ".txt");
                if (File.Exists(path)) return File.ReadAllText(path, new UTF8Encoding(false, true));
                using (var stream = assembly.GetManifestResourceStream("APIShared.Locales." + locale + ".txt"))
                {
                    if (stream == null) return null;
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false, true))) return reader.ReadToEnd();
                }
            }, diagnostic);
        }
        internal static string Get(string key, params object[] arguments) { return ForLocale(language(), key, arguments); }
        internal static string English(string key) { return ForLocale("en-US", key); }
        internal static string GetOrDefault(string key, string fallback)
        {
            string text = Get(key);
            return text == key ? fallback : text;
        }
        internal static string ForLocale(string locale, string key, params object[] arguments)
        {
            lock (gate) { if (catalog == null) catalog = Create(null); }
            return catalog.Get(locale, key, arguments);
        }
    }
}
