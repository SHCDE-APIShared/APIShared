using System;
using System.Collections.Generic;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Audio
{
    /// <summary>Immutable source filter: different dimensions are ANDed, values within one dimension are ORed.
    /// Null dimensions are unrestricted; unknown nullable source values cannot match a restricted dimension.</summary>
    public sealed class UnitVoiceSelection
    {
        private readonly HashSet<string> sources, cues, banks;
        private readonly HashSet<UnitVoiceSituation> situations;
        private readonly HashSet<int> requestingPlayers, owners, types;
        private UnitVoiceSelection(HashSet<string> sources, HashSet<string> cues, HashSet<string> banks, HashSet<UnitVoiceSituation> situations,
            HashSet<int> requestingPlayers, HashSet<int> owners, HashSet<int> types)
        {
            this.sources = sources; this.cues = cues; this.banks = banks; this.situations = situations;
            this.requestingPlayers = requestingPlayers; this.owners = owners; this.types = types;
            var keys = sources == null ? Array.Empty<string>() : new List<string>(sources).ToArray();
            Array.Sort(keys, StringComparer.Ordinal); SourceKeys = Array.AsReadOnly(keys);
        }
        /// <summary>All safely attributed unit speech; the default when no selection is passed.</summary>
        public static UnitVoiceSelection All { get; } = new UnitVoiceSelection(null, null, null, null, null, null, null);
        /// <summary>Selected native source keys in ordinal order. An empty list means this dimension is unrestricted.</summary>
        public IReadOnlyList<string> SourceKeys { get; }
        /// <summary>Builds a filter from copied collections, on any thread. Keys are ordinal and must exist in the catalog.
        /// Player IDs are one-based; unit types are SHCDESE eChimps values. An explicitly empty dimension is rejected.
        /// No dimensions returns All. This filters immutable emission fields, not later live game state.</summary>
        public static bool TryCreate(out UnitVoiceSelection selection, out string reason,
            IEnumerable<string> sourceKeys = null, IEnumerable<string> cueKeys = null, IEnumerable<string> voiceBankKeys = null,
            IEnumerable<UnitVoiceSituation> situations = null, IEnumerable<int> requestingPlayerIds = null,
            IEnumerable<int> ownerPlayerIds = null, IEnumerable<int> requestedUnitTypes = null)
        {
            selection = null;
            if (!Copy(sourceKeys, StringComparer.Ordinal, UnitVoiceCatalog.IsKnownSourceKey, "source key", out HashSet<string> sources, out reason) ||
                !Copy(cueKeys, StringComparer.Ordinal, key => key != null && UnitVoiceCatalog.TryGetCue(key, out _), "cue key", out HashSet<string> cues, out reason) ||
                !Copy(voiceBankKeys, StringComparer.Ordinal, HasBank, "voice bank", out HashSet<string> banks, out reason) ||
                !Copy(situations, EqualityComparer<UnitVoiceSituation>.Default, value => Enum.IsDefined(typeof(UnitVoiceSituation), value), "situation", out HashSet<UnitVoiceSituation> actions, out reason) ||
                !Copy(requestingPlayerIds, EqualityComparer<int>.Default, ValidPlayer, "requesting player", out HashSet<int> requesters, out reason) ||
                !Copy(ownerPlayerIds, EqualityComparer<int>.Default, ValidPlayer, "owner player", out HashSet<int> owners, out reason) ||
                !Copy(requestedUnitTypes, EqualityComparer<int>.Default, ValidType, "unit type", out HashSet<int> types, out reason)) return false;
            selection = sources == null && cues == null && banks == null && actions == null && requesters == null && owners == null && types == null
                ? All : new UnitVoiceSelection(sources, cues, banks, actions, requesters, owners, types);
            reason = string.Empty; return true;
        }
        /// <summary>Tests one immutable source without accessing live game state.</summary>
        public bool Matches(UnitVoiceSource source) =>
            (sources == null || sources.Contains(source.SourceKey)) &&
            (cues == null || cues.Contains(source.OriginalCueKey)) &&
            (banks == null || banks.Contains(source.VoiceBankKey)) &&
            (situations == null || situations.Contains(source.Situation)) &&
            (requestingPlayers == null || source.RequestingPlayerId.HasValue && requestingPlayers.Contains(source.RequestingPlayerId.Value)) &&
            (owners == null || source.OwnerPlayerId.HasValue && owners.Contains(source.OwnerPlayerId.Value)) &&
            (types == null || source.RequestedUnitType.HasValue && types.Contains(source.RequestedUnitType.Value));
        private static bool HasBank(string key)
        {
            if (key == null) return false;
            foreach (var cue in UnitVoiceCatalog.Cues) if (string.Equals(key, cue.VoiceBankKey, StringComparison.Ordinal)) return true;
            return false;
        }
        private static bool ValidPlayer(int id) => id >= 1 && id <= 8;
        private static bool ValidType(int type) => type > 0 && type < (int)eChimps.CHIMP_NUM_TYPES && Enum.IsDefined(typeof(eChimps), (eChimps)type);
        private static bool Copy<T>(IEnumerable<T> input, IEqualityComparer<T> comparer, Func<T, bool> valid,
            string label, out HashSet<T> values, out string reason)
        {
            values = null; reason = string.Empty;
            if (input == null) return true;
            var result = new HashSet<T>(comparer);
            foreach (T item in input)
            {
                if (!valid(item)) { reason = "Unknown or invalid unit voice " + label + ": " + item; return false; }
                result.Add(item);
            }
            if (result.Count == 0) { reason = "Unit voice " + label + " selection must not be empty."; return false; }
            values = result; return true;
        }
    }
}
