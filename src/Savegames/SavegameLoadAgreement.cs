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
        private readonly IReadOnlyList<Savegames.SavegameRecordDigest> records;
        internal SavegameLoadAgreement(string token, string fingerprint, IEnumerable<ulong> peers)
            : this(token, fingerprint, peers, Array.Empty<Savegames.SavegameRecordDigest>()) { }
        internal SavegameLoadAgreement(string token, string fingerprint, IEnumerable<ulong> peers,
            IReadOnlyList<Savegames.SavegameRecordDigest> hostRecords)
        {
            Token = token; FingerprintValue = fingerprint;
            expected = new HashSet<ulong>(peers);
            records = hostRecords ?? Array.Empty<Savegames.SavegameRecordDigest>();
        }
        internal string Token { get; }
        internal string FingerprintValue { get; }
        internal bool Ready => expected.SetEquals(accepted);
        /// <summary>Expected peers that have not confirmed yet, ascending.</summary>
        internal IEnumerable<ulong> Unconfirmed => expected.Where(x => !accepted.Contains(x)).OrderBy(x => x);

        /// <summary>Accepts a current peer's confirmation only when its declared simulation records
        /// match the host's. A stale or foreign confirmation is ignored without a mismatch; a current
        /// one with different records returns false and the explanation to show every player.</summary>
        internal bool Accept(ulong sender, string token, string fingerprint,
            IReadOnlyList<Savegames.SavegameRecordDigest> peerRecords, string peerName, out string mismatch)
        {
            mismatch = null;
            if (!expected.Contains(sender) || Token != token || FingerprintValue != fingerprint) return false;
            if (!Savegames.SavegameRecordComparison.IsValid(peerRecords))
            { mismatch = peerName + " sent an invalid list of saved simulation records."; return false; }
            mismatch = Savegames.SavegameRecordComparison.Describe(records, peerRecords, peerName);
            if (mismatch != null) return false;
            accepted.Add(sender); return true;
        }
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
