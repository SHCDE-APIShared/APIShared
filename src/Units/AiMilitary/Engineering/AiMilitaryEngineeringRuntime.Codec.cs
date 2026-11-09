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
        internal static byte[] Capture()
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1); writer.Write(Builds.Count);
                var builds = new List<KeyValuePair<BuildingRef, AiBuildProvenance>>(Builds);
                builds.Sort((a, b) => a.Key.Id != b.Key.Id ? a.Key.Id.CompareTo(b.Key.Id) : a.Key.Global.CompareTo(b.Key.Global));
                foreach (var pair in builds)
                {
                    writer.Write(pair.Key.Id); writer.Write(pair.Key.Global); writer.Write(pair.Value.Type);
                    writer.Write(pair.Value.Known); writer.Write(pair.Value.HasContributor); writer.Write(pair.Value.AllAdditional);
                }
                var products = new List<KeyValuePair<AiUnitRef, ProductHistory>>(Products); products.Sort((a, b) => CompareRef(a.Key, b.Key));
                writer.Write(products.Count);
                foreach (var pair in products)
                { WriteUnit(writer, pair.Key); writer.Write(pair.Value.Type); writer.Write(pair.Value.Owner); writer.Write(pair.Value.Additional);
                    writer.Write(pair.Value.Crew.Length); foreach (var worker in pair.Value.Crew) WriteUnit(writer, worker); }
                var candidates = new List<AiUnitRef>(Candidates.Keys); candidates.Sort(CompareRef); writer.Write(candidates.Count);
                foreach (var unit in candidates)
                {
                    var pending = Candidates[unit]; WriteUnit(writer, unit); WriteUnit(writer, pending.Source);
                    writer.Write(pending.Type); writer.Write(pending.Owner); writer.Write(pending.Crew.Length);
                    foreach (var worker in pending.Crew) WriteUnit(writer, worker);
                }
                return stream.ToArray();
            }
        }
        internal static void Restore(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            // Parse fully before changing live state; malformed snapshots are atomic failures.
            var builds = new Dictionary<BuildingRef, AiBuildProvenance>(); var products = new Dictionary<AiUnitRef, ProductHistory>(); var candidates = new Dictionary<AiUnitRef, ReplacementCandidate>();
            var assignedCrew = new HashSet<AiUnitRef>();
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported engineering provenance schema.");
                int count = ReadCount(reader, 4000);
                for (int i = 0; i < count; i++)
                {
                    int id = reader.ReadInt32(); uint gid = reader.ReadUInt32(); int type = reader.ReadInt32();
                    if (id <= 0 || id >= 4000 || gid == 0 || !IsMachine(type)) throw new InvalidDataException("Invalid builder identity.");
                    var history = new AiBuildProvenance(type, reader.ReadBoolean()) { HasContributor = reader.ReadBoolean(), AllAdditional = reader.ReadBoolean() };
                    builds.Add(new BuildingRef(id, gid), history);
                }
                count = ReadCount(reader, 10000);
                for (int i = 0; i < count; i++)
                {
                    AiUnitRef unit = ReadUnit(reader); int type = reader.ReadInt32(), owner = reader.ReadInt32(); bool additional = reader.ReadBoolean();
                    int size = ReadCount(reader, 4); if (!IsMachine(type) || owner < 0 || owner > 8 || size != 0 && size != RequiredCrew(type)) throw new InvalidDataException("Invalid product provenance.");
                    var crew = new AiUnitRef[size]; for (int w = 0; w < size; w++)
                    { crew[w] = ReadUnit(reader); if (!assignedCrew.Add(crew[w])) throw new InvalidDataException("Overlapping product crew identities."); }
                    products.Add(unit, new ProductHistory(type, owner, additional, crew));
                }
                count = ReadCount(reader, 10000);
                for (int i = 0; i < count; i++)
                {
                    var unit = ReadUnit(reader); var source = ReadUnit(reader); int type = reader.ReadInt32(), owner = reader.ReadInt32(), size = ReadCount(reader, 4);
                    if (unit.Equals(source) || !IsMachine(type) || owner < 1 || owner > 8 || size != RequiredCrew(type)) throw new InvalidDataException("Invalid scoped replacement.");
                    var crew = new AiUnitRef[size]; for (int w = 0; w < size; w++) { crew[w] = ReadUnit(reader);
                        for (int j = 0; j < w; j++) if (crew[j].Equals(crew[w])) throw new InvalidDataException("Duplicate replacement crew identity."); }
                    candidates.Add(unit, new ReplacementCandidate(source, type, owner, crew));
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing engineering provenance bytes.");
            }
            Builds.Clear(); foreach (var pair in builds) Builds.Add(pair.Key, pair.Value);
            if (trackedBuildings != IntPtr.Zero)
            {
                new Span<byte>((void*)trackedBuildings, 4000 * 4).Clear();
                foreach (var pair in builds) ((uint*)trackedBuildings)[pair.Key.Id] = pair.Key.Global;
            }
            Products.Clear(); foreach (var pair in products) Products.Add(pair.Key, pair.Value);
            Candidates.Clear(); foreach (var candidate in candidates) Candidates.Add(candidate.Key, candidate.Value);
        }
        private static int CompareRef(AiUnitRef a, AiUnitRef b) => a.UnitId != b.UnitId ? a.UnitId.CompareTo(b.UnitId) : a.GlobalId.CompareTo(b.GlobalId);
        private static void WriteUnit(BinaryWriter writer, AiUnitRef unit) { writer.Write(unit.UnitId); writer.Write(unit.GlobalId); }
        private static AiUnitRef ReadUnit(BinaryReader reader)
        { int id = reader.ReadInt32(); uint gid = reader.ReadUInt32(); if (id <= 0 || id >= AiMilitaryService.UnitIdLimit || gid == 0) throw new InvalidDataException("Invalid provenance unit."); return new AiUnitRef(id, gid); }
        private static int ReadCount(BinaryReader reader, int maximum)
        { int count = reader.ReadInt32(); if (count < 0 || count > maximum) throw new InvalidDataException("Invalid provenance count."); return count; }
    }
}
