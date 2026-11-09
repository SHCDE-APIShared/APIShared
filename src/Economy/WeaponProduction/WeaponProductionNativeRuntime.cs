using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    // Each building type publishes its complete adapter as a single permanent cohort.
    // A live patch with a failed publication keeps its gate at zero for process lifetime.
    internal static unsafe partial class WeaponProductionNativeRuntime
    {
        private static readonly object InitLock = new object();
        private static readonly Adapter[] Adapters = new Adapter[WeaponProductionNativeDefinition.Roles.Length];
        private static readonly Adapter[] IngredientAdapters = new Adapter[Adapters.Length];
        private static readonly string[] Diagnostics = new string[Adapters.Length];
        private static IntPtr buildingDemand;
        private static IntPtr module;
        private static ManualLogSource logger;
        private static int currentOffset;
        private static bool attempted;
        private static string failure;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Filter(int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int YieldFilter(int unitId, int amount);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Observation();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ResultObservation(int result);

        internal static bool TryInitialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log, out string reason)
        {
            lock (InitLock)
            {
                if (!attempted)
                {
                    attempted = true;
                    logger = log;
                    try
                    {
                        if (context == null || context.ModuleHandle == IntPtr.Zero || context.Region == null ||
                            !string.Equals(hash, WorkerProductionBonusNativeDefinition.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Weapon production requires the audited native image.");
                        if (!WorkerProductionBonusNativeRuntime.IsAvailable || !WorkerProductionYieldContext.IsAvailable)
                            throw new InvalidOperationException("Weapon production requires the shared yield and zero-output hooks.");
                        if (!BuildingProductionFieldLayout.TryResolve(hash, out currentOffset, out ingredientNextOffset, out string layoutReason))
                            throw new InvalidOperationException(layoutReason);
                        VerifyInterop();
                        module = context.ModuleHandle;
                        buildingDemand = Marshal.AllocHGlobal(WeaponProductionNativeDefinition.MaxBuildingId + 1);
                        ClearBuildingDemand();

                        for (int i = 0; i < Adapters.Length; i++)
                        {
                            var role = WeaponProductionNativeDefinition.Roles[i];
                            try
                            {
                                var candidate = new Adapter(role, role.Sites, ingredient: false);
                                Adapters[i] = candidate;
                                candidate.Install(context);
                            }
                            catch (Exception error)
                            {
                                Diagnostics[i] = error.Message;
                                NativeApiLog.Error(log, $"Weapon production {role.Building} unavailable: {error.Message}");
                            }
                            if (Adapters[i]?.Ready != true) continue;
                            var ingredients = WeaponProductionNativeDefinition.GetIngredientSites(role.Building);
                            if (ingredients.Length == 0) continue;
                            try
                            {
                                var candidate = new Adapter(role, ingredients, ingredient: true);
                                IngredientAdapters[i] = candidate;
                                candidate.Install(context);
                            }
                            catch (Exception error)
                            {
                                NativeApiLog.Error(log, $"Weapon production ingredient amount for {role.Building} unavailable: {error.Message}");
                            }
                        }
                    }
                    catch (Exception error) { failure = error.Message; }
                }

                bool available = Adapters.Any(adapter => adapter?.Ready == true);
                reason = available ? string.Empty : failure ?? string.Join(" | ", Diagnostics.Where(item => item != null));
                return available;
            }
        }

        internal static bool IsAvailable(eStructs building) => FindAdapter(building)?.Ready == true;

        internal static bool CanOverrideIngredients(eStructs building) => FindAdapter(building, IngredientAdapters)?.Ready == true;

        internal static string AvailabilityReason(eStructs building)
        {
            if (failure != null) return failure;
            for (int i = 0; i < Adapters.Length; i++)
                if (WeaponProductionNativeDefinition.Roles[i].Building == building)
                    return Diagnostics[i] ?? (Adapters[i]?.Ready == true ? string.Empty : "Weapon production has not initialized.");
            return "Unsupported weapon workshop type.";
        }

        internal static void SetDemand(bool demand)
        {
            foreach (Adapter adapter in Adapters) adapter?.SetDemand(demand);
            foreach (Adapter adapter in IngredientAdapters)
                if (adapter != null)
                    adapter.SetDemand(demand && WeaponProductionRuntime.GetOptions(adapter.Building)
                        .Any(WeaponProductionRuntime.NeedsIngredientAdapter));
        }

        internal static void SetBuildingDemand(int buildingId, bool demand)
        {
            if (buildingDemand != IntPtr.Zero && buildingId >= 1 && buildingId <= WeaponProductionNativeDefinition.MaxBuildingId)
                Volatile.Write(ref ((byte*)buildingDemand)[buildingId], demand ? (byte)1 : (byte)0);
        }

        internal static void ClearBuildingDemand()
        {
            if (buildingDemand != IntPtr.Zero)
                new Span<byte>((void*)buildingDemand, WeaponProductionNativeDefinition.MaxBuildingId + 1).Clear();
        }

        private static Adapter FindAdapter(eStructs building, Adapter[] adapters = null)
        {
            adapters = adapters ?? Adapters;
            for (int i = 0; i < Adapters.Length; i++)
                if (WeaponProductionNativeDefinition.Roles[i].Building == building) return adapters[i];
            return null;
        }

        private static void VerifyInterop()
        {
            if (sizeof(GameBuilding) != WeaponProductionNativeDefinition.BuildingStride || sizeof(GameUnit) != WorkerProductionBonusNativeDefinition.UnitStride ||
                Offset<GameBuilding>(nameof(GameBuilding.r_ProductionQuotaId)) != 0x24 ||
                Offset<GameBuilding>(nameof(GameBuilding.r_GlobalId)) != 0xD8 ||
                Offset<GameBuilding>(nameof(GameBuilding.r_WoodPlanksAmount)) != 0x128 ||
                Offset<GameBuilding>(nameof(GameBuilding.r_ArmouryMacesAmount)) != 0x174 ||
                Offset<GameBuilding>(nameof(GameBuilding.r_ArmourySwordsAmount)) != 0x178 ||
                Offset<GameBuilding>(nameof(GameBuilding.r_ArmouryMetalArmorAmount)) != 0x180 ||
                Offset<GameUnit>(nameof(GameUnit.r_LinkedProductionBuildingId)) != 0x334 ||
                Offset<GameUnit>(nameof(GameUnit.r_LinkedProductionBuildingGlobalId)) != 0x364 ||
                Offset<GameUnit>(nameof(GameUnit.r_GlobalId)) != 0x94)
                throw new InvalidOperationException("Weapon production interop offsets differ from the audited native layout.");
        }

        private static int Offset<T>(string field) => Marshal.OffsetOf(typeof(T), field).ToInt32();

        private static int CurrentUnit => *(int*)(module + WorkerProductionBonusNativeDefinition.CurrentUnitRva);

        private static bool TryGetWorker(WeaponProductionNativeRole role, int unitId, out GameUnit* worker)
        {
            worker = null;
            return unitId > 0 && unitId <= WeaponProductionNativeDefinition.MaxUnitId &&
                UnitAccess.TryGetById(unitId, out worker, out _) &&
                worker->r_AliveState == AliveState.IsAlive && worker->r_UnitChimp == role.Worker && worker->r_GlobalId != 0;
        }

        private static bool TryGetWorkshop(WeaponProductionNativeRole role, int unitId,
            out GameUnit* worker, out GameBuilding* building)
        {
            building = null;
            if (!TryGetWorker(role, unitId, out worker)) return false;
            int home = worker->r_LinkedProductionBuildingId;
            return home > 0 && home <= WeaponProductionNativeDefinition.MaxBuildingId &&
                GameBuildingManagerAPI.Instance.TryGetBuildingById(home, out building) && building != null &&
                building->r_AliveState == AliveState.IsAlive && building->r_BuildingType == role.Building &&
                building->r_PlayerIdOwner == worker->r_ControllableForPlayerId && building->r_GlobalId != 0 &&
                building->r_GlobalId == worker->r_LinkedProductionBuildingGlobalId;
        }

        private static void Report(Exception error)
        {
            // A runtime exception is a simulation fault. Never let it unwind through the
            // native caller, and never retry an original call with unknown side effects.
            try
            {
                string reason = "Weapon production adapter failed: " + error.Message;
                if (WorkerProductionWorldSafety.IsAvailable) WorkerProductionWorldSafety.Fault(reason);
                else NativeApiLog.Error(logger, reason);
            }
            catch { }
        }

        private sealed partial class Adapter
        {
            private readonly WeaponProductionNativeRole role;
            private readonly WeaponProductionNativeSite[] sites;
            private readonly string claim;
            private readonly NativeHookPublicationState publication = new NativeHookPublicationState();
            private readonly HookHandle<X64InlineHook>[] handles;
            private readonly Delegate[] callbacks;
            private readonly NativeInlineStubGroup group;
            private IntPtr enabled;

            internal Adapter(WeaponProductionNativeRole role, WeaponProductionNativeSite[] sites, bool ingredient)
            {
                this.role = role;
                this.sites = sites;
                claim = "Economy.WeaponProduction." + role.Building + (ingredient ? ".Ingredient" : string.Empty);
                handles = new HookHandle<X64InlineHook>[sites.Length];
                callbacks = new Delegate[handles.Length];
                group = new NativeInlineStubGroup(claim, publication);
            }

            internal bool Ready => publication.IsReady;
            internal eStructs Building => role.Building;

            internal void SetDemand(bool demand)
            {
                if (Ready) Volatile.Write(ref *(int*)enabled, demand ? 1 : 0);
            }

            internal void Install(CrusaderLibraryLoadContext context)
            {
                ulong image = Address(module);
                var worker = WorkerProductionBonusNativeDefinition.Functions.Single(item => item.Rva == role.WorkerRva);
                if (Sha256Hex.Of(context.Memory.Slice(worker.Rva, worker.Length).ToArray()) != worker.Sha256)
                    throw new InvalidOperationException("The audited worker control flow differs.");

                var spans = new NativeSiteSpan[handles.Length];
                for (int i = 0; i < handles.Length; i++)
                {
                    WeaponProductionNativeSite site = sites[i];
                    ValidateOriginal(context, site);
                    if (NativeInlineStub.ProbeDisplacement(image + (uint)site.Rva, site.Bytes.Length) != site.Bytes.Length)
                        throw new InvalidOperationException($"Inline backend span differs at 0x{site.Rva:X}.");
                    spans[i] = new NativeSiteSpan(site.Rva, site.Bytes.Length, NativeSiteMode.Exclusive);
                }

                group.Claim(spans);
                try
                {
                    enabled = Marshal.AllocHGlobal(sizeof(int));
                    Marshal.WriteInt32(enabled, 0);
                    group.Begin(context.Region);
                    for (int i = 0; i < handles.Length; i++)
                    {
                        WeaponProductionNativeSite site = sites[i];
                        Delegate callback = callbacks[i] = Callback(site.Operation);
                        ulong callbackAddress = Address(Marshal.GetFunctionPointerForDelegate(callback));
                        WeaponProductionNativeAssembly.ExpectedStub(site, image, image + NativeInlineStub.PreflightStubOffset,
                            Address(enabled), Address(buildingDemand), callbackAddress);
                        handles[i] = new HookHandle<X64InlineHook>();
                        group.Add(handles[i], image + (uint)site.Rva,
                            (assembler, original, back) => WeaponProductionNativeAssembly.Generate(assembler, original, back,
                                site, image, Address(enabled), Address(buildingDemand), callbackAddress), site.Bytes.Length);
                    }
                    var result = group.Commit("Weapon production preparation was incomplete.");
                    ValidatePrepared(false);
                    group.Publish(result, this, () => ValidatePrepared(true));
                    NativeApiLog.Info(logger, $"{claim} ready: {handles.Length} permanent reference RVA sites.");
                }
                catch
                {
                    // The claim above succeeded, so the group releases it before the state is freed.
                    group.RollbackUnpublished(() =>
                    {
                        if (enabled != IntPtr.Zero) Marshal.FreeHGlobal(enabled);
                        enabled = IntPtr.Zero;
                    });
                    throw;
                }
            }

            private static void ValidateOriginal(CrusaderLibraryLoadContext context, WeaponProductionNativeSite site)
            {
                int next = site.Rva + site.Bytes.Length;
                if (!context.Memory.Slice(site.Rva, site.Bytes.Length).SequenceEqual(site.Bytes) ||
                    !context.Memory.Slice(next, site.Next.Length).SequenceEqual(site.Next))
                    throw new InvalidOperationException($"The audited weapon production instructions differ at 0x{site.Rva:X}.");
                if (!new ReadOnlySpan<byte>((void*)(module + site.Rva), site.Bytes.Length).SequenceEqual(site.Bytes))
                    throw new InvalidOperationException($"A foreign hook occupies weapon production at 0x{site.Rva:X}.");
                var boundary = new ReadOnlySpan<byte>((void*)(module + next), Math.Max(2, site.Next.Length));
                if (!NativeInlineStub.AcceptsLiveBoundary(boundary, site.Next))
                    throw new InvalidOperationException($"The weapon production continuation at 0x{next:X} differs.");
            }

            private void ValidatePrepared(bool installed)
            {
                for (int i = 0; i < handles.Length; i++)
                {
                    WeaponProductionNativeSite site = sites[i];
                    Delegate callback = callbacks[i];
                    NativeInlineStub.Verify(handles[i], Address(module + site.Rva), site.Bytes.Length, installed,
                        stub => WeaponProductionNativeAssembly.ExpectedStub(site, Address(module), stub,
                            Address(enabled), Address(buildingDemand), Address(Marshal.GetFunctionPointerForDelegate(callback))),
                        check =>
                        {
                            switch (check)
                            {
                                case NativeInlineStub.Check.Backend: return $"Weapon production backend differs at 0x{site.Rva:X}.";
                                case NativeInlineStub.Check.Stub: return $"Weapon production generated stub differs at 0x{site.Rva:X}.";
                                case NativeInlineStub.Check.Jump: return $"Weapon production installed jump differs at 0x{site.Rva:X}.";
                                default: return $"Weapon production installed padding differs at 0x{site.Rva:X}.";
                            }
                        });
                }
            }

            private Delegate Callback(WeaponProductionNativeOperation operation)
            {
                if (TryCreateWorkshopIngredientCallback(operation, out Delegate workshop)) return workshop;
                switch (operation)
                {
                    case WeaponProductionNativeOperation.Latch:
                    case WeaponProductionNativeOperation.LatchAfterCall: return (Observation)ObserveLatch;
                    case WeaponProductionNativeOperation.Manufacturing: return (Filter)Manufacture;
                    case WeaponProductionNativeOperation.StageLocalProduct: return (ResultObservation)ObserveStaging;
                    case WeaponProductionNativeOperation.RecoverLocalProduct: return (ResultObservation)ObserveRecovery;
                    case WeaponProductionNativeOperation.Yield: return (YieldFilter)Yield;
                    case WeaponProductionNativeOperation.Deposit: return (Filter)Deposit;
                    case WeaponProductionNativeOperation.Complete: return (Observation)ObserveComplete;
                    case WeaponProductionNativeOperation.IngredientThreshold: return (Filter)FilterIngredientThreshold;
                    case WeaponProductionNativeOperation.IngredientFindWood: return (Filter)UseBufferedWood;
                    case WeaponProductionNativeOperation.IngredientConsumeWood: return (Filter)ConsumeWood;
                    default: throw new ArgumentOutOfRangeException(nameof(operation));
                }
            }

            private void ObserveLatch()
            {
                try
                {
                    int unitId = CurrentUnit;
                    if (TryGetWorkshop(role, unitId, out GameUnit* worker, out GameBuilding* building) &&
                        WeaponProductionRuntime.TryGetOrLatch(worker->r_LinkedProductionBuildingId, building->r_GlobalId,
                            unitId, worker->r_GlobalId, out WeaponProductionOption option) &&
                        role.Sites[0].Operation == WeaponProductionNativeOperation.Latch &&
                        (eGoods)(*(short*)((byte*)building + currentOffset)) != option.TemplateGood)
                        throw new InvalidOperationException("The native workshop latched a different template from its selected option.");
                }
                catch (Exception error) { Report(error); }
            }

            private int Manufacture(int result)
            {
                try
                {
                    int unitId = CurrentUnit;
                    if (TryGetWorkshop(role, unitId, out GameUnit* worker, out GameBuilding* building) &&
                        WeaponProductionRuntime.TryGetBatch(worker->r_LinkedProductionBuildingId, building->r_GlobalId, unitId, worker->r_GlobalId,
                            out WeaponProductionOption option) && option.WorkTicks.HasValue)
                    {
                        int buildingId = worker->r_LinkedProductionBuildingId;
                        // Native B6240 reads the same word before exposing a completion result.
                        bool allowed = building->r_ProductionQuotaId != 0;
                        return WeaponProductionRuntime.CheckManufacturing(buildingId, building->r_GlobalId, unitId,
                            worker->r_GlobalId, allowed, result != 0) ? 1 : 0;
                    }
                }
                catch (Exception error) { Report(error); }
                return result;
            }

            private void ObserveStaging(int result)
            {
                if (result == 0) return;
                try
                {
                    int unitId = CurrentUnit;
                    if (TryGetWorkshop(role, unitId, out GameUnit* worker, out GameBuilding* building))
                        WeaponProductionRuntime.MarkLocalProduct(worker->r_LinkedProductionBuildingId, building->r_GlobalId,
                            unitId, worker->r_GlobalId);
                }
                catch (Exception error) { Report(error); }
            }

            private void ObserveRecovery(int result)
            {
                if (result == 0) return;
                try
                {
                    int unitId = CurrentUnit;
                    if (TryGetWorkshop(role, unitId, out GameUnit* worker, out GameBuilding* building))
                        WeaponProductionRuntime.TryAdoptFinishedBatch(worker->r_LinkedProductionBuildingId, building->r_GlobalId,
                            unitId, worker->r_GlobalId, out _);
                }
                catch (Exception error) { Report(error); }
            }

            private int Yield(int unitId, int amount)
            {
                try
                {
                    if (unitId == CurrentUnit && TryGetWorkshop(role, unitId, out GameUnit* worker, out GameBuilding* building) &&
                        WeaponProductionRuntime.TryGetBatch(worker->r_LinkedProductionBuildingId, building->r_GlobalId,
                            unitId, worker->r_GlobalId, out WeaponProductionOption option))
                    {
                        // This native call's result is unconditionally issued as carried goods.
                        // The shared zero-output footer also closes the resulting empty charge.
                        WeaponProductionRuntime.MarkCargo(worker->r_LinkedProductionBuildingId, building->r_GlobalId,
                            unitId, worker->r_GlobalId);
                        if (option.BaseAmount.HasValue) amount = option.BaseAmount.Value;
                    }
                }
                catch (Exception error) { Report(error); }
                return amount;
            }

            private int Deposit(int good)
            {
                try
                {
                    int unitId = CurrentUnit;
                    if (TryGetWorker(role, unitId, out GameUnit* worker) &&
                        WeaponProductionRuntime.TryGetBatch(worker->r_LinkedProductionBuildingId, worker->r_LinkedProductionBuildingGlobalId,
                            unitId, worker->r_GlobalId, out WeaponProductionOption option))
                        good = (int)option.OutputGood;
                }
                catch (Exception error) { Report(error); }
                return good;
            }

            private void ObserveComplete()
            {
                try
                {
                    int unitId = CurrentUnit;
                    if (TryGetWorker(role, unitId, out GameUnit* worker))
                        WeaponProductionRuntime.CompleteBatch(worker->r_LinkedProductionBuildingId, worker->r_LinkedProductionBuildingGlobalId,
                            unitId, worker->r_GlobalId);
                }
                catch (Exception error) { Report(error); }
            }

            private static ulong Address(IntPtr pointer) => unchecked((ulong)pointer.ToInt64());
        }
    }
}
