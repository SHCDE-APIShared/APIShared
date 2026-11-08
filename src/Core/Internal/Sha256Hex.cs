using System;
using System.IO;
using System.Security.Cryptography;

namespace APIShared
{
    // Upper-case hex without separators. Persisted digests and audited native hashes compare
    // against exactly this text, so callers that need lower case convert at the call site.
    internal static class Sha256Hex
    {
        internal static string Of(byte[] data)
        {
            using (var sha = SHA256.Create()) return Format(sha.ComputeHash(data));
        }

        internal static string Of(byte[] data, int offset, int count)
        {
            using (var sha = SHA256.Create()) return Format(sha.ComputeHash(data, offset, count));
        }

        internal static string Of(Stream stream)
        {
            using (var sha = SHA256.Create()) return Format(sha.ComputeHash(stream));
        }

        internal static string Format(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");
    }
}
