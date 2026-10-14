using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace APIShared
{
    // Pure transaction rules, tested independently of Unity and network timing.
    internal sealed class SavegameLoadAgreement
    {
        private readonly HashSet<ulong> expected;
        private readonly HashSet<ulong> accepted = new HashSet<ulong>();
        internal SavegameLoadAgreement(string token, string fingerprint, IEnumerable<ulong> peers)
        {
            Token = token; FingerprintValue = fingerprint;
            expected = new HashSet<ulong>(peers);
        }
        internal string Token { get; }
        internal string FingerprintValue { get; }
        internal bool Ready => expected.SetEquals(accepted);
        internal static bool MatchesSaveIdentity(string localName, int localChecksum, int localTime,
            string hostName, int hostChecksum, int hostTime) =>
            string.Equals(localName, hostName, StringComparison.OrdinalIgnoreCase) &&
            (localChecksum ^ localTime) == (hostChecksum ^ hostTime);
        internal bool Accept(ulong sender, string token, string fingerprint)
        {
            if (!expected.Contains(sender) || Token != token || FingerprintValue != fingerprint) return false;
            accepted.Add(sender); return true;
        }
        internal static string Fingerprint(Dictionary<string, Dictionary<string, byte[]>> values)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(values.Count);
                foreach (var mod in values.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    writer.Write(mod.Key); writer.Write(mod.Value.Count);
                    foreach (var setting in mod.Value.OrderBy(x => x.Key, StringComparer.Ordinal))
                    {
                        writer.Write(setting.Key);
                        if (setting.Value == null) throw new InvalidDataException("Null host setting.");
                        writer.Write(setting.Value.Length); writer.Write(setting.Value);
                    }
                }
                writer.Flush();
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
            }
        }
    }
}
