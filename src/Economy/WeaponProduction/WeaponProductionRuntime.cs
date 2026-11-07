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
    // Process-owned state. The admission owner holds the engine monitor for world
    // changes. Chores and the native producer adapters mutate only in simulation.
    internal static unsafe partial class WeaponProductionRuntime
    {
        private static readonly object RegistrationGate = new object();
        private static WeaponProductionRegistration[] registrations = Array.Empty<WeaponProductionRegistration>();
        private static WeaponProductionRegistration[] preparedRegistrations;
        private static bool preparedBackend;
        private static bool preparedTannerIngredients;
        private static WeaponProductionCatalog prepared = WeaponProductionCatalog.Empty;
        private static WeaponProductionCatalog frozen = WeaponProductionCatalog.Empty;
        private static WeaponProductionCatalog active = WeaponProductionCatalog.Empty;
        private static readonly Dictionary<int, BuildingState> buildings = new Dictionary<int, BuildingState>();
        private static readonly Dictionary<int, Batch> workers = new Dictionary<int, Batch>();
        private static readonly List<int> retiredWorkerIds = new List<int>();
        private static readonly List<int> retiredBuildingIds = new List<int>();
        private static AuthenticatedChoreChannel<WeaponProductionPacket> channel;
        private static object engine;
        private static int currentOffset, nextOffset;
        private static int* availability;
        private static bool backendReady;
        private static bool cleanupReady;
        private static IDisposable vanillaSwitch;
        internal static bool HasHudDemand => Volatile.Read(ref active).HasOptions;
        internal static bool HasTannerIngredientOverrides => Volatile.Read(ref active).HasTannerIngredientOverrides;

        internal static string AvailabilityReason(WeaponProductionOption option)
        {
            string native = WeaponProductionNativeRuntime.AvailabilityReason(option.BuildingType);
            if (!string.IsNullOrEmpty(native)) return native;
            if (!backendReady) return "Workshop HUD, transfer or save preparation is unavailable.";
            if (!cleanupReady) return "Workshop simulation lifecycle is unavailable.";
            if (NeedsIngredientAdapter(option) && !WeaponProductionNativeRuntime.CanOverrideIngredients(option.BuildingType))
                return "The workshop's ingredient adapter is unavailable.";
            if (NeedsTannerIngredientAdapter(option) && !WeaponProductionNativeRuntime.CanOverrideTannerIngredients)
                return "The tanner's native cow yield is modified by another plugin. Custom costs above one hide require the original cow yield.";
            return "Shared worker-production protection is unavailable.";
        }

        private static bool NeedsTannerIngredientAdapter(WeaponProductionOption option) =>
            option.BuildingType == eStructs.STRUCT_TANNERS_WORKSHOP && option.IngredientAmount > 1;

        internal static bool NeedsIngredientAdapter(WeaponProductionOption option) =>
            option.IngredientAmount > 1 || option.IngredientAmount.HasValue &&
            (option.BuildingType == eStructs.STRUCT_FLETCHERS_WORKSHOP ||
                option.BuildingType == eStructs.STRUCT_POLETURNERS_WORKSHOP);

        private sealed class BuildingState
        {
            internal uint GlobalId;
            internal eStructs Type;
            internal WeaponProductionOption Next;
            internal Batch Current;
        }

        private sealed class Batch
        {
            internal int BuildingId, UnitId;
            internal uint BuildingGlobalId, UnitGlobalId;
            internal WeaponProductionOption Option;
            internal int Elapsed, LastTick;
            internal bool HasTick;
            internal BatchPhase Phase;
        }

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            engine = EngineMonitor.Current;
            channel = ApiShared.ForMod(APISharedPlugin.PluginGuid).Network.RegisterAuthenticatedChore<WeaponProductionPacket>(
                "weapon-workshop/selection", Execute, new AuthenticatedChoreChannelOptions<WeaponProductionPacket>
                { IsEnabled = () => HasHudDemand, MaximumPayloadBytes = 128, ValidatePayload = packet => packet.IsValid });

            if (engine == null) throw new InvalidOperationException("Weapon workshops need the engine monitor.");
            InitializeSave();
            if (!BuildingProductionFieldLayout.TryResolve(hash, out currentOffset, out nextOffset, out string reason))
                throw new InvalidOperationException(reason);
            // The current-image D66B0 permission readers bind this six-DWORD table.
            // Its values are live map rules and may be set by a scenario.
            const int ruleTableRva = 0x3666030;
            if (context.ModuleHandle == IntPtr.Zero || context.Memory.Length < ruleTableRva + 6 * sizeof(int))
                throw new InvalidOperationException("The audited weapon availability table is outside the loaded image.");
            availability = (int*)(context.ModuleHandle + ruleTableRva);
            if (!WeaponProductionNativeRuntime.TryInitialize(context, hash, log, out reason))
                throw new InvalidOperationException(reason);
            WeaponProductionTransferNativeRuntime.Initialize(context, hash, log);
            if (!WeaponProductionTransferNativeRuntime.IsAvailable)
                throw new InvalidOperationException("Weapon-workshop transfer and checksum protection is unavailable.");
            if (!WeaponProductionHud.TryInitialize(out reason)) throw new InvalidOperationException(reason);
            vanillaSwitch = BuildingR3EventHooks.OnSwitchProductionGood.Observable.Subscribe(OnVanillaSwitch);
            backendReady = true;
            InitializeCleanup();
        }

        internal static bool TryRegister(string owner, string id, WeaponProductionOptionDefinition definition,
            out WeaponProductionRegistration registration, out string reason)
        {
            registration = null;
            reason = "";
            if (!UnityMainThreadDispatch.IsMainThread)
            { reason = "Workshop registration requires the initialized Unity thread."; return false; }
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || owner.Length > 128 || id.Length > 128 ||
                StringComparer.Ordinal.Equals(owner, APISharedPlugin.PluginGuid))
            { reason = "A valid consumer GUID and option ID are required."; return false; }
            if (definition == null || string.IsNullOrWhiteSpace(definition.DisplayName) ||
                WeaponProductionCatalog.FindVanilla(definition.BuildingType, definition.TemplateGood) == null ||
                !WeaponProductionCatalog.IsWeapon(definition.OutputGood) ||
                definition.BaseAmount < 0 || definition.BaseAmount > 100 || definition.WorkTicks < 1 || definition.WorkTicks > ushort.MaxValue)
            { reason = "The workshop, template, weapon, base amount or work duration is invalid."; return false; }
            int maxIngredientAmount = WeaponProductionCatalog.IngredientLimit(definition.BuildingType);
            if (definition.IngredientAmount < 1 || definition.IngredientAmount > maxIngredientAmount)
            { reason = $"The ingredient amount must be between 1 and {maxIngredientAmount} for this workshop."; return false; }

            var key = new WeaponProductionOptionId(owner, id);
            lock (RegistrationGate)
            {
                foreach (var old in registrations)
                    if (old.Option.Id.Equals(key))
                    { reason = "This owner already registered that workshop option ID."; return false; }
                registration = new WeaponProductionRegistration(new WeaponProductionOption(key, definition, false));
                var next = new WeaponProductionRegistration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length);
                next[next.Length - 1] = registration;
                registrations = next;
            }
            InitializeCleanup();
            WorkerProductionAgreement.RequestRefresh();
            return true;
        }

        internal static WeaponProductionCatalog PrepareCatalog()
        {
            lock (RegistrationGate)
            {
                bool ready = backendReady && cleanupReady && WorkerProductionRuntime.BaseAvailable;
                bool tannerIngredients = ready && Array.Exists(registrations, registration => NeedsTannerIngredientAdapter(registration.Option)) &&
                    WeaponProductionNativeRuntime.CanOverrideTannerIngredients;
                if (!ReferenceEquals(preparedRegistrations, registrations) || preparedBackend != ready ||
                    preparedTannerIngredients != tannerIngredients)
                {
                    prepared = WeaponProductionCatalog.Compile(registrations,
                        type => ready && WeaponProductionNativeRuntime.IsAvailable(type),
                        option => !NeedsIngredientAdapter(option) ||
                            WeaponProductionNativeRuntime.CanOverrideIngredients(option.BuildingType) &&
                            (tannerIngredients || !NeedsTannerIngredientAdapter(option)));
                    preparedRegistrations = registrations;
                    preparedBackend = ready;
                    preparedTannerIngredients = tannerIngredients;
                }
                return prepared;
            }
        }

        private static void InitializeCleanup()
        {
            if (!backendReady || cleanupReady || registrations.Length == 0) return;
            cleanupReady = SimulationTickEvents.TryRegister(APISharedPlugin.PluginGuid, "weapon-workshop/retire-state",
                RetireLostActors, out _, int.MinValue);
        }

        private static void RetireLostActors(int tick)
        {
            // The Extender has kill events for individual damage sources but no
            // universal worker retirement notification. Only visit custom rows.
            if (!HasHudDemand || (tick & 7) != 0 || (buildings.Count == 0 && workers.Count == 0)) return;
            foreach (var pair in workers)
                if (!IsBatchActorCurrent(pair.Value)) retiredWorkerIds.Add(pair.Key);
            foreach (int id in retiredWorkerIds)
            {
                workers.Remove(id);
                WorkerWire(id).Clear();
                ((ushort*)WorkerWireKindsAddress)[id] = 0;
            }
            retiredWorkerIds.Clear();

            foreach (var pair in buildings)
            {
                var state = pair.Value;
                if (!TryBuilding(pair.Key, state.GlobalId, out _) ||
                    state.Next == null && !HasCurrentBatch(state))
                    retiredBuildingIds.Add(pair.Key);
                else if (!HasCurrentBatch(state) && state.Current != null)
                {
                    state.Current = null;
                    PublishBuilding(pair.Key, state);
                }
            }
            foreach (int id in retiredBuildingIds) UpdateDemand(id, null);
            retiredBuildingIds.Clear();
        }

        private static bool HasCurrentBatch(BuildingState state) => state?.Current != null &&
            (state.Current.Phase == BatchPhase.LocalProduct || IsBatchActorCurrent(state.Current));

        private static bool IsBatchActorCurrent(Batch batch) =>
            UnitAccess.TryGetById(batch.UnitId, out GameUnit* unit, out _) && unit->r_GlobalId == batch.UnitGlobalId &&
            MatchesWorkerRole(batch.Option.BuildingType, unit->r_UnitChimp) &&
            (UnitAccess.IsReallyAlive(unit) || unit->r_AliveState == AliveState.Paused && unit->r_IsKilledByProjectile == 0);

        internal static void BeginWorld(WeaponProductionCatalog catalog)
        {
            EndWorld();
            frozen = catalog;
            if (catalog.HasOptions) Marshal.WriteByte(TransferDemandAddress, 1);
        }

        internal static bool Activate(bool allowed, out string reason)
        {
            reason = null;
            if (!allowed && (buildings.Count != 0 || workers.Count != 0))
            { reason = "This mission mode does not allow the saved weapon-workshop recipes."; return false; }
            var catalog = allowed ? frozen : WeaponProductionCatalog.Empty;
            Volatile.Write(ref active, catalog);
            WeaponProductionNativeRuntime.SetDemand(catalog.HasOptions);
            return true;
        }

        internal static void EndWorld()
        {
            Volatile.Write(ref active, WeaponProductionCatalog.Empty);
            WeaponProductionNativeRuntime.SetDemand(false);
            WeaponProductionNativeRuntime.ClearBuildingDemand();
            frozen = WeaponProductionCatalog.Empty;
            ClearWire();
            ResetSaveLoad();
            buildings.Clear();
            workers.Clear();
            retiredBuildingIds.Clear();
            retiredWorkerIds.Clear();
        }

        internal static IReadOnlyList<WeaponProductionOption> GetOptions(eStructs type) => Volatile.Read(ref active).For(type);

        internal static bool IsOptionAllowed(WeaponProductionOption option)
        {
            if (option == null || availability == null) return false;
            switch (option.OutputGood)
            {
                case eGoods.STORED_CROSSBOWS: return availability[0] != 0;
                case eGoods.STORED_PIKES: return availability[1] != 0;
                case eGoods.STORED_SWORDS: return availability[2] != 0;
                case eGoods.STORED_BOWS: return availability[3] != 0;
                case eGoods.STORED_SPEARS: return availability[4] != 0;
                case eGoods.STORED_MACES: return availability[5] != 0;
                case eGoods.STORED_LEATHER_ARMOUR:
                case eGoods.STORED_METAL_ARMOUR: return true;
                default: return false;
            }
        }

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

        internal static bool TryGetOrLatch(int id, uint gid, int unitId, uint unitGid, out WeaponProductionOption option)
        {
            option = null;
            if (!HasHudDemand || unitId <= 0 || unitId > MaxUnitId || unitGid == 0 || !TryBuilding(id, gid, out var building)) return false;
            var state = FindState(id, gid, building->r_BuildingType);
            if (state == null) return false;
            var existing = FindBatch(id, gid, unitId, unitGid);
            if (state.Current?.Phase == BatchPhase.LocalProduct && HasBufferedProduct(building, state.Current.Option))
            {
                if (!TryAdoptFinishedBatch(id, gid, unitId, unitGid, out option)) return false;
                ResetManufacturing(id, gid, unitId, unitGid);
                return true;
            }
            if (existing?.Phase == BatchPhase.Manufacturing) { option = existing.Option; return true; }
            option = state.Next;
            if (!IsOptionAllowed(option)) { option = null; return false; }
            if (HasNativeSelection(building->r_BuildingType) && ReadGood(building, currentOffset) != option.TemplateGood)
            {
                // A raw SE field setter can bypass the Chore publisher. Its
                // different native template takes precedence at the next latch.
                state.Next = null;
                UpdateDemand(id, state);
                option = null;
                return false;
            }
            var batch = new Batch { BuildingId = id, BuildingGlobalId = gid, UnitId = unitId, UnitGlobalId = unitGid, Option = option };
            workers[unitId] = batch;
            state.Current = batch;
            PublishWorker(batch);
            PublishBuilding(id, state);
            return true;
        }

        internal static bool TryAdoptFinishedBatch(int id, uint gid, int unitId, uint unitGid, out WeaponProductionOption option)
        {
            option = null;
            if (unitId <= 0 || unitId > MaxUnitId || unitGid == 0 || !TryBuilding(id, gid, out var building)) return false;
            var state = FindState(id, gid, building->r_BuildingType);
            if (state?.Current?.Phase != BatchPhase.LocalProduct) return false;
            var previous = state.Current;
            option = previous.Option;
            if (FindBatch(id, gid, unitId, unitGid) == previous) return true;
            if (workers.TryGetValue(previous.UnitId, out var prior) && ReferenceEquals(prior, previous))
            {
                workers.Remove(previous.UnitId);
                WorkerWire(previous.UnitId).Clear();
                ((ushort*)WorkerWireKindsAddress)[previous.UnitId] = 0;
            }
            var batch = new Batch
            {
                BuildingId = id, BuildingGlobalId = gid, UnitId = unitId, UnitGlobalId = unitGid,
                Option = option, Phase = BatchPhase.LocalProduct
            };
            state.Current = workers[unitId] = batch;
            PublishWorker(batch);
            PublishBuilding(id, state);
            return true;
        }

        internal static void MarkLocalProduct(int id, uint gid, int unitId, uint unitGid) =>
            SetPhase(id, gid, unitId, unitGid, BatchPhase.LocalProduct);

        internal static void MarkCargo(int id, uint gid, int unitId, uint unitGid)
        {
            var batch = FindBatch(id, gid, unitId, unitGid);
            if (batch == null) return;
            var state = FindState(id, gid, batch.Option.BuildingType);
            if (batch.Phase == BatchPhase.LocalProduct && state?.Current == batch &&
                TryBuilding(id, gid, out var building) && HasBufferedProduct(building, batch.Option))
            {
                // The native building can retain several finished pieces while
                // the worker delivers one. The local lot and the trip can outlive
                // one another, so keep separate provenance until both drain.
                state.Current = new Batch
                {
                    BuildingId = id, BuildingGlobalId = gid, UnitId = unitId, UnitGlobalId = unitGid,
                    Option = batch.Option, Phase = BatchPhase.LocalProduct
                };
                PublishBuilding(id, state);
            }
            SetPhase(id, gid, unitId, unitGid, BatchPhase.Cargo);
        }

        private static bool HasBufferedProduct(GameBuilding* building, WeaponProductionOption option)
        {
            if (option.BuildingType == eStructs.STRUCT_ARMOURERS_WORKSHOP) return building->r_ArmouryMetalArmorAmount != 0;
            if (option.BuildingType != eStructs.STRUCT_BLACKSMITHS_WORKSHOP) return false;
            return option.TemplateGood == eGoods.STORED_SWORDS
                ? building->r_ArmourySwordsAmount != 0
                : building->r_ArmouryMacesAmount != 0;
        }

        private static void SetPhase(int id, uint gid, int unitId, uint unitGid, BatchPhase phase)
        {
            var batch = FindBatch(id, gid, unitId, unitGid);
            if (batch == null || batch.Phase == phase) return;
            batch.Phase = phase;
            PublishWorker(batch);
            var state = FindState(id, gid, batch.Option.BuildingType);
            if (state != null && ReferenceEquals(state.Current, batch)) PublishBuilding(id, state);
        }

        internal static bool TryGetBatch(int id, uint gid, int unitId, uint unitGid, out WeaponProductionOption option)
        {
            var batch = FindBatch(id, gid, unitId, unitGid);
            option = batch?.Option;
            return option != null;
        }

        private static WeaponProductionOption GetPendingIngredientOption(BuildingState state, GameBuilding* building)
        {
            var option = state?.Next;
            return IsOptionAllowed(option) &&
                (!HasNativeSelection(state.Type) || ReadGood(building, nextOffset) == option.TemplateGood) ? option : null;
        }

        internal static int GetIngredientAmount(int id, uint gid, int vanillaAmount)
        {
            if (!TryBuilding(id, gid, out var building)) return vanillaAmount;
            var state = FindState(id, gid, building->r_BuildingType);
            return GetPendingIngredientOption(state, building)?.IngredientAmount ?? vanillaAmount;
        }

        internal static bool TryGetIngredientOption(int id, uint gid, int unitId, uint unitGid, out WeaponProductionOption option)
        {
            option = null;
            if (!TryBuilding(id, gid, out var building)) return false;
            var state = FindState(id, gid, building->r_BuildingType);
            var current = state?.Current;
            if (current?.Phase == BatchPhase.LocalProduct && HasBufferedProduct(building, current.Option))
                option = current.Option;
            else
            {
                var batch = FindBatch(id, gid, unitId, unitGid);
                if (batch?.Phase == BatchPhase.Manufacturing)
                    option = batch.Option;
                // The blacksmith has already latched by the time it debits
                // iron. The fixed-good workshops latch after the debit.
                else if (state != null && !HasNativeSelection(state.Type))
                    option = GetPendingIngredientOption(state, building);
            }
            return option != null;
        }

        internal static void AbortUnchargedBatch(int id, uint gid, int unitId, uint unitGid)
        {
            if (FindBatch(id, gid, unitId, unitGid)?.Phase == BatchPhase.Manufacturing)
                CompleteBatch(id, gid, unitId, unitGid);
        }

        internal static void CompleteBatch(int id, uint gid, int unitId, uint unitGid)
        {
            var batch = FindBatch(id, gid, unitId, unitGid);
            if (batch == null) return;
            workers.Remove(unitId);
            WorkerWire(unitId).Clear();
            ((ushort*)WorkerWireKindsAddress)[unitId] = 0;
            var state = FindState(id, gid, batch.Option.BuildingType);
            if (state != null && ReferenceEquals(state.Current, batch))
            { state.Current = null; UpdateDemand(id, state); }
        }

        internal static bool CheckManufacturing(int id, uint gid, int unitId, uint unitGid, bool enabled, bool originalDone)
        {
            var batch = FindBatch(id, gid, unitId, unitGid);
            int? duration = batch?.Option.WorkTicks;
            if (!duration.HasValue) return originalDone;
            if (!enabled) { ResetManufacturing(id, gid, unitId, unitGid); return false; }
            if (engine == null || !Monitor.IsEntered(engine) || !SimulationTickSource.TryReadAdmittedNativeTick(out int tick))
            {
                WorkerProductionWorldSafety.Fault("Weapon-workshop manufacturing cannot read its native map tick.");
                return false;
            }
            if ((!batch.HasTick || batch.LastTick != tick) && batch.Elapsed < duration.Value)
            { batch.LastTick = tick; batch.HasTick = true; batch.Elapsed++; PublishWorker(batch); }
            return batch.Elapsed >= duration.Value;
        }

        internal static void ResetManufacturing(int id, uint gid, int unitId, uint unitGid)
        {
            var batch = FindBatch(id, gid, unitId, unitGid);
            if (batch != null && batch.HasTick)
            { batch.Elapsed = 0; batch.LastTick = 0; batch.HasTick = false; PublishWorker(batch); }
        }

        private static BuildingState FindState(int id, uint gid, eStructs type) =>
            buildings.TryGetValue(id, out var state) && state.GlobalId == gid && state.Type == type ? state : null;

        private static Batch FindBatch(int id, uint gid, int unitId, uint unitGid) =>
            workers.TryGetValue(unitId, out var batch) && batch.UnitGlobalId == unitGid &&
            batch.BuildingId == id && batch.BuildingGlobalId == gid ? batch : null;

        private static void UpdateDemand(int id, BuildingState state)
        {
            bool needed = state != null && (state.Next != null || state.Current != null);
            if (!needed) buildings.Remove(id);
            PublishBuilding(id, needed ? state : null);
            WeaponProductionNativeRuntime.SetBuildingDemand(id, needed);
        }

        private static bool TryBuilding(int id, uint gid, out GameBuilding* building)
        {
            building = null;
            var manager = GameBuildingManagerAPI.Instance;
            return id > 0 && id <= MaxBuildingId && gid != 0 && manager != null && manager.IsValidId(id) &&
                manager.TryGetBuildingById(id, out building) && building != null &&
                building->r_GlobalId == gid && building->r_AliveState == AliveState.IsAlive;
        }

        private static eGoods ReadGood(GameBuilding* building, int offset) => (eGoods)(*(short*)((byte*)building + offset));
        private static WeaponProductionOption ReadVanilla(GameBuilding* building, int offset)
        {
            var type = building->r_BuildingType;
            if (type == eStructs.STRUCT_TANNERS_WORKSHOP) return WeaponProductionCatalog.FindVanilla(type, eGoods.STORED_LEATHER_ARMOUR);
            if (type == eStructs.STRUCT_ARMOURERS_WORKSHOP) return WeaponProductionCatalog.FindVanilla(type, eGoods.STORED_METAL_ARMOUR);
            return WeaponProductionCatalog.FindVanilla(type, ReadGood(building, offset));
        }

        private static bool HasNativeSelection(eStructs type) => type == eStructs.STRUCT_FLETCHERS_WORKSHOP ||
            type == eStructs.STRUCT_POLETURNERS_WORKSHOP || type == eStructs.STRUCT_BLACKSMITHS_WORKSHOP;
    }
}
