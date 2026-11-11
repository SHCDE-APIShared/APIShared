using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace APIShared.ModData
{
    /// <summary>Result categories shared by JSON namespace readers and runtime data sources.</summary>
    public enum ModDataReadStatus
    {
        /// <summary>Success result category.</summary>
        Success,
        /// <summary>FileNotFound result category.</summary>
        FileNotFound,
        /// <summary>NamespaceNotFound result category.</summary>
        NamespaceNotFound,
        /// <summary>InvalidDocument result category.</summary>
        InvalidDocument,
        /// <summary>ReadError result category.</summary>
        ReadError,
        /// <summary>InvalidRequest result category.</summary>
        InvalidRequest,
        /// <summary>HostDataUnavailable result category.</summary>
        HostDataUnavailable,
    }

    /// <summary>Independent immutable JSON snapshot. Source is caller-provided provenance.</summary>
    public sealed class ModDataReadResult
    {
        internal ModDataReadResult(
            ModDataReadStatus status,
            string modGuid,
            string source,
            IReadOnlyDictionary<string, object> data,
            string json,
            string diagnostic)
        {
            Status = status;
            ModGuid = modGuid ?? string.Empty;
            Source = source ?? string.Empty;
            Data = data;
            Json = json;
            Diagnostic = diagnostic ?? string.Empty;
        }

        /// <summary>Result category.</summary>
        public ModDataReadStatus Status { get; }
        /// <summary>Whether the requested namespace was read.</summary>
        public bool Success => Status == ModDataReadStatus.Success;
        /// <summary>Canonical GUID spelling on success; requested GUID otherwise.</summary>
        public string ModGuid { get; }
        /// <summary>Provenance supplied by the caller.</summary>
        public string Source { get; }
        /// <summary>Recursively immutable object snapshot; null on failure.</summary>
        public IReadOnlyDictionary<string, object> Data { get; }
        /// <summary>Normalized standalone namespace JSON; null on failure.</summary>
        public string Json { get; }
        /// <summary>Diagnostic text; empty on success.</summary>
        public string Diagnostic { get; }

        internal static ModDataReadResult FileNotFound(string modGuid, string source) =>
            Failure(ModDataReadStatus.FileNotFound, modGuid, source, "The mod-data file does not exist.");

        internal static ModDataReadResult ReadError(string modGuid, string source, string diagnostic) =>
            Failure(ModDataReadStatus.ReadError, modGuid, source, diagnostic);

        internal static ModDataReadResult InvalidDocument(string modGuid, string source, string diagnostic) =>
            Failure(ModDataReadStatus.InvalidDocument, modGuid, source, diagnostic);

        internal static ModDataReadResult InvalidRequest(string modGuid, string source, string diagnostic) =>
            Failure(ModDataReadStatus.InvalidRequest, modGuid, source, diagnostic);

        internal static ModDataReadResult HostDataUnavailable(string modGuid, string source, string diagnostic) =>
            Failure(ModDataReadStatus.HostDataUnavailable, modGuid, source, diagnostic);

        /// <summary>Creates an immutable failure result for a runtime source/provider. Success and unknown statuses
        /// throw ArgumentOutOfRangeException; successful namespace snapshots are created by the namespace reader.</summary>
        public static ModDataReadResult Failure(
            ModDataReadStatus status,
            string modGuid,
            string source,
            string diagnostic)
        {
            if (status == ModDataReadStatus.Success || !Enum.IsDefined(typeof(ModDataReadStatus), status))
                throw new ArgumentOutOfRangeException(nameof(status));
            return new ModDataReadResult(status, modGuid, source, null, null, diagnostic);
        }
    }

    /// <summary>Reads GUID-separated JSON object namespaces without game or filesystem dependencies. GUID matching ignores case. Ambiguous casing, empty GUID keys and non-object namespaces reject the entire document. Source-specific size limits remain the caller's responsibility.</summary>
    public static class ModDataNamespaceReader
    {
        /// <summary>Reads a JSON container and returns the requested namespace. Malformed JSON returns InvalidDocument; an empty GUID returns InvalidRequest.</summary>
        public static ModDataReadResult Read(string json, string modGuid, string source)
        {
            if (string.IsNullOrWhiteSpace(modGuid))
            {
                return ModDataReadResult.InvalidRequest(
                    modGuid,
                    source,
                    "A non-empty mod GUID is required.");
            }

            Dictionary<string, object> root;
            try
            {
                root = APIShared.Internal.DependencyFreeJson.Parse(json) as Dictionary<string, object>;
            }
            catch (Exception exception) when (exception is InvalidDataException || exception is FormatException)
            {
                return ModDataReadResult.InvalidDocument(
                    modGuid,
                    source,
                    "The mod-data file is not valid JSON: " + exception.Message);
            }

            if (root == null)
            {
                return ModDataReadResult.InvalidDocument(
                    modGuid,
                    source,
                    "The mod-data document root must be a JSON object.");
            }

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            KeyValuePair<string, object>? requested = null;
            foreach (KeyValuePair<string, object> entry in root)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    return ModDataReadResult.InvalidDocument(
                        modGuid,
                        source,
                        "Mod GUID keys must not be empty.");
                }
                if (!keys.Add(entry.Key))
                {
                    return ModDataReadResult.InvalidDocument(
                        modGuid,
                        source,
                        "The document contains mod GUID keys that differ only by letter casing.");
                }
                if (!(entry.Value is Dictionary<string, object>))
                {
                    return ModDataReadResult.InvalidDocument(
                        modGuid,
                        source,
                        "Every mod GUID namespace must be a JSON object.");
                }
                if (string.Equals(entry.Key, modGuid, StringComparison.OrdinalIgnoreCase))
                    requested = entry;
            }

            if (!requested.HasValue)
            {
                return new ModDataReadResult(
                    ModDataReadStatus.NamespaceNotFound,
                    modGuid,
                    source,
                    null,
                    null,
                    "The requested mod GUID namespace is not present.");
            }

            var namespaceObject = (Dictionary<string, object>)requested.Value.Value;
            string namespaceJson = APIShared.Internal.DependencyFreeJson.Serialize(namespaceObject);
            return new ModDataReadResult(
                ModDataReadStatus.Success,
                requested.Value.Key,
                source,
                FreezeObject(namespaceObject),
                namespaceJson,
                string.Empty);
        }

        private static IReadOnlyDictionary<string, object> FreezeObject(Dictionary<string, object> source)
        {
            var copy = new Dictionary<string, object>(source.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> entry in source)
                copy.Add(entry.Key, FreezeValue(entry.Value));
            return new ReadOnlyDictionary<string, object>(copy);
        }

        private static object FreezeValue(object value)
        {
            if (value is Dictionary<string, object> objectValue)
                return FreezeObject(objectValue);
            if (value is List<object> arrayValue)
            {
                var copy = new List<object>(arrayValue.Count);
                foreach (object item in arrayValue)
                    copy.Add(FreezeValue(item));
                return new ReadOnlyCollection<object>(copy);
            }
            return value;
        }
    }
}
