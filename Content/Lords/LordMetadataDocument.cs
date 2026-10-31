using APIShared.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace APIShared.Content.Lords
{
    /// <summary>A lossless lordmeta.json document. Unknown fields remain in <see cref="OriginalJson"/>.</summary>
    public sealed class LordMetadataDocument
    {
        private readonly Dictionary<string, object> root;
        private LordMetadataDocument(string json, Dictionary<string, object> root) { OriginalJson = json; this.root = root; }
        /// <summary>The complete input, including unknown fields and formatting.</summary>
        public string OriginalJson { get; }
        /// <summary>Parses an object without deserializing through a version-specific LordInfo type.</summary>
        public static LordMetadataDocument Parse(string json)
        {
            var value = DependencyFreeJson.Parse(json) as Dictionary<string, object>;
            if (value == null) throw new InvalidDataException("lordmeta.json must contain an object.");
            return new LordMetadataDocument(json, value);
        }
        /// <summary>Reads current-language then English text, otherwise the supplied fallback.</summary>
        public string ResolveText(string field, string locale, string fallback = "")
        {
            var texts = Get(field) as Dictionary<string, object>;
            return Text(texts, locale) ?? Text(texts, "en-US") ?? fallback;
        }
        /// <summary>Resolves a display name; empty/placeholder names use the authored fallback.</summary>
        public string ResolveName(string locale, string fallback) => ResolveText("LocalizedDisplayName", locale, fallback);
        /// <summary>Resolves a suffix using the zero-based nickname subtype; missing suffixes are empty.</summary>
        public string ResolveTitle(string locale, int nickname)
        {
            if (nickname < 0) return "";
            var titles = Get("LocalizedTitles") as Dictionary<string, object>;
            object value;
            List<object> list = null;
            if (titles != null && titles.TryGetValue(locale ?? "", out value)) list = value as List<object>;
            if ((list == null || list.Count == 0) && titles != null && titles.TryGetValue("en-US", out value)) list = value as List<object>;
            string selected = list == null || list.Count == 0 ? null : Usable(list[nickname % list.Count] as string);
            if (selected == null && titles != null && titles.TryGetValue("en-US", out value) && value is List<object> english && english.Count > 0)
                selected = Usable(english[nickname % english.Count] as string);
            return selected ?? "";
        }
        /// <summary>Combines an authored name and suffix without duplicating punctuation or whitespace.</summary>
        public static string Combine(string name, string title)
        {
            name = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(title)) return name;
            return name + (char.IsWhiteSpace(title[0]) || char.IsPunctuation(title[0]) ? "" : " ") + title;
        }
        /// <summary>Returns a nonlocalized string field, or empty.</summary>
        public string GetPath(string field) => Get(field) as string ?? "";
        /// <summary>Enumerates known presentation file references, retaining every locale in the document.</summary>
        public IReadOnlyList<string> MediaReferences()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "FacePath", "JoinAudioPath", "LeaveAudioPath", "IncomingMessage" })
                Add(result, GetPath(key));
            var messages = Get("Messages") as Dictionary<string, object>;
            if (messages != null) foreach (object clips in messages.Values)
                foreach (object clip in clips as List<object> ?? new List<object>())
                {
                    var row = clip as Dictionary<string, object>;
                    if (row == null) continue;
                    object value;
                    if (row.TryGetValue("AudioPath", out value)) Add(result, value as string);
                    if (row.TryGetValue("VideoPath", out value)) Add(result, value as string);
                }
            return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        /// <summary>Returns immutable JSON for the clips of one message category.</summary>
        public string GetMessageClipsJson(string category)
        {
            var messages = Get("Messages") as Dictionary<string, object>;
            object clips;
            return messages != null && messages.TryGetValue(category, out clips) ? DependencyFreeJson.Serialize(clips) : "[]";
        }
        private object Get(string field) { object value; return root.TryGetValue(field, out value) ? value : null; }
        private static string Text(Dictionary<string, object> values, string locale)
        { object value; return values != null && values.TryGetValue(locale ?? "", out value) ? Usable(value as string) : null; }
        private static string Usable(string value) => string.IsNullOrWhiteSpace(value) || value == "not-set" ? null : value;
        private static void Add(HashSet<string> values, string path) { if (!string.IsNullOrWhiteSpace(path)) values.Add(path); }
    }
}
