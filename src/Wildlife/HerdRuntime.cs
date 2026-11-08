using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.Simulation;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Wildlife
{
    // Herd records, spawning, route events and flight marks (static audit, hash FBCB9319, 2026-11-08; see
    // docs/HERDS.md and UpdateToNewDLL.md "Herds"). Herds are tribe records of Vanilla's tribe manager
    // 0x7CC6720; the Extender's GameTribe starts 0x2A after a raw record, so raw +0x42 (kind) is GameTribe
    // +0x18 and raw +0x5F4 (flight) is +0x5CA. Route events and flight gates install on their first
    // registration (an older ImprovedHunters patches the same spans itself while nobody registers).
    internal static unsafe partial class HerdRuntime
    {
        internal const int ManagerRva = 0x7CC6720, SpawnRva = 0x11E150, GridRva = 0x3395E10;
        internal const int KindOffset = 0x18, FlightOffset = 0x5CA;
        // Member bits by unit ID: 0x271 words cover unit IDs 0 through 9999.
        internal const int MemberWords = 0x271;
        private const int MapTiles = 800;

        // Entry of 0x11E150(manager, art, x, y, height): the three argument spills and the pushes.
        internal static readonly byte[] SpawnEntryBytes = WildlifeInlineSite.Hex("44894C2420 4489442418 89542410 53 55 56 57 4154 4155 4157 4883EC50");

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SpawnCall(IntPtr manager, int art, int tileX, int tileY, int height);

        private static readonly object Gate = new object();
        private static ManualLogSource log;
        private static CrusaderLibraryLoadContext loadContext;
        private static ulong image;
        private static byte* records;
        private static SpawnCall spawn;
        private static IDisposable initializationSubscription, endSubscription;
        private static int available;
        private static string unavailable = "The herd service has not been initialized.";

        internal static bool IsAvailable => Volatile.Read(ref available) != 0;
        internal static string UnavailableReason => IsAvailable ? "" : Volatile.Read(ref unavailable);

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            if (IsAvailable) return;
            log = logger;
            try
            {
                if (!HerdNativeContract.TryValidate(context, hash, out string invalid))
                    throw new InvalidOperationException(invalid);
                loadContext = context;
                image = unchecked((ulong)context.ModuleHandle.ToInt64());
                records = (byte*)&GameTribeManagerAPI.Instance.GetTribeManager().Pointer->GameTribeArray;
                spawn = Marshal.GetDelegateForFunctionPointer<SpawnCall>((IntPtr)(long)(image + SpawnRva));
                // Herd IDs belong to one map: no calm mark outlives it. Rooted for the process lifetime.
                initializationSubscription = MissionEvents.Initialization.Subscribe(_ => ClearAllCalm());
                endSubscription = MissionEvents.Ended.Subscribe(_ => ClearAllCalm());
                Volatile.Write(ref available, 1);
                NativeApiLog.Info(log, "Herds ready: tribe records 0x" + ManagerRva.ToString("X") + ", spawn 0x" + SpawnRva.ToString("X") +
                    "; route events and flight gates install on their first registration.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref unavailable, error.Message);
                NativeApiLog.Error(log, "Herds unavailable: " + error);
            }
        }

        private static GameTribe* Record(int herdId) => (GameTribe*)(records + (long)herdId * sizeof(GameTribe));

        internal static bool IsHerdKind(short kind)
        {
            switch ((HerdKind)kind)
            {
                case HerdKind.Deer: case HerdKind.Lion: case HerdKind.Rabbit: case HerdKind.Camel:
                case HerdKind.Goat: case HerdKind.Hyena: case HerdKind.Crocodile: return true;
                default: return false;
            }
        }

        // An animal herd record in use (record state 2, Vanilla's herd maintenance 0x1261F0 skips 0 and 3).
        private static bool TryRecord(int herdId, out GameTribe* record)
        {
            record = null;
            if (!IsAvailable || herdId < 1 || herdId > Herds.MaximumHerdId) return false;
            var candidate = Record(herdId);
            if (candidate->r_AliveState != AliveState.IsAlive || !IsHerdKind(*(short*)((byte*)candidate + KindOffset))) return false;
            record = candidate;
            return true;
        }

        internal static bool TryRead(int herdId, out HerdInfo herd)
        {
            herd = default;
            if (!TryRecord(herdId, out GameTribe* record)) return false;
            var kind = (HerdKind)(*(short*)((byte*)record + KindOffset));
            bool flees = kind == HerdKind.Deer || kind == HerdKind.Goat || kind == HerdKind.Rabbit;
            herd = new HerdInfo(herdId, record->r_GlobalId, kind, record->r_PlayerIdOwner, record->r_LeaderUnitId, record->r_UnitsInGroup,
                flees && *(short*)((byte*)record + FlightOffset) != 0);
            return true;
        }

        internal static bool TryCopyMembers(int herdId, List<int> unitIds)
        {
            if (unitIds == null) throw new ArgumentNullException(nameof(unitIds));
            unitIds.Clear();
            if (!TryRecord(herdId, out GameTribe* record)) return false;
            CopyMembers(record, unitIds);
            return true;
        }

        private static void CopyMembers(GameTribe* record, List<int> unitIds)
        {
            ushort* words = &record->r_UnitIdsInGroupBitfield;
            for (int word = 0; word < MemberWords; word++)
                for (int bits = words[word], bit = 0; bits != 0; bits >>= 1, bit++)
                    if ((bits & 1) != 0 && word * 16 + bit > 0)
                        unitIds.Add(word * 16 + bit);
        }

        // Vanilla's art arguments of 0x11E150 (cmp esi at 0x11E1D9 onward), one per herd kind.
        internal static int SpawnArt(HerdKind kind)
        {
            switch (kind)
            {
                case HerdKind.Deer: return 0xDC;
                case HerdKind.Lion: return 0xDD;
                case HerdKind.Rabbit: return 0xDE;
                case HerdKind.Camel: return 0xDF;
                case HerdKind.Goat: return 0xE2;
                case HerdKind.Hyena: return 0xE3;
                case HerdKind.Crocodile: return 0xE5;
                default: return 0;
            }
        }

        internal static bool TrySpawn(HerdKind kind, int tileX, int tileY, int maxAnimals, out int herdId, out string reason)
        {
            herdId = 0;
            if (!IsAvailable) { reason = UnavailableReason; return false; }
            if (!SimulationMutationScope.IsTickActive)
            { reason = "Herds spawn only inside a SimulationTickEvents callback."; return false; }
            int art = SpawnArt(kind);
            if (art == 0) { reason = "Unknown herd kind " + (short)kind + "."; return false; }
            if (maxAnimals < 1) { reason = "At least one animal must stay."; return false; }
            GameTileManagerAPI tiles = GameTileManagerAPI.Instance;
            if (!tiles.IsTileInsideMapBounds(tileX, tileY)) { reason = "The tile is outside the map."; return false; }
            int tile = tiles.GetTileId(tileX, tileY);
            if (!tiles.IsValidTileId(tile)) { reason = "The tile is outside the map."; return false; }
            int created = spawn((IntPtr)(long)(image + ManagerRva), art, tileX, tileY, tiles.GetTileHeight(tile));
            if (created < 1 || created > Herds.MaximumHerdId) { reason = "Vanilla refused the place."; return false; }
            KeepFirstAnimals(Record(created), maxAnimals);
            herdId = created;
            reason = "";
            return true;
        }

        // Vanilla always makes a herd of its own size. The rest leaves in the same tick the way Vanilla removes
        // animals over its limit (0x11B460); its herd maintenance 0x1261F0 then takes them out of the herd and
        // picks a leader among the rest (0x123EA0, 0x11A2B0).
        private static void KeepFirstAnimals(GameTribe* record, int maxAnimals)
        {
            var members = new List<int>(32);
            CopyMembers(record, members);
            for (int index = maxAnimals; index < members.Count; index++)
                if (UnitAccess.TryGetById(members[index], out GameUnit* unit, out _) && unit != null)
                    unit->r_AliveState = AliveState.MarkedForDeletion;
        }

        internal static bool IsTileInside(int tileX, int tileY) =>
            tileX >= 0 && tileY >= 0 && tileX < MapTiles && tileY < MapTiles && GameTileManagerAPI.Instance.IsTileInsideMapBounds(tileX, tileY);

        internal static bool TryGetSectorThreats(int tileX, int tileY, out HerdSectorThreats threats)
        {
            threats = default;
            int sectorX = tileX / HerdNativeAssembly.SectorSize, sectorY = tileY / HerdNativeAssembly.SectorSize;
            if (!IsAvailable || tileX < 0 || tileY < 0 || sectorX >= HerdNativeAssembly.SectorsPerSide || sectorY >= HerdNativeAssembly.SectorsPerSide)
                return false;
            byte* sector = (byte*)(image + GridRva) + (sectorX * HerdNativeAssembly.SectorsPerSide + sectorY) * HerdNativeAssembly.SectorStride;
            threats = new HerdSectorThreats(*(int*)(sector + HerdNativeAssembly.SectorUnitsOffset),
                *(int*)(sector + HerdNativeAssembly.SectorPredatorsOffset));
            return true;
        }

        private sealed class Owned<T>
        {
            internal readonly string Owner, Id;
            internal readonly int Order;
            internal readonly T Callback;
            internal int FailureLogged;
            internal Owned(string owner, string id, int order, T callback) { Owner = owner; Id = id; Order = order; Callback = callback; }
        }

        private static bool ValidateRegistration(string owner, string id, object callback, out string reason)
        {
            if (!UnityMainThreadDispatch.IsMainThread)
            { reason = "Herd rules must register on the initialized Unity thread."; return false; }
            if (!IsAvailable) { reason = UnavailableReason; return false; }
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || owner.Length > 128 || id.Length > 128 ||
                StringComparer.Ordinal.Equals(owner, APISharedPlugin.PluginGuid) || callback == null)
            { reason = "A valid consumer GUID, registration ID and callback are required."; return false; }
            reason = "";
            return true;
        }

        private static bool IsDuplicate<T>(Owned<T>[] list, string owner, string id)
        {
            foreach (var old in list)
                if (StringComparer.Ordinal.Equals(old.Owner, owner) && StringComparer.Ordinal.Equals(old.Id, id)) return true;
            return false;
        }

        // Ordered, immutable snapshot (order, then ordinal owner and ID); callers hold Gate.
        private static Owned<T>[] Insert<T>(Owned<T>[] list, Owned<T> item)
        {
            var next = new Owned<T>[list.Length + 1];
            int index = 0;
            bool placed = false;
            foreach (var old in list)
            {
                if (!placed && Compare(item, old) < 0) { next[index++] = item; placed = true; }
                next[index++] = old;
            }
            if (!placed) next[index] = item;
            return next;
        }

        private static int Compare<T>(Owned<T> left, Owned<T> right)
        {
            int result = left.Order.CompareTo(right.Order);
            if (result == 0) result = StringComparer.Ordinal.Compare(left.Owner, right.Owner);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.Id, right.Id);
        }

        private static void ReportFailure<T>(Owned<T> item, string kind, Exception error)
        {
            if (Interlocked.Exchange(ref item.FailureLogged, 1) == 0)
                NativeApiLog.Error(log, "Herd " + kind + " failed and is ignored for this call: owner=" + item.Owner + ", id=" + item.Id + ", error=" + error);
        }
    }
}
