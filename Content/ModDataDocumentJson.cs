using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace APIShared.ModData
{
    /// <summary>Size and namespace validation for bounded GUID-separated sidecars, including existing Lord data.
    /// No file access, game dependencies or network authority. Returned DOM objects belong to the caller.</summary>
    public static class ModDataDocumentJson
    {
        /// <summary>Existing Lord-sidecar document limit in UTF-8 bytes.</summary>
        public const int MaximumDocumentBytes = 64 * 1024;
        /// <summary>Existing per-namespace limit in compact UTF-8 JSON bytes.</summary>
        public const int MaximumNamespaceBytes = 16 * 1024;

        /// <summary>Reads at most maximumDocumentBytes plus one byte from a caller-owned stream, decodes strict
        /// UTF-8, strips leading BOM characters and validates the bounded document. Leaves the stream open.
        /// Oversize/invalid data throws; malformed UTF-8 throws DecoderFallbackException. Does not open files.</summary>
        public static string ReadUtf8(Stream input, int maximumDocumentBytes = MaximumDocumentBytes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (maximumDocumentBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDocumentBytes));
            using (var bytes = new MemoryStream())
            {
                var buffer = new byte[4096];
                while (true)
                {
                    int remaining = maximumDocumentBytes - (int)bytes.Length;
                    int count = input.Read(buffer, 0, remaining >= buffer.Length ? buffer.Length : remaining + 1);
                    if (count == 0) break;
                    if (count > remaining) throw new InvalidDataException("The mod-data document exceeds its byte limit.");
                    bytes.Write(buffer, 0, count);
                }
                string json = new UTF8Encoding(false, true).GetString(bytes.ToArray()).TrimStart('\uFEFF');
                Parse(json, maximumDocumentBytes);
                return json;
            }
        }

        /// <summary>Parses and validates all namespaces. Empty/ambiguous GUID keys, non-object namespaces and
        /// exceeded limits throw InvalidDataException; malformed JSON retains the parser's data/format errors.
        /// The default limits preserve the existing Lord contract; positive custom limits are allowed.
        /// Namespace size excludes formatting outside strings. Null is invalid; callers with optional documents
        /// handle absence before parsing.</summary>
        public static Dictionary<string, object> Parse(string json, int maximumDocumentBytes = MaximumDocumentBytes,
            int maximumNamespaceBytes = MaximumNamespaceBytes)
        {
            if (maximumDocumentBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDocumentBytes));
            if (maximumNamespaceBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumNamespaceBytes));
            if (json == null || Encoding.UTF8.GetByteCount(json) > maximumDocumentBytes)
                throw new InvalidDataException("The mod-data document is missing or exceeds its byte limit.");
            var root = Internal.DependencyFreeJson.Parse(json) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("The mod-data document root must be an object.");
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root)
            {
                if (string.IsNullOrWhiteSpace(item.Key) || !keys.Add(item.Key))
                    throw new InvalidDataException("The mod-data document contains empty or ambiguous GUID namespaces.");
                if (!(item.Value is Dictionary<string, object>))
                    throw new InvalidDataException("Every mod-data GUID namespace must be an object.");
                if (Encoding.UTF8.GetByteCount(Compact(Internal.DependencyFreeJson.Serialize(item.Value))) > maximumNamespaceBytes)
                    throw new InvalidDataException("The mod-data GUID namespace '" + item.Key + "' exceeds its byte limit.");
            }
            return root;
        }

        /// <summary>Validates the entire bounded document before returning an immutable namespace snapshot.
        /// Invalid documents return InvalidDocument; an empty GUID returns InvalidRequest.</summary>
        public static ModDataReadResult ReadNamespace(string json, string modGuid, string source)
        {
            if (string.IsNullOrWhiteSpace(modGuid))
                return ModDataReadResult.Failure(ModDataReadStatus.InvalidRequest, modGuid, source, "A non-empty mod GUID is required.");
            try { Parse(json); }
            catch (Exception error) when (error is InvalidDataException || error is FormatException)
            {
                return ModDataReadResult.Failure(ModDataReadStatus.InvalidDocument, modGuid, source, error.Message);
            }
            return ModDataNamespaceReader.Read(json, modGuid, source);
        }

        private static string Compact(string pretty)
        {
            var result = new StringBuilder(pretty.Length);
            bool inString = false, escaped = false;
            foreach (char value in pretty)
            {
                if (inString)
                {
                    result.Append(value);
                    if (escaped) escaped = false;
                    else if (value == '\\') escaped = true;
                    else if (value == '"') inString = false;
                }
                else if (value == '"') { inString = true; result.Append(value); }
                else if (!char.IsWhiteSpace(value)) result.Append(value);
            }
            return result.ToString();
        }
    }
}
