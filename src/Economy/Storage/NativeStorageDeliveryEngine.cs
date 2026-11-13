using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Goods;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    // Immediate typed views, resolved by the shared producer owner and rechecked before any
    // preparation. This is not a retainable/public transaction capability or a cargo registry.
    internal readonly unsafe struct NativeStorageDeliveryActor
    {
        internal readonly GameUnit* Worker;
        internal readonly GameBuilding* Home;
        internal readonly int WorkerId, HomeId;
        internal readonly uint WorkerGlobalId, HomeGlobalId;
        internal NativeStorageDeliveryActor(GameUnit* worker, int workerId, uint workerGlobalId,
            GameBuilding* home, int homeId, uint homeGlobalId)
        { Worker = worker; WorkerId = workerId; WorkerGlobalId = workerGlobalId; Home = home; HomeId = homeId; HomeGlobalId = homeGlobalId; }

        internal short* CargoCell => (short*)((byte*)Worker + NativeStorageDeliveryEngine.CargoOffset);
        internal bool TryRead(NativeStorageProbe destination, out short cargo)
        {
            cargo = 0;
            int player = destination.PlayerId;
            if (player < 1 || player > 8 || Worker == null || Home == null || WorkerId < 1 || WorkerId > 9999 || HomeId < 1 || HomeId > 3999 ||
                WorkerGlobalId == 0 || HomeGlobalId == 0 || Worker->r_GlobalId != WorkerGlobalId || Home->r_GlobalId != HomeGlobalId ||
                !UnitAccess.IsReallyAlive(Worker) || Home->r_AliveState != AliveState.IsAlive ||
                Worker->r_ControllableForPlayerId != player || Home->r_PlayerIdOwner != player ||
                Worker->r_LinkedProductionBuildingId != HomeId || Worker->r_LinkedProductionBuildingGlobalId != HomeGlobalId ||
                *(ushort*)((byte*)Worker + NativeStorageDeliveryEngine.DestinationSlotOffset) != destination.Slot ||
                *(uint*)((byte*)Worker + NativeStorageDeliveryEngine.DestinationGlobalOffset) != destination.GlobalId ||
                !MatchesRole(Home->r_BuildingType, Worker->r_UnitChimp)) return false;
            cargo = *CargoCell;
            return cargo > 0;
        }
        private static bool MatchesRole(eStructs home, eChimps worker)
        {
            switch (home)
            {
                case eStructs.STRUCT_FLETCHERS_WORKSHOP: return worker == eChimps.CHIMP_TYPE_FLETCHER;
                case eStructs.STRUCT_POLETURNERS_WORKSHOP: return worker == eChimps.CHIMP_TYPE_POLETURNER;
                case eStructs.STRUCT_BLACKSMITHS_WORKSHOP: return worker == eChimps.CHIMP_TYPE_BLACKSMITH;
                case eStructs.STRUCT_ARMOURERS_WORKSHOP: return worker == eChimps.CHIMP_TYPE_ARMOURER;
                case eStructs.STRUCT_TANNERS_WORKSHOP: return worker == eChimps.CHIMP_TYPE_TANNER;
                default: return false;
            }
        }
    }

    // Each qualified native invocation attempts one item. Native cargo is the durable progress
    // checkpoint: refusal leaves it unchanged; success c -> c-1 is the next call's distinct work.
    internal sealed unsafe class NativeStorageDeliveryEngine
    {
        internal const int CargoOffset = 0x384;
        internal const int DestinationSlotOffset = 0x332, DestinationGlobalOffset = 0x9C;
        private static readonly object warmGate = new object();
        private static bool warmed;
        private readonly IGoodsWalletBackend backend;
        internal NativeStorageDeliveryEngine(IGoodsWalletBackend backend)
        { this.backend = backend ?? throw new ArgumentNullException(nameof(backend)); }

        internal static bool ValidateLayout() => typeof(GameUnit).StructLayoutAttribute?.Value == LayoutKind.Sequential &&
            typeof(GameUnit).StructLayoutAttribute.Pack == 1 && Marshal.SizeOf(typeof(GameUnit)) == 0x490 &&
            HasField(CargoOffset, typeof(ushort), typeof(short), typeof(uint), typeof(int)) &&
            HasField(DestinationSlotOffset, typeof(ushort), typeof(short)) &&
            (HasField(DestinationGlobalOffset, typeof(uint), typeof(int)) ||
                HasField(DestinationGlobalOffset, typeof(ushort), typeof(short)) &&
                HasField(DestinationGlobalOffset + 2, typeof(ushort), typeof(short)));

        // Unnamed SDK fields have been renamed between compatible releases. Qualify exact
        // primitive storage locations; native semantics come from the separately audited code.
        private static bool HasField(int offset, params Type[] accepted)
        {
            foreach (FieldInfo field in typeof(GameUnit).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (Marshal.OffsetOf(typeof(GameUnit), field.Name).ToInt32() != offset) continue;
                foreach (Type type in accepted) if (field.FieldType == type) return true;
            }
            return false;
        }

        private bool Context(out object world, out string reason)
        {
            world = null;
            object monitor = backend.EngineMonitor;
            if (!backend.IsReady || monitor == null || !Monitor.IsEntered(monitor) || !backend.HasAuthorityContext)
            { reason = "Delivery requires a ready engine-protected authoritative world."; return false; }
            world = backend.World;
            reason = world == null ? "Delivery has no confirmed native world." : string.Empty;
            return world != null;
        }

        internal bool TryDeliver(NativeStorageState state, CustomGoodId good, object expectedWorld, NativeStorageProbe destination,
            NativeStorageDeliveryActor actor, out GoodsTransactionStatus status, out string reason)
        {
            status = GoodsTransactionStatus.InvalidRequest; reason = string.Empty;
            NativeStorageState.PreparedExchange prepared = null;
            short before = 0;
            bool commit = false;
            try
            {
                if (state == null || destination.Kind != StoragePool.Armory || !destination.IsLive ||
                    !Context(out object world, out reason) || !ReferenceEquals(world, expectedWorld))
                { if (reason.Length == 0) reason = "Delivery requires this world's exact live Armory."; return false; }
                WarmCommitPath();
                if (!actor.TryRead(destination, out before))
                { reason = "The live worker, workshop, owner or signed cargo does not match."; return false; }
                long last = state.GetLastSequence(good.OwnerGuid);
                if (last == long.MaxValue) { status = GoodsTransactionStatus.LimitReached; reason = "The owner's storage sequence is exhausted."; return false; }
                byte[] intent = Intent(actor, destination, before);
                status = state.PrepareDelivery(good, last + 1, destination, intent, out prepared, out reason);
                if (status != GoodsTransactionStatus.Applied || prepared == null) return false;
                if (!prepared.IsPrepared || !Context(out object finalWorld, out reason) || !ReferenceEquals(world, finalWorld) ||
                    !actor.TryRead(destination, out short finalCargo) || finalCargo != before)
                { status = GoodsTransactionStatus.InvalidRequest; reason = "Delivery identity, cargo or world changed before publication."; return false; }
                commit = true;
            }
            catch (Exception error)
            { status = GoodsTransactionStatus.InvalidRequest; reason = "Delivery preparation failed: " + error.Message; }
            finally { if (!commit) prepared?.Dispose(); }
            if (!commit) return false;
            // No normal failure is translated into a refusal after the native cargo store.
            using (prepared) PublishCargoAndStorage(actor.CargoCell, (short)(before - 1), prepared);
            reason = string.Empty; return true;
        }

        private static byte[] Intent(NativeStorageDeliveryActor actor, NativeStorageProbe destination, short cargo)
        {
            using (var stream = new MemoryStream(64))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)3); writer.Write((byte)1);
                writer.Write(actor.WorkerId); writer.Write(actor.WorkerGlobalId);
                writer.Write(actor.HomeId); writer.Write(actor.HomeGlobalId);
                writer.Write((int)actor.Worker->r_UnitChimp); writer.Write((int)actor.Home->r_BuildingType);
                writer.Write(destination.Slot); writer.Write(destination.GlobalId); writer.Write(destination.PlayerId);
                writer.Write((int)destination.Kind); writer.Write(cargo);
                return stream.ToArray();
            }
        }

        internal static void WarmCommitPath()
        {
            lock (warmGate)
            {
                if (warmed) return;
                if (!ValidateLayout()) throw new InvalidOperationException("The packed native delivery actor layout differs.");
                var scratch = new NativeStorageState();
                var good = new CustomGoodId("APIShared.Storage.DeliveryWarmup", "scratch");
                if (!scratch.TryRegister(good, StoragePool.Armory, out string reason)) throw new InvalidOperationException(reason);
                var probe = new NativeStorageProbe(true, 1, 1, 1, StoragePool.Armory, 0, 50);
                if (scratch.PrepareDelivery(good, 1, probe, new byte[] { 3, 1 }, out var plan, out reason) != GoodsTransactionStatus.Applied || plan == null)
                    throw new InvalidOperationException(reason);
                short cargo = 1;
                using (plan) PublishCargoAndStorage(&cargo, 0, plan);
                warmed = true;
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PublishCargoAndStorage(short* cell, short target, NativeStorageState.PreparedExchange prepared)
        { *cell = target; prepared.Publish(); }
    }
}
