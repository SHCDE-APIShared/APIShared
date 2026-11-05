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
    // Provenance is cumulative over every real participant in every builder tick.
    // A mixed/unknown build can never become additional by changing its final crew.
    internal static unsafe class AiMilitaryEngineeringRuntime
    {
        internal const string SiteName = "ai-military-engineering-provenance";
        private const int BuildingManager = 0x64CCBB0, BuildingStride = 0x32C;
        private const int UnitManager = 0x67E8400, UnitStride = 0x490;
        private static readonly int[] Rvas = { 0xA65A0, 0xA8E40, 0xA93C0, 0xA9A30, 0xAA030, 0xAA630 };
        private static readonly eChimps[] Types = { eChimps.CHIMP_TYPE_ARAB_BALLISTA, eChimps.CHIMP_TYPE_CATAPULT,
            eChimps.CHIMP_TYPE_TREBUCHET, eChimps.CHIMP_TYPE_SIEGE_TOWER, eChimps.CHIMP_TYPE_BATTERING_RAM, eChimps.CHIMP_TYPE_PORTABLE_SHIELD };
        private static readonly int[] Crew = { 2, 2, 3, 4, 4, 1 };
        private static readonly string[] EntryHex = { "40535641544157", "40535641544157", "405356574155", "48895C242056", "48895C242056", "48895C242056" };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Builder();
        private static readonly Builder[] Callbacks = { () => Invoke(0), () => Invoke(1), () => Invoke(2), () => Invoke(3), () => Invoke(4), () => Invoke(5) };
        private static readonly DetourHandle<Builder>[] Hooks = new DetourHandle<Builder>[6];
        private static readonly AiEngineeringNativeGate[] Gates = new AiEngineeringNativeGate[6];
        private static readonly NativeDetourBackend Backend = new NativeDetourBackend(new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false });
        private static readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
        private static readonly Dictionary<BuildingRef, AiBuildProvenance> Builds = new Dictionary<BuildingRef, AiBuildProvenance>();
        private static readonly Dictionary<AiUnitRef, ProductHistory> Products = new Dictionary<AiUnitRef, ProductHistory>();
        private static readonly Dictionary<AiUnitRef, ReplacementCandidate> Candidates = new Dictionary<AiUnitRef, ReplacementCandidate>();
        private static readonly List<AiUnitRef> RemoveCandidates = new List<AiUnitRef>();
        private static readonly List<AiUnitRef> RemoveProducts = new List<AiUnitRef>();
        private static readonly List<BuildingRef> RemoveBuilds = new List<BuildingRef>();
        private static IntPtr module;
        private static IntPtr trackedBuildings;
        private static Func<AiUnitRef, bool> isAdditional;
        private static Action<AiUnitRef, bool> markProduct;
        private static ManualLogSource logger;
        private static HookTransaction transaction;
        [ThreadStatic] private static BuildScope scope;
        [ThreadStatic] private static ReplacementScope replacement;
        private static long generation;
        internal static bool IsReady => Publication.IsReady;
        internal static NativeSiteSpan[] Spans()
        {
            var result = new NativeSiteSpan[Rvas.Length];
            for (int i = 0; i < result.Length; i++) result[i] = new NativeSiteSpan(Rvas[i], EntryHex[i].Length / 2, NativeSiteMode.Exclusive);
            return result;
        }
        private readonly struct BuildingRef : IEquatable<BuildingRef>
        {
            internal BuildingRef(int id, uint global) { Id = id; Global = global; }
            internal int Id { get; } internal uint Global { get; }
            public bool Equals(BuildingRef other) => Id == other.Id && Global == other.Global;
            public override bool Equals(object other) => other is BuildingRef value && Equals(value);
            public override int GetHashCode() => unchecked(Id * 397 ^ (int)Global);
        }
        private readonly struct ProductHistory
        {
            internal ProductHistory(int type, int owner, bool additional, AiUnitRef[] crew)
            { Type = type; Owner = owner; Additional = additional; Crew = crew; }
            internal int Type { get; } internal int Owner { get; } internal bool Additional { get; }
            internal AiUnitRef[] Crew { get; }
        }
        private struct BuildScope
        {
            internal int Index; internal BuildingRef Building; internal AiBuildProvenance History; internal AiUnitRef Product;
            internal long Generation;
        }
        internal sealed class ReplacementScope
        {
            internal ReplacementScope Previous;
            internal AiUnitRef Source, Created;
            internal int Type, Owner;
            internal long Generation;
            internal AiUnitRef[] Crew;
            internal bool Ambiguous;
        }
        private readonly struct ReplacementCandidate
        {
            internal ReplacementCandidate(AiUnitRef source, int type, int owner, AiUnitRef[] crew)
            { Source = source; Type = type; Owner = owner; Crew = crew; }
            internal AiUnitRef Source { get; }
            internal int Type { get; }
            internal int Owner { get; }
            internal AiUnitRef[] Crew { get; }
        }
        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log,
            Func<AiUnitRef, bool> additional, Action<AiUnitRef, bool> productMarker)
        {
            Publication.ThrowIfUnavailable(); if (Publication.IsReady) return;
            if (!string.Equals(hash, Quota.AiQuotaNativeContract.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Engineering provenance requires the audited image.");
            if (additional == null || productMarker == null) throw new ArgumentNullException(nameof(additional));
            module = context.ModuleHandle; logger = log; isAdditional = additional; markProduct = productMarker;
            var spans = new NativeSiteSpan[6];
            for (int i = 0; i < Rvas.Length; i++)
            {
                byte[] bytes = Quota.AiQuotaNativeContract.Hex(EntryHex[i]);
                if (!context.Memory.Slice(Rvas[i], bytes.Length).SequenceEqual(bytes)) throw new InvalidOperationException("Engineering load-image entry differs.");
                NativeIndirectEntry.VerifyBytes(module + Rvas[i], bytes);
                spans[i] = new NativeSiteSpan(Rvas[i], bytes.Length, NativeSiteMode.Exclusive);
            }
            var claim = NativeSiteRegistry.Register(APISharedPlugin.PluginGuid, SiteName, spans);
            if (!claim.Succeeded) throw new InvalidOperationException(claim.Reason);
            try
            {
                trackedBuildings = Marshal.AllocHGlobal(4000 * 4); new Span<byte>((void*)trackedBuildings, 4000 * 4).Clear();
                transaction = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true,
                        InstallOnCommit = false, Backend = NativeStartupPatchWindow.Backend(Backend) });
                for (int i = 0; i < Hooks.Length; i++)
                {
                    Gates[i] = new AiEngineeringNativeGate(Address(0), unchecked((ulong)Quota.AiMilitaryQuotaRuntime.DemandAddress.ToInt64()),
                        unchecked((ulong)trackedBuildings.ToInt64()), unchecked((ulong)Marshal.GetFunctionPointerForDelegate(Callbacks[i]).ToInt64()));
                    Hooks[i] = new DetourHandle<Builder>(); transaction.AddDetour(Hooks[i], HookTarget.FromAddress(Address(Rvas[i])), Callbacks[i], Gates[i]);
                }
                var result = transaction.Commit();
                if (!result.IsCompleteSuccess) throw new InvalidOperationException("Engineering native preparation incomplete.");
                for (int i = 0; i < Hooks.Length; i++)
                {
                    Validate(i, false);
                }
                // Gates are already held by the static owner. The publication rollback
                // guard preserves all of them after even a partial executable write.
                NativeHookPublication.Enable(transaction, result, Publication, typeof(AiMilitaryEngineeringRuntime), () => {
                    for (int i = 0; i < Hooks.Length; i++) Validate(i, true);
                });
                AiFixesReplacementObserver.Initialize();
            }
            catch
            {
                Publication.RollbackUnpublished(transaction, () => {
                    NativeSiteRegistry.Unregister(APISharedPlugin.PluginGuid, SiteName); transaction = null;
                    Marshal.FreeHGlobal(trackedBuildings); trackedBuildings = IntPtr.Zero;
                });
                throw;
            }
        }
        private static ulong Address(int rva) => unchecked((ulong)(module.ToInt64() + rva));
        private static void Validate(int index, bool installed)
        {
            var native = Hooks[index].Hook as NativeDetour<Builder>;
            if (!Hooks[index].Success) throw new InvalidOperationException("Engineering Indirect entry backend differs.");
            byte[] bytes = Quota.AiQuotaNativeContract.Hex(EntryHex[index]);
            AiEngineeringNativeGate.ValidateEntry(Hooks[index].Hook, module + Rvas[index], bytes, installed);
            Gates[index].Validate(native.HookEntryPointAddress, unchecked((ulong)native.TrampolineAddress.ToInt64()));
        }
        private static void Invoke(int index)
        {
            BuildScope previous = scope; scope = default;
            if (Publication.IsReady)
            {
                try { scope = Begin(index); } catch (Exception error) { Report(error); }
            }
            bool completed = false;
            try { Hooks[index].Original(); completed = true; }
            finally
            {
                try { if (completed && scope.History != null && scope.Generation == generation) Complete(scope); }
                catch (Exception error) { Report(error); }
                finally { scope = previous; }
            }
        }
        private static BuildScope Begin(int index)
        {
            int id = *(int*)(module + 0x8F9BE4); if (id <= 0 || id >= 4000) return default;
            byte* building = (byte*)(module + BuildingManager + id * BuildingStride);
            var key = new BuildingRef(id, *(uint*)(building + 0x134)); if (key.Global == 0) return default;
            if (!Builds.TryGetValue(key, out AiBuildProvenance history))
            {
                // An archive containing a progressed tent without our record has unknown
                // earlier workers. Treat it conservatively as regular, never ALL=true.
                history = new AiBuildProvenance((int)Types[index], *(int*)(building + 0x2D4) <= 0); Builds.Add(key, history);
                ((uint*)trackedBuildings)[id] = key.Global;
            }
            if (history.Type != (int)Types[index]) history.Known = false;
            int count = *(short*)(building + 0x1F6);
            if (count < 0 || count > 4) { history.Known = false; count = Math.Max(0, Math.Min(4, count)); }
            for (int i = 0; i < count; i++)
            {
                int worker = *(ushort*)(building + 0x1FA + i * 2); uint gid = *(uint*)(building + 0x204 + i * 4);
                var reference = new AiUnitRef(worker, gid);
                if (!TryUnit(reference, out byte* raw) || *(ushort*)(raw + 0x6E6) != (int)eChimps.CHIMP_TYPE_ENGINEER)
                { history.Observe(false, false); continue; }
                history.Observe(true, isAdditional(reference));
            }
            return new BuildScope { Index = index, Building = key, History = history, Generation = generation };
        }
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
        private static void Complete(BuildScope value)
        {
            if (value.Product.UnitId == 0) return;
            if (!TryUnit(value.Product, out byte* raw)) return;
            int owner = *(ushort*)(raw + 0x6EE);
            AiUnitRef[] crew = ReadCrew(raw, Crew[value.Index]);
            bool extra = value.History.IsAdditional;
            // A complete native builder may release mismatched crews and leave the new
            // machine empty. Historical provenance remains useful, but never imply that
            // it is a fully staffed/admissible machine.
            markProduct(value.Product, extra);
            ReplaceProductHistory(value.Product, new ProductHistory((int)Types[value.Index], owner, extra, crew));
            Builds.Remove(value.Building);
            if (((uint*)trackedBuildings)[value.Building.Id] == value.Building.Global) ((uint*)trackedBuildings)[value.Building.Id] = 0;
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
        internal static void Clear(long worldGeneration)
        { generation = worldGeneration; Builds.Clear(); Products.Clear(); Candidates.Clear(); RemoveCandidates.Clear(); RemoveProducts.Clear(); RemoveBuilds.Clear(); scope = default; replacement = null;
            if (trackedBuildings != IntPtr.Zero) new Span<byte>((void*)trackedBuildings, 4000 * 4).Clear(); }
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
        { int id = reader.ReadInt32(); uint gid = reader.ReadUInt32(); if (id <= 0 || id >= 10000 || gid == 0) throw new InvalidDataException("Invalid provenance unit."); return new AiUnitRef(id, gid); }
        private static int ReadCount(BinaryReader reader, int maximum)
        { int count = reader.ReadInt32(); if (count < 0 || count > maximum) throw new InvalidDataException("Invalid provenance count."); return count; }
        private static void Report(Exception error)
        { try { Internal.RuntimeHealth.Failure(logger, "ai-military-engineering", true, "AI engineering provenance failed: " + error); } catch { } }
        internal static void ReportIntegration(Exception error) => Report(error);
    }
}
