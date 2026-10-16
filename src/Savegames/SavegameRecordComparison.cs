using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace APIShared.Savegames
{
    internal enum SavegameRecordState : byte { Absent = 0, Present = 1, Unreadable = 2 }

    /// <summary>SHA-256 of one declared record as stored in one player's copy of a save.</summary>
    internal sealed class SavegameRecordDigest
    {
        internal const int HashBytes = 32;
        private static readonly byte[] NoHash = new byte[0];

        internal SavegameRecordDigest(string owner, string identifier, SavegameRecordState state, byte[] hash)
        {
            Owner = owner; Identifier = identifier; State = state; Hash = hash ?? NoHash;
        }

        internal string Owner { get; }
        internal string Identifier { get; }
        internal SavegameRecordState State { get; }
        /// <summary>32 bytes when Present, otherwise empty.</summary>
        internal byte[] Hash { get; }

        internal bool SameStoredData(SavegameRecordDigest other) =>
            State == other.State && State != SavegameRecordState.Unreadable && Hash.SequenceEqual(other.Hash);

        // read returns the stored entry bytes or null when the save has none; an exception marks the
        // record unreadable without stopping the other records.
        internal static List<SavegameRecordDigest> Capture(IEnumerable<KeyValuePair<string, string>> declared,
            Func<string, byte[]> read)
        {
            var result = new List<SavegameRecordDigest>();
            using (var sha = SHA256.Create())
                foreach (KeyValuePair<string, string> record in declared)
                {
                    byte[] stored;
                    try { stored = read(record.Value); }
                    catch { result.Add(new SavegameRecordDigest(record.Key, record.Value, SavegameRecordState.Unreadable, null)); continue; }
                    result.Add(stored == null
                        ? new SavegameRecordDigest(record.Key, record.Value, SavegameRecordState.Absent, null)
                        : new SavegameRecordDigest(record.Key, record.Value, SavegameRecordState.Present, sha.ComputeHash(stored)));
                }
            return result;
        }
    }

    // The comparison boundary of multiplayer save loading: the stored bytes of explicitly declared
    // simulation-relevant records in each player's own copy of the selected save. Settings, in-memory
    // state of an earlier mission and undeclared records are not part of it. Detection only: nothing
    // is copied or repaired. Pure rules, tested without Unity, Steam or the Extender.
    internal static class SavegameRecordComparison
    {
        internal const int MaximumRecords = 64;
        internal const int MaximumOwnerLength = 256;
        internal const int MaximumIdentifierLength = 200;
        private const int ListedDifferences = 3;

        internal static bool IsValid(IReadOnlyList<SavegameRecordDigest> records)
        {
            if (records == null || records.Count > MaximumRecords) return false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SavegameRecordDigest record in records)
            {
                if (record == null || string.IsNullOrWhiteSpace(record.Owner) || record.Owner.Length > MaximumOwnerLength ||
                    string.IsNullOrWhiteSpace(record.Identifier) || record.Identifier.Length > MaximumIdentifierLength ||
                    !seen.Add(record.Identifier) || record.State > SavegameRecordState.Unreadable ||
                    record.Hash.Length != (record.State == SavegameRecordState.Present ? SavegameRecordDigest.HashBytes : 0))
                    return false;
            }
            return true;
        }

        /// <summary>Null when every declared record matches; otherwise an explanation naming the player,
        /// mod and record. Identifiers are compared ignoring case, as the Extender's archive names are.</summary>
        internal static string Describe(IReadOnlyList<SavegameRecordDigest> host, IReadOnlyList<SavegameRecordDigest> peer, string peerName)
        {
            var hostById = host.ToDictionary(x => x.Identifier, StringComparer.OrdinalIgnoreCase);
            var peerById = peer.ToDictionary(x => x.Identifier, StringComparer.OrdinalIgnoreCase);
            var differences = new List<string>();
            foreach (string id in hostById.Keys.Union(peerById.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                hostById.TryGetValue(id, out SavegameRecordDigest h);
                peerById.TryGetValue(id, out SavegameRecordDigest p);
                string record = "record '" + id + "' of mod '" + (h ?? p).Owner + "'";
                string difference =
                    p == null ? record + " is declared by the host but not by " + peerName :
                    h == null ? record + " is declared by " + peerName + " but not by the host" :
                    h.SameStoredData(p) ? null :
                    h.State == SavegameRecordState.Unreadable ? record + " cannot be read from the host's save" :
                    p.State == SavegameRecordState.Unreadable ? record + " cannot be read from " + peerName + "'s save" :
                    h.State == SavegameRecordState.Absent ? record + " is missing from the host's save but present in " + peerName + "'s" :
                    p.State == SavegameRecordState.Absent ? record + " is in the host's save but missing from " + peerName + "'s" :
                    record + " differs between the host's save and " + peerName + "'s";
                if (difference != null) differences.Add(difference);
            }
            if (differences.Count == 0) return null;
            var text = new StringBuilder("Saved simulation data differs between players, so loading would desynchronize the game: ");
            text.Append(string.Join("; ", differences.Take(ListedDifferences)));
            if (differences.Count > ListedDifferences) text.Append("; and ").Append(differences.Count - ListedDifferences).Append(" more");
            text.Append(". Every player needs the same mod and APIShared versions and a copy of the same saved game;" +
                " if that is the case, report this to the mod's author.");
            return text.ToString();
        }
    }
}
