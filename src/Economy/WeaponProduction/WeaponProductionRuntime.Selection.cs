using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.Networking;
using APIShared.Simulation;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    internal static unsafe partial class WeaponProductionRuntime
    {
        internal static bool TryGetSelection(int id, uint gid, out WeaponProductionSelection selection)
        {
            selection = null;
            if (!HasHudDemand || engine == null) return false;
            lock (engine)
            {
                if (!TryBuilding(id, gid, out var building) || active.For(building->r_BuildingType).Count == 0) return false;
                var state = FindState(id, gid, building->r_BuildingType);
                var current = HasCurrentBatch(state) ? state.Current.Option : ReadVanilla(building, currentOffset);
                var next = state?.Next ?? ReadVanilla(building, nextOffset);
                selection = new WeaponProductionSelection(current, next);
                return true;
            }
        }

        internal static NetworkSendResult RequestSelection(int id, uint gid, WeaponProductionOptionId key, bool group)
        {
            if (!UnityMainThreadDispatch.IsMainThread || engine == null || channel == null)
                return ChoreSendPolicy.Result(NetworkSendStatus.Unavailable, "Weapon-workshop submission is unavailable.");
            lock (engine)
            {
                int index = active.IndexOf(key);
                var option = active.At(index);
                int player = PlayerPerspectiveAPI.GetControlledPlayerId();
                if (player <= 0 || player != PlayerPerspectiveAPI.GetViewedPlayerId() ||
                    !TryBuilding(id, gid, out var building) || building->r_PlayerIdOwner != player ||
                    option == null || building->r_BuildingType != option.BuildingType || !IsOptionAllowed(option))
                    return ChoreSendPolicy.Result(NetworkSendStatus.Invalid, "The workshop or recipe is not selectable by this player.");
                return channel.Send(new WeaponProductionPacket(active.Digest, index, group ? 0 : id, group ? 0 : gid));
            }
        }

        private static void Execute(AuthenticatedChoreMessage<WeaponProductionPacket> message)
        {
            var packet = message.Packet;
            if (!StringComparer.Ordinal.Equals(active.Digest, packet.Catalog)) return;
            var option = active.At(packet.OptionIndex);
            if (!IsOptionAllowed(option) || GamePlayerManagerAPI.Instance.IsAIPlayer(message.SenderPlayerId)) return;
            if (packet.BuildingId != 0)
            {
                if (TryBuilding(packet.BuildingId, packet.BuildingGlobalId, out var building) &&
                    building->r_PlayerIdOwner == message.SenderPlayerId && building->r_BuildingType == option.BuildingType)
                    SetNext(packet.BuildingId, building, option);
                return;
            }

            Span<GameBuilding> all = GameBuildingManagerAPI.Instance.GetBuildingsAsSpan();
            fixed (GameBuilding* first = all)
                for (int spanIndex = 0; spanIndex < all.Length && spanIndex < MaxBuildingId; spanIndex++)
                {
                    GameBuilding* building = first + spanIndex;
                    if (building->r_AliveState == AliveState.IsAlive && building->r_GlobalId != 0 &&
                        building->r_PlayerIdOwner == message.SenderPlayerId && building->r_BuildingType == option.BuildingType)
                        SetNext(spanIndex + 1, building, option);
                }
        }

        internal static bool TrySetAiNextOption(int id, uint gid, WeaponProductionOptionId key)
        {
            if (!SimulationMutationScope.IsTickActive || !TryBuilding(id, gid, out var building) ||
                !GamePlayerManagerAPI.Instance.IsAIPlayer(building->r_PlayerIdOwner)) return false;
            var option = active.At(active.IndexOf(key));
            if (!IsOptionAllowed(option) || building->r_BuildingType != option.BuildingType) return false;
            SetNext(id, building, option);
            return true;
        }

        private static void OnVanillaSwitch(BuildingSwitchProductionGoodEventArgs args)
        {
            if (!HasHudDemand || args.Phase != EventHookPhase.Post) return;
            if (!NativeChoreContext.TryCaptureRaw((byte)ChoreType.SetNextWeaponMade, out var native)) return;
            if (args.BuildingId > 32000)
            {
                // SE Post carries the original arguments, even if a Pre handler
                // rewrote the building or product. Reconcile actual field changes.
                retiredBuildingIds.Clear();
                foreach (var pair in buildings)
                    if (TryBuilding(pair.Key, pair.Value.GlobalId, out var target) && target->r_PlayerIdOwner == native.Sender &&
                        ReconcileNativeWish(pair.Value, target))
                        retiredBuildingIds.Add(pair.Key);
                foreach (int id in retiredBuildingIds) UpdateDemand(id, buildings[id]);
                retiredBuildingIds.Clear();
                return;
            }
            if (TryBuilding(args.BuildingId, unchecked((uint)args.BuildingGlobalId), out var building) &&
                building->r_PlayerIdOwner == native.Sender)
            {
                var state = FindState(args.BuildingId, building->r_GlobalId, building->r_BuildingType);
                if (ReconcileNativeWish(state, building)) UpdateDemand(args.BuildingId, state);
            }
        }

        private static bool ReconcileNativeWish(BuildingState state, GameBuilding* building)
        {
            if (state?.Next == null || state.Type != building->r_BuildingType || !HasNativeSelection(state.Type) ||
                ReadGood(building, nextOffset) == state.Next.TemplateGood) return false;
            state.Next = null;
            return true;
        }

        private static void SetNext(int id, GameBuilding* building, WeaponProductionOption option)
        {
            uint gid = building->r_GlobalId;
            var state = FindState(id, gid, building->r_BuildingType);
            if (state == null && !option.IsVanilla)
                buildings[id] = state = new BuildingState { GlobalId = gid, Type = building->r_BuildingType };
            if (state != null) state.Next = option.IsVanilla ? null : option;
            if (HasNativeSelection(building->r_BuildingType))
                *(short*)((byte*)building + nextOffset) = (short)option.TemplateGood;
            UpdateDemand(id, state);
        }
    }
}
