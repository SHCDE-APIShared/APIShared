using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using APIShared.NativeSites;

namespace APIShared.Units.AiMilitary.Engineering
{
    internal static unsafe partial class AiMilitaryEngineeringRuntime
    {
        // Called by the single actual-native-create broker after its Original returns.
        // It must pass the real return ID/GID/effective type, not mutable SE Post labels.
        internal static void OnUnitCreated(AiUnitRef unit, eChimps type)
        {
            if (!IsMachine((int)type)) return;
            if (scope.History != null && scope.Generation == generation && Types[scope.Index] == type)
            {
                if (scope.Product.UnitId == 0) scope.Product = unit;
                else scope.History.Known = false; // An unexpected nested product is ambiguous.
            }
            else if (replacement != null && replacement.Generation == generation && replacement.Type == (int)type &&
                TryUnit(unit, out byte* raw) && *(ushort*)(raw + 0x6EE) == replacement.Owner)
            {
                if (replacement.Created.UnitId == 0) replacement.Created = unit;
                else replacement.Ambiguous = true;
            }
        }
        internal static ReplacementScope BeginReplacement(int id, uint global)
        {
            if (!Publication.IsReady || !Quota.AiMilitaryQuotaRuntime.HasDemand) return null;
            var source = new AiUnitRef(id, global);
            if (!isAdditional(source) || !TryUnit(source, out byte* raw) || *(ushort*)(raw + 0x6E4) == 0 || *(ushort*)(raw + 0x6E4) == 3) return MaskReplacement();
            int type = *(ushort*)(raw + 0x6E6), count = RequiredCrew(type);
            AiUnitRef[] crew = ReadCrew(raw, count); if (crew.Length != count || count == 0) return MaskReplacement();
            var current = new ReplacementScope { Previous = replacement, Source = source, Type = type,
                Owner = *(ushort*)(raw + 0x6EE), Generation = generation, Crew = crew };
            replacement = current; return current;
        }
        private static ReplacementScope MaskReplacement()
        {
            if (replacement == null) return null;
            var barrier = new ReplacementScope { Previous = replacement, Generation = generation, Type = -1 };
            replacement = barrier; return barrier;
        }
        internal static void EndReplacement(ReplacementScope current, bool completed)
        {
            if (current == null) return;
            try
            {
                if (completed && ReferenceEquals(replacement, current) && current.Generation == generation &&
                    !current.Ambiguous && current.Source.UnitId != 0 && current.Created.UnitId != 0 && !current.Created.Equals(current.Source) &&
                    (!TryUnit(current.Source, out byte* old) || *(ushort*)(old + 0x6E4) == 0 || *(ushort*)(old + 0x6E4) == 3))
                    Candidates[current.Created] = new ReplacementCandidate(current.Source, current.Type, current.Owner, current.Crew);
            }
            finally { if (ReferenceEquals(replacement, current)) replacement = current.Previous; }
        }
        internal static void OnQuotaMarkerChanged(AiUnitRef unit, bool additional)
        {
            if (!Publication.IsReady || !TryUnit(unit, out byte* raw)) return;
            // Automatic cleanup of a deleted old slot must retain its provenance
            // until a Fixes replacement has recovered the same real crew.
            if (*(ushort*)(raw + 0x6E4) == 0 || *(ushort*)(raw + 0x6E4) == 3) return;
            int type = *(ushort*)(raw + 0x6E6), count = RequiredCrew(type); if (count == 0) return;
            AiUnitRef[] crew = ReadCrew(raw, count);
            if (crew.Length != 0)
                ReplaceProductHistory(unit, new ProductHistory(type, *(ushort*)(raw + 0x6EE), additional, crew));
            else if (Products.TryGetValue(unit, out ProductHistory prior))
                Products[unit] = new ProductHistory(prior.Type, prior.Owner, additional, prior.Crew);
        }

        // Bounded reconciliation of actual machine creations. Call at the core's
        // quiescent point before any admissions or save capture; no global unit scan.
        internal static void Reconcile()
        {
            PruneBuilds(); PruneProducts();
            if (Candidates.Count == 0) return;
            RemoveCandidates.Clear();
            foreach (var pending in Candidates)
            {
                AiUnitRef candidate = pending.Key; ReplacementCandidate origin = pending.Value;
                if (!TryUnit(candidate, out byte* raw) || *(ushort*)(raw + 0x6E4) == 0 || *(ushort*)(raw + 0x6E4) == 3)
                { RemoveCandidates.Add(candidate); continue; }
                int type = *(ushort*)(raw + 0x6E6), owner = *(ushort*)(raw + 0x6EE), need = RequiredCrew(type);
                if (origin.Type != type || origin.Owner != owner || origin.Crew.Length != need) { RemoveCandidates.Add(candidate); continue; }
                bool validCrew = true;
                foreach (var worker in origin.Crew)
                    if (!TryUnit(worker, out byte* person) || !UnitAccess.IsReallyAlive((GameUnit*)(person + 0x65C)) ||
                        *(ushort*)(person + 0x6EE) != owner || *(ushort*)(person + 0x6E6) != (int)eChimps.CHIMP_TYPE_ENGINEER)
                    { validCrew = false; break; }
                if (!validCrew) { RemoveCandidates.Add(candidate); continue; }
                if (*(ushort*)(raw + 0xA0C) != need) continue;
                if (SameCrew(raw, origin.Crew)) markProduct(candidate, true);
                RemoveCandidates.Add(candidate);
            }
            // Record replacement lineage too, so subsequent Fixes relocations preserve it.
            foreach (AiUnitRef done in RemoveCandidates)
            {
                Candidates.Remove(done);
                if (TryUnit(done, out byte* raw) && *(ushort*)(raw + 0x6E4) != 0 && *(ushort*)(raw + 0x6E4) != 3)
                { int type = *(ushort*)(raw + 0x6E6); int need = RequiredCrew(type);
                    ReplaceProductHistory(done, new ProductHistory(type, *(ushort*)(raw + 0x6EE), isAdditional(done), ReadCrew(raw, need))); }
            }
        }
        private static void PruneBuilds()
        {
            if (Builds.Count == 0) return;
            RemoveBuilds.Clear();
            foreach (var pair in Builds)
            {
                byte* raw = (byte*)(module + BuildingManager + pair.Key.Id * BuildingStride);
                if (*(uint*)(raw + 0x134) != pair.Key.Global || *(ushort*)(raw + 0x12C) == 0 || *(ushort*)(raw + 0x12C) == 3)
                    RemoveBuilds.Add(pair.Key);
            }
            foreach (var key in RemoveBuilds)
            { Builds.Remove(key); if (((uint*)trackedBuildings)[key.Id] == key.Global) ((uint*)trackedBuildings)[key.Id] = 0; }
        }
        private static void PruneProducts()
        {
            if (Products.Count == 0) return;
            RemoveProducts.Clear();
            foreach (var pair in Products)
            {
                if (TryUnit(pair.Key, out byte* raw) && *(ushort*)(raw + 0x6E4) != 0 && *(ushort*)(raw + 0x6E4) != 3) continue;
                bool anyCrew = false; foreach (var crew in pair.Value.Crew) if (TryUnit(crew, out _)) { anyCrew = true; break; }
                if (!anyCrew) RemoveProducts.Add(pair.Key);
            }
            foreach (var key in RemoveProducts) Products.Remove(key);
        }
        private static void ReplaceProductHistory(AiUnitRef product, ProductHistory history)
        {
            // A participant can belong to only one current product lineage. Once it
            // builds/mans a replacement, old overlapping records cannot be reused for
            // a later unrelated machine. This also bounds histories by live crew IDs.
            RemoveProducts.Clear();
            foreach (var pair in Products)
            {
                bool overlap = false;
                foreach (var worker in history.Crew)
                    foreach (var previous in pair.Value.Crew) if (worker.Equals(previous)) { overlap = true; break; }
                if (overlap || pair.Key.Equals(product)) RemoveProducts.Add(pair.Key);
            }
            foreach (var key in RemoveProducts) Products.Remove(key);
            if (history.Crew.Length != 0) Products[product] = history;
        }
        private static bool SameCrew(byte* raw, AiUnitRef[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                bool found = false;
                for (int j = 0; j < expected.Length; j++)
                    if (*(ushort*)(raw + 0x96C + j * 2) == expected[i].UnitId && *(uint*)(raw + 0x974 + j * 4) == expected[i].GlobalId)
                    { found = TryUnit(expected[i], out _); break; }
                if (!found) return false;
            }
            return true;
        }
        private static AiUnitRef[] ReadCrew(byte* raw, int count)
        {
            if (count <= 0 || count > 4 || *(ushort*)(raw + 0xA0C) != count) return Array.Empty<AiUnitRef>();
            var result = new AiUnitRef[count];
            for (int i = 0; i < count; i++)
            { result[i] = new AiUnitRef(*(ushort*)(raw + 0x96C + i * 2), *(uint*)(raw + 0x974 + i * 4));
                if (!TryUnit(result[i], out byte* worker) || !UnitAccess.IsReallyAlive((GameUnit*)(worker + 0x65C)) ||
                    *(ushort*)(worker + 0x6E6) != (int)eChimps.CHIMP_TYPE_ENGINEER || *(ushort*)(worker + 0x6EE) != *(ushort*)(raw + 0x6EE)) return Array.Empty<AiUnitRef>();
                for (int j = 0; j < i; j++) if (result[i].Equals(result[j])) return Array.Empty<AiUnitRef>(); }
            return result;
        }
        private static bool TryUnit(AiUnitRef unit, out byte* raw)
        {
            raw = null;
            if (module == IntPtr.Zero || unit.UnitId <= 0 || unit.UnitId >= 10000 || unit.GlobalId == 0) return false;
            raw = (byte*)(module + UnitManager + unit.UnitId * UnitStride);
            if (*(uint*)(raw + 0x6F0) == unit.GlobalId) return true;
            raw = null; return false;
        }
        private static bool IsMachine(int type) => RequiredCrew(type) != 0;
        private static int RequiredCrew(int type)
        { switch ((eChimps)type) {
                case eChimps.CHIMP_TYPE_CATAPULT: case eChimps.CHIMP_TYPE_MANGONEL: case eChimps.CHIMP_TYPE_BALLISTA: case eChimps.CHIMP_TYPE_ARAB_BALLISTA: return 2;
                case eChimps.CHIMP_TYPE_TREBUCHET: return 3;
                case eChimps.CHIMP_TYPE_SIEGE_TOWER: case eChimps.CHIMP_TYPE_BATTERING_RAM: return 4;
                case eChimps.CHIMP_TYPE_PORTABLE_SHIELD: return 1; default: return 0; } }
    }
}
