using System;
using System.Collections.Generic;

namespace APIShared.Audio
{
    /// <summary>Immutable set of attributable catalog sources a sound registration needs: the soldier
    /// sources (<c>UnitNN.SourceNN</c>) and melee kill cues (<c>MeleeKill.SourceNN</c>). Pass it to the
    /// <c>TryRegister</c> overloads of <see cref="SoundEvents"/> and <see cref="SoundSourceEvents"/> so native
    /// attribution work runs only for sources some enabled registration needs.</summary>
    /// <remarks>Registrations without a selection use <see cref="All"/>, the behavior of earlier packages.
    /// A selection is fixed for the registration's lifetime; <see cref="SoundRegistration.SetEnabled"/>
    /// adds or removes its demand. Sources without an attributable situation (for example
    /// <c>Unit55.Source05</c>) are never published and cannot be selected.</remarks>
    public sealed class SoundSourceSelection
    {
        // Demand index: catalog soldier index 0..90, then kill cues at SoundSources.All.Length + i.
        internal static readonly int Count = SoundSources.All.Length + SoundSources.Kills.Length;
        private static readonly string[] keys = BuildKeys();
        private static readonly bool[] selectable = BuildSelectable();
        private static readonly IReadOnlyList<string> selectableKeys = BuildSelectableKeys();
        private readonly bool[] members;
        private readonly IReadOnlyList<string> memberKeys;

        /// <summary>Every attributable catalog source; the default for registrations without a selection.</summary>
        public static SoundSourceSelection All { get; } = new SoundSourceSelection(selectable, true);
        /// <summary>Keys accepted by <see cref="TryCreate"/>, in catalog order. Keys stay stable; a future
        /// package may add entries.</summary>
        public static IReadOnlyList<string> SelectableSourceKeys => selectableKeys;
        /// <summary>True only for <see cref="All"/>.</summary>
        public bool IsAll { get; }
        /// <summary>Selected source keys in catalog order.</summary>
        public IReadOnlyList<string> SourceKeys => memberKeys;

        private SoundSourceSelection(bool[] value, bool all)
        {
            members = value; IsAll = all;
            var list = new List<string>();
            for (int i = 0; i < Count; i++) if (members[i]) list.Add(keys[i]);
            memberKeys = list.AsReadOnly();
        }

        /// <summary>Creates the union of the listed source keys (ordinal) and every source that can report
        /// one of the listed situations. Either argument may be null. Any thread.</summary>
        /// <remarks>A situation selects each soldier source with at least one effect of that situation;
        /// <see cref="SoundSituation.MeleeKill"/> selects the four kill cues. Unity playback never carries
        /// kill context, so a <see cref="SoundEvents"/> registration ignores selected kill cues.</remarks>
        /// <returns>False with a reason, and no selection, for an unknown or unattributable key, for
        /// <see cref="SoundSituation.Unknown"/> or a situation no catalog source reports, and for an empty union.</returns>
        public static bool TryCreate(IEnumerable<string> sourceKeys, IEnumerable<SoundSituation> situations,
            out SoundSourceSelection selection, out string reason)
        {
            selection = null;
            var value = new bool[Count]; bool any = false;
            if (sourceKeys != null)
                foreach (string key in sourceKeys)
                {
                    int index = Array.IndexOf(keys, key);
                    if (key == null || index < 0) { reason = "Unknown audio source key: " + (key ?? "null"); return false; }
                    if (!selectable[index]) { reason = "Audio source has no attributable situation: " + key; return false; }
                    value[index] = any = true;
                }
            if (situations != null)
                foreach (var situation in situations)
                {
                    bool found = false;
                    if (situation != SoundSituation.Unknown)
                        for (int i = 0; i < Count; i++)
                            if (selectable[i] && Reports(i, situation)) value[i] = found = true;
                    if (!found) { reason = "No catalog source reports situation: " + situation; return false; }
                    any = true;
                }
            if (!any) { reason = "Select at least one source or situation, or register without a selection."; return false; }
            selection = new SoundSourceSelection(value, false); reason = string.Empty; return true;
        }

        /// <summary>True when this selection contains the key (ordinal).</summary>
        public bool Contains(string sourceKey)
        {
            int index = sourceKey == null ? -1 : Array.IndexOf(keys, sourceKey);
            return index >= 0 && members[index];
        }

        internal bool Has(int index) => members[index];
        // Playback can serve only soldier sources; a kill-only selection has no playback demand.
        internal bool HasSoldierSource()
        {
            for (int i = 0; i < SoundSources.All.Length; i++) if (members[i]) return true;
            return false;
        }

        private static bool Reports(int index, SoundSituation situation)
        {
            if (index >= SoundSources.All.Length) return situation == SoundSituation.MeleeKill;
            var source = SoundSources.All[index];
            foreach (int effect in source.Effects) if (source.Situation(effect) == situation) return true;
            return false;
        }
        private static string[] BuildKeys()
        {
            var result = new string[Count];
            for (int i = 0; i < SoundSources.All.Length; i++) result[i] = SoundSources.All[i].Key;
            for (int i = 0; i < SoundSources.Kills.Length; i++) result[SoundSources.All.Length + i] = SoundSources.Kills[i].Key;
            return result;
        }
        // Mirrors OnWrapper: a soldier source is published only for an effect with a named situation.
        private static bool[] BuildSelectable()
        {
            var result = new bool[Count];
            for (int i = 0; i < SoundSources.All.Length; i++)
                foreach (int effect in SoundSources.All[i].Effects)
                    if (SoundSources.All[i].Situation(effect) != SoundSituation.Unknown) result[i] = true;
            for (int i = SoundSources.All.Length; i < Count; i++) result[i] = true;
            return result;
        }
        private static IReadOnlyList<string> BuildSelectableKeys()
        {
            var list = new List<string>();
            for (int i = 0; i < Count; i++) if (selectable[i]) list.Add(keys[i]);
            return list.AsReadOnly();
        }
    }
}
