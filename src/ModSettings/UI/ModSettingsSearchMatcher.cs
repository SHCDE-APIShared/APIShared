using APIShared.ModSettings;
using APIShared.Internal;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
#if !API_SHARED_PRESET_TESTS
using Noesis;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
#endif

namespace APIShared.ModSettings
{
    /// <summary>Pure matching policy shared by runtime filtering and isolated tests.</summary>
    public static class ModSettingsSearchMatcher
    {
        /// <summary>Matches an exact ordinal setting/section key when supplied, otherwise all free-text terms in titles and optionally tooltips. Empty filters match all; text matching folds case (culture-invariant) and diacritics.</summary>
        public static bool IsMatch(
            string filterText,
            bool includeToolTips,
            string exactKey,
            string key,
            string title,
            string toolTip)
        {
            return IsMatch(
                filterText,
                includeToolTips,
                exactKey,
                key,
                title,
                toolTip,
                string.Empty,
                string.Empty);
        }

        /// <summary>Matches an exact ordinal setting/section key when supplied, otherwise all free-text terms in titles and optionally tooltips. Empty filters match all; text matching folds case (culture-invariant) and diacritics.</summary>
        public static bool IsMatch(
            string filterText,
            bool includeToolTips,
            string exactKey,
            string key,
            string title,
            string toolTip,
            string sectionKey,
            string sectionTitle)
        {
            string exact = Normalize(exactKey);
            if (exact.Length > 0)
            {
                return string.Equals(Normalize(key), exact, StringComparison.Ordinal) ||
                    string.Equals(Normalize(sectionKey), exact, StringComparison.Ordinal);
            }

            string filter = Normalize(filterText);
            if (filter.Length == 0)
                return true;

            string visibleText = JoinSearchText(title, sectionTitle);
            if (ContainsAllTerms(visibleText, filter))
                return true;
            return includeToolTips && ContainsAllTerms(JoinSearchText(visibleText, toolTip), filter);
        }

        /// <summary>Tests all free-text terms against a section title using the same case/diacritic folding as entry matching.</summary>
        public static bool IsSectionTitleMatch(string filterText, string sectionTitle) =>
            ContainsAllTerms(sectionTitle, Normalize(filterText));

        /// <summary>Trims search text and maps null to empty; does not itself perform case/diacritic folding.</summary>
        public static string Normalize(string value) => (value ?? string.Empty).Trim();

        private static bool ContainsAllTerms(string value, string filter)
        {
            if (filter.Length == 0)
                return true;
            string haystack = FoldEntryText(value);
            FoldedFilter needle = FoldFilter(filter);
            if (haystack.Contains(needle.Text))
                return true;
            foreach (string term in needle.Terms)
                if (!haystack.Contains(term))
                    return false;
            return true;
        }

        private static string JoinSearchText(string first, string second)
        {
            bool hasFirst = !string.IsNullOrWhiteSpace(first);
            bool hasSecond = !string.IsNullOrWhiteSpace(second);
            if (hasFirst && hasSecond)
                return first + " " + second;
            return hasFirst ? first : hasSecond ? second : string.Empty;
        }

        // One query matches every entry against the same filter, so the folded filter of the
        // latest query is reused. Fold is pure; entry texts are cached until a size limit.
        private sealed class FoldedFilter
        {
            internal FoldedFilter(string filter, string text)
            {
                Filter = filter;
                Text = text;
                Terms = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            }

            internal readonly string Filter;
            internal readonly string Text;
            internal readonly string[] Terms;
        }

        private const int FoldedEntryCacheLimit = 4096;
        private static readonly Dictionary<string, string> FoldedEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        private static FoldedFilter lastFilter;

        private static FoldedFilter FoldFilter(string filter)
        {
            FoldedFilter last = Volatile.Read(ref lastFilter);
            if (last != null && string.Equals(last.Filter, filter, StringComparison.Ordinal))
                return last;
            last = new FoldedFilter(filter, Fold(filter));
            Volatile.Write(ref lastFilter, last);
            return last;
        }

        private static string FoldEntryText(string value)
        {
            string key = value ?? string.Empty, folded;
            lock (FoldedEntries)
            {
                if (FoldedEntries.TryGetValue(key, out folded))
                    return folded;
            }
            folded = Fold(key);
            lock (FoldedEntries)
            {
                if (FoldedEntries.Count >= FoldedEntryCacheLimit)
                    FoldedEntries.Clear();
                FoldedEntries[key] = folded;
            }
            return folded;
        }

        private static string Fold(string value)
        {
            string decomposed = ModSettingsSearchText.RepairNormalizationInput(Normalize(value)).Normalize(NormalizationForm.FormD);
            var result = new StringBuilder(decomposed.Length);
            foreach (char character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                    result.Append(char.ToLowerInvariant(character));
            }
            return result.ToString().Normalize(NormalizationForm.FormC);
        }
    }
    // Kept with the pure matcher so the legacy preset harness tests production text handling too.
    internal static class ModSettingsSearchText
    {
        internal static string RepairNormalizationInput(string value)
        {
            StringBuilder repaired = null;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    int scalar = char.ConvertToUtf32(character, value[index + 1]);
                    if ((scalar & 0xFFFF) >= 0xFFFE)
                    {
                        if (repaired == null) repaired = new StringBuilder(value.Length).Append(value, 0, index);
                        repaired.Append('\uFFFD');
                    }
                    else if (repaired != null) repaired.Append(character).Append(value[index + 1]);
                    index++;
                    continue;
                }
                // CLR normalization rejects Unicode noncharacters on supported runtime variants.
                // Repair comparison input only; never rewrite displayed text or exact keys.
                if (char.IsSurrogate(character) || character >= '\uFFFE' ||
                    (character >= '\uFDD0' && character <= '\uFDEF'))
                {
                    if (repaired == null) repaired = new StringBuilder(value.Length).Append(value, 0, index);
                    repaired.Append('\uFFFD');
                }
                else if (repaired != null) repaired.Append(character);
            }
            return repaired == null ? value : repaired.ToString();
        }

        internal static string FirstSentence(string text)
        {
            string normalized = (text ?? string.Empty).Trim();
            int end = normalized.IndexOfAny(new[] { '.', '!', '?', '\r', '\n' });
            if (end >= 0) normalized = normalized.Substring(0, end + 1);
            if (normalized.Length <= 120) return normalized;
            int length = 117;
            if (char.IsHighSurrogate(normalized[length - 1]) && char.IsLowSurrogate(normalized[length])) length--;
            return normalized.Substring(0, length) + "...";
        }

        internal static uint StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in value ?? string.Empty)
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return hash;
            }
        }

        // Automatic key of a setting without explicit metadata. A known tooltip binding path keeps
        // the key independent of the localized tooltip text.
        internal static string AutomaticKey(string bindingPath, string title, string toolTip)
        {
            string seed = bindingPath.Length > 0 ? bindingPath + ":" + title : title + ":" + toolTip;
            return "auto:" + StableHash(seed).ToString("x8", CultureInfo.InvariantCulture);
        }

        // Entries with the same title and tooltip are one logical setting.
        internal static string Identity(string title, string toolTip) => title + "\u001f" + toolTip;

        // Adds and returns the first free key of key, key:2, key:3, ...
        internal static string ReserveUniqueKey(System.Collections.Generic.ISet<string> keys, string key)
        {
            string uniqueKey = key;
            int suffix = 2;
            while (!keys.Add(uniqueKey))
                uniqueKey = key + ":" + suffix++;
            return uniqueKey;
        }
    }
}
