using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace APIShared.Localization
{
    // Game-independent, assembly-private reader also compiled into the publisher and warning bootstrap.
    internal sealed class TextCatalog
    {
        private readonly Func<string, string> read;
        private readonly Action<string> diagnostic;
        private readonly Dictionary<string, string> english;
        private readonly Dictionary<string, Dictionary<string, string>> locales = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();
        internal TextCatalog(string embeddedEnglish, Func<string, string> read, Action<string> diagnostic = null)
        {
            english = Parse(embeddedEnglish);
            this.read = read;
            this.diagnostic = diagnostic;
        }

        internal string Get(string locale, string key, params object[] arguments)
        {
            locale = LocaleSelection.Normalize(locale);
            Dictionary<string, string> texts;
            lock (gate)
            {
                if (!locales.TryGetValue(locale, out texts))
                {
                    texts = new Dictionary<string, string>(english, StringComparer.Ordinal);
                    Overlay(texts, "en-US");
                    if (locale != "en-US") Overlay(texts, locale);
                    locales.Add(locale, texts);
                }
            }
            string value;
            if (!texts.TryGetValue(key, out value))
            {
                if (diagnostic != null) diagnostic("Unknown localization key: " + key);
                value = key;
            }
            if (arguments.Length == 0) return value;
            if (arguments.Length % 2 != 0) throw new ArgumentException("Localization arguments must be name/value pairs.");
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < arguments.Length; i += 2)
                replacements.Add((string)arguments[i], Convert.ToString(arguments[i + 1], CultureInfo.CurrentCulture) ?? "");
            // Substitute the template once; braces inside supplied mod names are never reinterpreted.
            return Regex.Replace(value, @"\{([A-Za-z_][A-Za-z0-9_]*)\}", m => {
                string replacement;
                return replacements.TryGetValue(m.Groups[1].Value, out replacement) ? replacement : m.Value;
            });
        }

        private void Overlay(Dictionary<string, string> target, string locale)
        {
            try
            {
                string content = read(locale);
                if (content == null) return;
                var parsed = Parse(content);
                foreach (var item in parsed)
                {
                    string original;
                    if (!english.TryGetValue(item.Key, out original)) throw new InvalidDataException("Unknown localization key: " + item.Key);
                    if (!SamePlaceholders(original, item.Value)) throw new InvalidDataException("Localization placeholder mismatch: " + item.Key);
                }
                foreach (var item in parsed) target[item.Key] = item.Value;
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException)) throw;
                if (diagnostic != null) diagnostic("Localization file " + locale + " was not used: " + error.Message);
            }
        }

        internal static bool SamePlaceholders(string left, string right)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(left, @"\{([A-Za-z_][A-Za-z0-9_]*)\}")) names.Add(m.Groups[1].Value);
            var other = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(right, @"\{([A-Za-z_][A-Za-z0-9_]*)\}")) other.Add(m.Groups[1].Value);
            return names.SetEquals(other);
        }

        internal static Dictionary<string, string> Parse(string content)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var reader = new StringReader(content ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.TrimStart('\uFEFF');
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                    int separator = line.IndexOf('=');
                    if (separator <= 0) throw new InvalidDataException("Malformed localization entry.");
                    string key = line.Substring(0, separator).Trim();
                    if (!Regex.IsMatch(key, @"^[A-Za-z0-9_.-]+$") || result.ContainsKey(key))
                        throw new InvalidDataException("Invalid or duplicate localization key: " + key);
                    var value = new StringBuilder();
                    for (int i = separator + 1; i < line.Length; i++)
                    {
                        char ch = line[i];
                        if (ch == '\\' && i + 1 < line.Length && (line[i + 1] == '\\' || line[i + 1] == 'n'))
                            value.Append(line[++i] == 'n' ? '\n' : '\\');
                        else value.Append(ch);
                    }
                    result.Add(key, value.ToString());
                }
            }
            return result;
        }
    }

    internal static class LocaleSelection
    {
        private static readonly Dictionary<string, string> SteamLanguages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            {"arabic","ar"}, {"brazilian","pt-BR"}, {"czech","cs-CZ"}, {"dutch","nl-NL"}, {"english","en-US"},
            {"french","fr-FR"}, {"german","de-DE"}, {"greek","el-GR"}, {"hungarian","hu-HU"}, {"italian","it-IT"},
            {"japanese","ja-JP"}, {"koreana","ko-KR"}, {"latam","es-ES"}, {"polish","pl-PL"}, {"portuguese","pt-BR"},
            {"russian","ru-RU"}, {"schinese","zh-CN"}, {"spanish","es-ES"}, {"swedish","sv-SE"}, {"tchinese","zh-HK"},
            {"thai","th-TH"}, {"turkish","tr-TR"}, {"ukrainian","uk-UA"} };
        internal static IEnumerable<string> SupportedLocales
        {
            get { return new SortedSet<string>(SteamLanguages.Values, StringComparer.Ordinal); }
        }
        internal static string Normalize(string value)
        {
            value = (value ?? "").Trim().Replace('_', '-');
            string mapped;
            if (SteamLanguages.TryGetValue(value, out mapped)) return mapped;
            if (value.Length == 4 && value.IndexOf('-') < 0) value = value.Insert(2, "-");
            try
            {
                var culture = CultureInfo.GetCultureInfo(value);
                if (culture.TwoLetterISOLanguageName == "de") return "de-DE";
                if (culture.TwoLetterISOLanguageName == "en") return "en-US";
                return culture.Name.Length == 0 ? "en-US" : culture.Name;
            }
            catch (CultureNotFoundException) { return "en-US"; }
        }
        internal static string Select(string configured, string steam, string extender)
        {
            return Normalize(!string.IsNullOrWhiteSpace(configured) && !string.Equals(configured, "detect", StringComparison.OrdinalIgnoreCase)
                ? configured : !string.IsNullOrWhiteSpace(steam) ? steam : extender);
        }
    }
}
