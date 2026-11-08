using System;
using System.IO;
using System.Threading;
using APIShared.Internal;
using APIShared.Savegames;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Units;
using SHCDESE.Interop;

namespace APIShared.Wildlife
{
    // Target policies, Hunter meat yields and the save record of butchered animals.
    internal static unsafe partial class HuntablePreyRuntime
    {
        internal const int MaximumBaseMeat = 1000;
        private const string SaveIdentifier = "APIShared.Wildlife.HunterPrey";
        private const string ButcheredEntry = "butchered";

        private sealed class Owned<T>
        {
            internal readonly string Owner, Id;
            internal readonly int Order;
            internal readonly T Callback;
            internal int FailureLogged;
            internal Owned(string owner, string id, int order, T callback) { Owner = owner; Id = id; Order = order; Callback = callback; }
        }

        private static Owned<HunterTargetPolicy>[] policies = Array.Empty<Owned<HunterTargetPolicy>>();
        private static Owned<Action<HunterMeatYield>>[] meatHandlers = Array.Empty<Owned<Action<HunterMeatYield>>>();
        private static int meatDemand;

        internal static bool TryRegisterPolicy(string owner, string id, HunterTargetPolicy policy, int order, out string reason)
        {
            if (!ValidateRuleRegistration(owner, id, policy, out reason)) return false;
            lock (Gate)
            {
                if (!TryAdd(ref policies, new Owned<HunterTargetPolicy>(owner, id, order, policy), out reason)) return false;
            }
            NativeApiLog.Info(log, "Hunter target policy registered: owner=" + owner + ", id=" + id + ", order=" + order + ".");
            return true;
        }

        internal static bool TryRegisterMeatHandler(string owner, string id, Action<HunterMeatYield> handler, int order, out string reason)
        {
            if (!ValidateRuleRegistration(owner, id, handler, out reason)) return false;
            lock (Gate)
            {
                if (!TryAdd(ref meatHandlers, new Owned<Action<HunterMeatYield>>(owner, id, order, handler), out reason)) return false;
            }
            UpdateMeatDemand();
            NativeApiLog.Info(log, "Hunter meat yield handler registered: owner=" + owner + ", id=" + id + ", order=" + order + ".");
            return true;
        }

        private static bool ValidateRuleRegistration(string owner, string id, object callback, out string reason)
        {
            if (!UnityMainThreadDispatch.IsMainThread)
            { reason = "Hunter rules must register on the initialized Unity thread."; return false; }
            if (!IsAvailable || !HunterNativeSites.IsAvailable)
            { reason = IsAvailable ? HunterNativeSites.Diagnostic : Volatile.Read(ref unavailable); return false; }
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || owner.Length > 128 || id.Length > 128 ||
                StringComparer.Ordinal.Equals(owner, APISharedPlugin.PluginGuid) || callback == null)
            { reason = "A valid consumer GUID, registration ID and callback are required."; return false; }
            reason = "";
            return true;
        }

        // Ordered, immutable snapshot; callers hold Gate.
        private static bool TryAdd<T>(ref Owned<T>[] list, Owned<T> item, out string reason)
        {
            foreach (var old in list)
                if (StringComparer.Ordinal.Equals(old.Owner, item.Owner) && StringComparer.Ordinal.Equals(old.Id, item.Id))
                { reason = "This owner already registered that ID."; return false; }
            var next = new Owned<T>[list.Length + 1];
            int index = 0;
            bool placed = false;
            foreach (var old in list)
            {
                if (!placed && Compare(item, old) < 0) { next[index++] = item; placed = true; }
                next[index++] = old;
            }
            if (!placed) next[index] = item;
            Volatile.Write(ref list, next);
            reason = "";
            return true;
        }

        private static int Compare<T>(Owned<T> left, Owned<T> right)
        {
            int result = left.Order.CompareTo(right.Order);
            if (result == 0) result = StringComparer.Ordinal.Compare(left.Owner, right.Owner);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.Id, right.Id);
        }

        // Runs inside Vanilla's query on the simulation thread (also for the local speech query).
        private static void ApplyPolicies(UnitHunterQueryTargetEventArgs args, GameUnit* candidate, HuntablePreyCatalog catalog,
            Owned<HunterTargetPolicy>[] list)
        {
            if (!HunterNativeSites.TryGetCurrentQuery(out int hunter, out bool simulation)) return;
            int type = (int)candidate->r_UnitChimp;
            var context = new HunterTargetCandidate(hunter, args.QueryUnitId, type, catalog.IsHuntable(type), simulation);
            bool allow = false, refuse = false;
            foreach (var item in list)
            {
                HunterTargetDecision decision;
                try { decision = item.Callback(in context); }
                catch (Exception error) { ReportFailure(item, "target policy", error); continue; }
                if (decision == HunterTargetDecision.Refuse) refuse = true;
                else if (decision == HunterTargetDecision.Allow) allow = true;
            }
            if (refuse) args.IsValidTarget = false;
            else if (allow && context.IsHuntable) args.IsValidTarget = true;
        }

        private static void ReportFailure<T>(Owned<T> item, string kind, Exception error)
        {
            if (Interlocked.Exchange(ref item.FailureLogged, 1) == 0)
                NativeApiLog.Error(log, "Hunter " + kind + " failed and is ignored for this call: owner=" + item.Owner + ", id=" + item.Id + ", error=" + error);
        }

        // Capture is needed while a mission is active and any yield can differ from Vanilla.
        private static void UpdateMeatDemand()
        {
            var catalog = Volatile.Read(ref active);
            bool needed = Volatile.Read(ref worldActive) != 0 && (Volatile.Read(ref meatHandlers).Length != 0 || HasBaseMeat(catalog));
            Volatile.Write(ref meatDemand, needed ? 1 : 0);
            HunterNativeSites.SetCaptureDemand(needed);
        }

        private static bool HasBaseMeat(HuntablePreyCatalog catalog)
        {
            foreach (var entry in catalog.Entries) if (entry.BaseMeat.HasValue) return true;
            return false;
        }

        // Vanilla's only Hunter yield: 0x18D940(hunter, 6, 1) at 0x1309FE, when he takes the hut's meat.
        // The Extender's Pre event passes a changed base to the original, so Vanilla's bonus applies to it.
        private static void OnYield(UnitCalculateBonusYieldEventArgs args)
        {
            if (Volatile.Read(ref meatDemand) == 0 || args.Phase != EventHookPhase.Pre) return;
            if (!UnitAccess.TryGetById(args.UnitId, out GameUnit* hunter, out _) || hunter->r_UnitChimp != eChimps.CHIMP_TYPE_HUNTER ||
                !HunterNativeSites.TryGetButchered(args.UnitId, out uint hunterGlobal, out uint corpseGlobal, out int preyType) ||
                hunterGlobal != hunter->r_GlobalId || corpseGlobal == 0 || corpseGlobal != hunter->r_AI_ContextTargetUnitGlobalId)
                return;
            var catalog = Volatile.Read(ref active);
            int vanilla = args.GoodAmount;
            int amount = catalog.TryGet(preyType, out var entry) && entry.BaseMeat.HasValue ? entry.BaseMeat.Value : vanilla;
            var handlers = Volatile.Read(ref meatHandlers);
            if (handlers.Length != 0)
            {
                var yield = new HunterMeatYield(args.UnitId, preyType, vanilla, amount);
                foreach (var item in handlers)
                {
                    int before = yield.BaseAmount;
                    try { item.Callback(yield); }
                    catch (Exception error) { yield.BaseAmount = before; ReportFailure(item, "meat yield handler", error); }
                }
                amount = yield.BaseAmount;
            }
            args.GoodAmount = Math.Max(0, Math.Min(MaximumBaseMeat, amount));
        }

        private static void RegisterSaveRecord()
        {
            var registration = new VersionedSaveDataRegistration(APISharedPlugin.PluginGuid, SaveIdentifier, 1, 1,
                new[] { ButcheredEntry }, CaptureButchered, OnButcheredLoaded);
            if (!VersionedSaveData.TryRegister(registration, out _, out string reason))
                throw new InvalidOperationException("Hunter prey save record: " + reason);
        }

        // Only records of living Hunters that still hold the butchered corpse as target can matter.
        private static bool CaptureButchered(VersionedSaveWriter writer)
        {
            using (var data = new MemoryStream())
            using (var output = new BinaryWriter(data))
            {
                output.Write(0);
                int count = 0;
                for (int id = 1; id < HunterNativeSites.UnitCapacity; id++)
                {
                    if (!HunterNativeSites.TryGetButchered(id, out uint hunterGlobal, out uint corpseGlobal, out int type) ||
                        !UnitAccess.TryGetById(id, out GameUnit* hunter, out _) || hunter->r_UnitChimp != eChimps.CHIMP_TYPE_HUNTER ||
                        hunter->r_GlobalId != hunterGlobal || hunter->r_AI_ContextTargetUnitGlobalId != corpseGlobal)
                        continue;
                    output.Write((ushort)id); output.Write(hunterGlobal); output.Write(corpseGlobal); output.Write(type);
                    count++;
                }
                output.Flush();
                data.Position = 0;
                output.Write(count);
                output.Flush();
                writer.Write(ButcheredEntry, data.ToArray());
            }
            return true;
        }

        private static void OnButcheredLoaded(VersionedSaveLoad load)
        {
            HunterNativeSites.ClearTable();
            if (load.Status != VersionedSaveLoadStatus.Restored || !load.Entries.TryGetValue(ButcheredEntry, out byte[] bytes)) return;
            try
            {
                using (var input = new BinaryReader(new MemoryStream(bytes)))
                {
                    int count = input.ReadInt32();
                    if (count < 0 || count > HunterNativeSites.UnitCapacity || bytes.Length != 4 + count * 14)
                        throw new InvalidDataException("Unexpected record size.");
                    for (int i = 0; i < count; i++)
                        HunterNativeSites.SetButchered(input.ReadUInt16(), input.ReadUInt32(), input.ReadUInt32(), input.ReadInt32());
                }
            }
            catch (Exception error)
            {
                HunterNativeSites.ClearTable();
                NativeApiLog.Error(log, "Hunter prey save record ignored; affected yields keep Vanilla's base: " + error.Message);
            }
        }
    }
}
