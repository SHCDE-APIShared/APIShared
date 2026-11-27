using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SHCDESE.Interop;

namespace APIShared.Wildlife
{
    // Pure catalog compilation, independent of the game. Statuses are written for every registration.
    internal static class HuntablePreyCatalogBuilder
    {
        internal static readonly int Deer = (int)eChimps.CHIMP_TYPE_DEER;
        internal static readonly int Goat = (int)eChimps.CHIMP_TYPE_GOAT;
        internal static readonly HuntablePreyCatalog VanillaOnly = Compile(Array.Empty<HuntablePreyRegistration>());

        // Vanilla counts the Hunter reservation down only for deer and goats (0x156130).
        internal static bool HasVanillaReservationRelease(int unitType) => unitType == Deer || unitType == Goat;

        // The audited reservation word at public GameUnit +0x448 has an unrelated meaning for these types:
        // the kennel stores a war dog's slot index there (0xB47E0, read at 0x1671BB) and the Engineer keeps
        // a flag there (0x14AED0). A Hunter lease (0x12FE63) and the release sweep would corrupt both.
        internal static string UnsupportedReason(int unitType)
        {
            if (unitType == (int)eChimps.CHIMP_TYPE_WAR_DOG)
                return "War dogs use the Hunter reservation field for their kennel slot.";
            if (unitType == (int)eChimps.CHIMP_TYPE_ENGINEER)
                return "Engineers use the Hunter reservation field for their own state.";
            return "";
        }

        internal static HuntablePreyCatalog Compile(IReadOnlyList<HuntablePreyRegistration> registrations)
        {
            var winners = new Dictionary<int, HuntablePreyRegistration>();
            // A corpse lifetime is a lower bound each requesting mod needs: every enabled registration of the
            // type contributes, independent of priority, and the longest one wins (order-independent).
            var lifetimes = new Dictionary<int, int>();
            foreach (var registration in registrations.OrderByDescending(item => item.Priority)
                .ThenBy(item => item.OwnerGuid, StringComparer.Ordinal)
                .ThenBy(item => item.RegistrationId, StringComparer.Ordinal))
            {
                int type = registration.Definition.UnitType;
                if (registration.UnsupportedReason.Length != 0)
                    registration.SetStatus(HuntablePreyRegistrationStatus.Unsupported);
                else if (!registration.IsEnabled)
                    registration.SetStatus(HuntablePreyRegistrationStatus.Disabled);
                else
                {
                    if (winners.ContainsKey(type))
                        registration.SetStatus(HuntablePreyRegistrationStatus.ExcludedByPriority);
                    else
                    {
                        winners.Add(type, registration);
                        registration.SetStatus(HuntablePreyRegistrationStatus.Admitted);
                    }
                    int? lifetime = registration.Definition.MinimumCorpseLifetime;
                    if (lifetime.HasValue && (!lifetimes.TryGetValue(type, out int longest) || lifetime.Value > longest))
                        lifetimes[type] = lifetime.Value;
                }
            }

            var entries = new List<HuntablePreyEntry>(winners.Count + 2);
            foreach (int vanilla in new[] { Deer, Goat })
                if (!winners.ContainsKey(vanilla))
                    entries.Add(new HuntablePreyEntry(vanilla, 0, null, false, false, APISharedPlugin.PluginGuid, "", true));
            foreach (var winner in winners.Values)
                entries.Add(new HuntablePreyEntry(winner.Definition.UnitType, winner.Definition.HandlingCost,
                    winner.Definition.BaseMeat, winner.Definition.AllowsOwnPlayerPrey, winner.Definition.RemovesCorpseWhenButchered,
                    winner.OwnerGuid, winner.RegistrationId, false,
                    lifetimes.TryGetValue(winner.Definition.UnitType, out int corpseLifetime) ? corpseLifetime : (int?)null));
            var ordered = entries.OrderBy(entry => entry.UnitType).ToArray();
            return new HuntablePreyCatalog(ordered, DigestOf(ordered), winners.Count != 0);
        }

        // Every field that can change a peer's simulation or a consumer's deterministic ranking.
        private static string DigestOf(HuntablePreyEntry[] entries)
        {
            using (var data = new MemoryStream())
            using (var writer = new BinaryWriter(data, Encoding.UTF8))
            {
                // Version 2 adds base meat, own-player prey and corpse removal, version 3 corpse lifetimes; a
                // catalog without the newer fields keeps the older bytes and digest.
                bool lifetimes = entries.Any(entry => entry.MinimumCorpseLifetime.HasValue);
                bool extended = lifetimes || entries.Any(entry => entry.BaseMeat.HasValue || entry.AllowsOwnPlayerPrey || entry.RemovesCorpseWhenButchered);
                writer.Write(lifetimes ? "huntable-prey/3" : extended ? "huntable-prey/2" : "huntable-prey/1");
                writer.Write(entries.Length);
                foreach (var entry in entries)
                {
                    writer.Write(entry.UnitType);
                    writer.Write(entry.HandlingCost);
                    if (extended)
                    {
                        writer.Write(entry.BaseMeat ?? -1);
                        writer.Write(entry.AllowsOwnPlayerPrey);
                        writer.Write(entry.RemovesCorpseWhenButchered);
                    }
                    if (lifetimes) writer.Write(entry.MinimumCorpseLifetime ?? -1);
                    writer.Write(entry.OwnerGuid);
                    writer.Write(entry.RegistrationId);
                    writer.Write(entry.IsVanilla);
                }
                writer.Flush();
                return Sha256Hex.Of(data.GetBuffer(), 0, checked((int)data.Length));
            }
        }

        // Types APIShared admits to the query and releases. Deer and goats keep Vanilla's own release.
        internal static byte[] BuildDemandTable(HuntablePreyCatalog catalog)
        {
            if (!catalog.HasRegistrations) return null;
            byte[] table = null;
            foreach (var entry in catalog.Entries)
            {
                if (entry.IsVanilla || HasVanillaReservationRelease(entry.UnitType)) continue;
                (table ?? (table = new byte[ushort.MaxValue + 1]))[entry.UnitType] = 1;
            }
            return table;
        }

        // Per-type flags for the native gates; null when no entry uses the option.
        internal static byte[] BuildFlagTable(HuntablePreyCatalog catalog, Func<HuntablePreyEntry, bool> selected, byte value = 1)
        {
            byte[] table = null;
            foreach (var entry in catalog.Entries)
                if (selected(entry)) (table ?? (table = new byte[ushort.MaxValue + 1]))[entry.UnitType] = value;
            return table;
        }
    }
}