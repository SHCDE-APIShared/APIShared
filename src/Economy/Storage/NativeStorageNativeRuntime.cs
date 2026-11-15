using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Logging;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;
using SHCDESE.GameGlobals;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    // Owns one permanent mixed cohort. The load context/region remain Script-Extender-owned.
    // Preparation installs nothing; callbacks keep their exact originals while publication
    // is incomplete, and the native inline demand DWORD stays zero until the cohort is ready.
    internal static unsafe class NativeStorageNativeRuntime
    {
        internal const string SiteName = "Economy.NativeStorage";
        // NativeX64 Indirect needs six bytes, unlike the definition's inline-sized windows.
        private const int AdmissionDisplaced = 6, ArmoryFreeDisplaced = 10, GranaryFreeDisplaced = 10;
        private static readonly object Startup = new object();
        private static CrusaderLibraryLoadContext context;
        private static ManualLogSource logger;
        private static Exception initializationFailure;
        private static Candidate candidate;
        [ThreadStatic] private static int addDepth;

        internal static bool IsInstalled => Volatile.Read(ref candidate)?.Ready == true;
        internal static object EngineGate => GoodsWalletNative.Instance.EngineMonitor;
        internal static int B6100Depth => addDepth;
        // Native startup can publish a world before its mode/context is known. Do not apply
        // capacity policy to Vanilla's initialization writes in that intermediate state.
        internal static bool CanActivateWorld(bool knownMode, object clockWorld, object currentWorld) =>
            knownMode && currentWorld != null && ReferenceEquals(clockWorld, currentWorld);
        internal static void Demand(bool active) => Volatile.Read(ref candidate)?.SetDemand(active);
        internal static void UpdateDemand(bool active) => Demand(active);

        internal static void Initialize(CrusaderLibraryLoadContext loaded, string hash, ManualLogSource log)
        {
            lock (Startup)
            {
                if (context != null)
                {
                    if (loaded == null || loaded.ModuleHandle != context.ModuleHandle ||
                        !string.Equals(hash, NativeStorageNativeDefinition.NativeHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Native storage cannot change its process image.");
                    return;
                }
                if (initializationFailure != null)
                    throw new InvalidOperationException("Native storage initialization previously failed.", initializationFailure);
                try
                {
                    if (loaded == null || loaded.ModuleHandle == IntPtr.Zero || loaded.Region == null ||
                        !string.Equals(hash, NativeStorageNativeDefinition.NativeHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Native storage requires the audited LibraryLoaded image.");
                    ValidateLayout();
                    ValidatePristine(loaded.Memory);
                    logger = log;
                    context = loaded; // Retain the SE context; never dispose its region.
                }
                catch (Exception error) { initializationFailure = error; throw; }
            }
        }

        internal static bool EnsureInstalled(out string reason)
        {
            lock (Startup)
            {
                if (candidate != null)
                { reason = candidate.Ready ? string.Empty : candidate.Failure ?? "Native storage publication is unavailable."; return candidate.Ready; }
                if (context == null || initializationFailure != null)
                { reason = initializationFailure?.Message ?? "Native storage has not received LibraryLoaded initialization."; return false; }
                // Inline writes do not suspend threads. A retained context is not permission
                // to patch a gameplay block after the first mission has begun.
                if (!NativeInlinePatchWindow.IsOpen)
                { reason = NativeInlinePatchWindow.ClosedReason; return false; }
                var prepared = new Candidate(context, logger);
                Volatile.Write(ref candidate, prepared);
                try { prepared.Install(); reason = string.Empty; return true; }
                catch (Exception error)
                {
                    prepared.Fail(error);
                    reason = error.Message;
                    try { NativeApiLog.Error(logger, "Native storage unavailable; published candidates remain dormant: " + error); } catch { }
                    return false;
                }
            }
        }

        internal static bool TryRead(int slot, out NativeStorageProbe probe) => TryReadCore(IntPtr.Zero, slot, out probe, out _, true);
        internal static bool TryRead(IntPtr manager, int slot, out NativeStorageProbe probe) =>
            TryReadCore(manager, slot, out probe, out _);

        private static bool TryReadCore(IntPtr manager, int slot, out NativeStorageProbe probe, out bool unsupported, bool resolveManager = false)
        {
            probe = default; unsupported = false;
            var wallet = GoodsWalletNative.Instance;
            object gate = wallet.EngineMonitor;
            // These are protected reads, not mutation authority. Multiplayer presentation may
            // query capacity outside a tick/Chore while holding the real engine monitor.
            if (context == null || !wallet.IsReady || gate == null || !Monitor.IsEntered(gate) ||
                slot < 1 || slot > NativeStorageNativeDefinition.MaxBuildingId) return false;
            var clock = MissionLifecycleService.NativeClock;
            object world = wallet.World;
            if (clock == null || clock.Context.Mode.Kind == GameModes.GameModeKind.Unknown || world == null ||
                !ReferenceEquals(clock.World, world)) return false;
            ulong address = GameGlobalsManager.Instance.GameBuildingManagerVA;
            const ulong extent = NativeStorageNativeDefinition.BuildingManagerArrayOffset +
                4000UL * NativeStorageNativeDefinition.BuildingStride;
            if (address == 0 || address > (ulong)long.MaxValue - extent || manager == IntPtr.Zero && !resolveManager ||
                manager != IntPtr.Zero && Address(manager) != address) return false;
            var building = (GameBuilding*)(address + NativeStorageNativeDefinition.BuildingManagerArrayOffset +
                (ulong)slot * NativeStorageNativeDefinition.BuildingStride);
            StoragePool kind;
            int capacity;
            if ((int)building->r_BuildingType == NativeStorageNativeDefinition.ArmoryType)
            { kind = StoragePool.Armory; capacity = NativeStorageNativeDefinition.ArmoryCapacity; }
            else if ((int)building->r_BuildingType == NativeStorageNativeDefinition.GranaryType)
            { kind = StoragePool.Granary; capacity = NativeStorageNativeDefinition.GranaryCapacity; }
            else { unsupported = true; return false; }
            if ((int)building->r_AliveState != NativeStorageNativeDefinition.AliveValue ||
                building->r_GlobalId == 0 || building->r_PlayerIdOwner < 1 || building->r_PlayerIdOwner > 8) return false;
            uint* goods = (uint*)((byte*)building + NativeStorageNativeDefinition.LocalGoodsOffset);
            long sum = 0;
            for (int good = kind == StoragePool.Armory ? 0 : 1; good < NativeStorageNativeDefinition.NativeGoodsCount; good++)
            {
                uint quantity = goods[good];
                if (quantity > int.MaxValue) return false; // Signed native arithmetic would wrap.
                sum += quantity;
            }
            if (kind == StoragePool.Granary)
            {
                if (building->r_CurrentGoodStackAmount > int.MaxValue) return false;
                sum = Math.Max(sum, building->r_CurrentGoodStackAmount);
            }
            if (sum > int.MaxValue || !ReferenceEquals(world, wallet.World) ||
                GameGlobalsManager.Instance.GameBuildingManagerVA != address) return false;
            probe = new NativeStorageProbe(true, slot, building->r_GlobalId, building->r_PlayerIdOwner, kind, (int)sum, capacity);
            return true;
        }

        private static void EnterAdd() => addDepth++;
        private static void LeaveAdd() => addDepth--;
        private static ulong Address(IntPtr pointer) => unchecked((ulong)pointer.ToInt64());

        internal static void ValidateLayout()
        {
            if (typeof(GameBuilding).StructLayoutAttribute?.Value != LayoutKind.Sequential ||
                typeof(GameBuilding).StructLayoutAttribute.Pack != 1 || sizeof(GameBuilding) != 0x32C ||
                typeof(GameBuildingManager).StructLayoutAttribute?.Value != LayoutKind.Sequential ||
                typeof(GameBuildingManager).StructLayoutAttribute.Pack != 1 ||
                Marshal.OffsetOf(typeof(GameBuildingManager), nameof(GameBuildingManager.BuildingsArray)).ToInt32() != 0x5C ||
                typeof(GameBuildingManager).GetField(nameof(GameBuildingManager.BuildingsArray))?.FieldType != typeof(GameBuilding))
                throw new InvalidOperationException("The native storage record/manager layout differs.");
            Field(nameof(GameBuilding.r_AliveState), typeof(AliveState), 0xD0);
            Field(nameof(GameBuilding.r_BuildingType), typeof(eStructs), 0xD2);
            if (Enum.GetUnderlyingType(typeof(AliveState)) != typeof(short) || Enum.GetUnderlyingType(typeof(eStructs)) != typeof(ushort))
                throw new InvalidOperationException("Native storage enum widths differ.");
            Field(nameof(GameBuilding.r_PlayerIdOwner), typeof(ushort), 0xD6);
            Field(nameof(GameBuilding.r_GlobalId), typeof(uint), 0xD8);
            Field(nameof(GameBuilding.r_CurrentGoodStackAmount), typeof(uint), 0x184);
            string[] goods = { "r_NullAmount", "r_WoodLogsAmount", "r_WoodPlanksAmount", "r_RawHopsAmount", "r_StoneBlocksAmount",
                "r_CowHidesAmount", "r_IronIngotsAmount", "r_PitchRawAmount", "r_PitchRefinedAmount", "r_RawWheatAmount",
                "r_BreadAmount", "r_CheeseAmount", "r_MeatAmount", "r_FruitAmount", "r_AleAmount", "r_GoldAmount", "r_FlourAmount",
                "r_ArmouryBowsAmount", "r_ArmouryCrossbowsAmount", "r_ArmourySpearsAmount", "r_ArmouryPikesAmount",
                "r_ArmouryMacesAmount", "r_ArmourySwordsAmount", "r_ArmouryLeatherArmorAmount", "r_ArmouryMetalArmorAmount" };
            for (int i = 0; i < goods.Length; i++) Field(goods[i], typeof(uint), 0x120 + i * 4);
            if ((int)eGoods.Count != 25) throw new InvalidOperationException("The native storage goods table differs.");
        }
        private static void Field(string name, Type type, int offset)
        {
            if (typeof(GameBuilding).GetField(name)?.FieldType != type || Marshal.OffsetOf(typeof(GameBuilding), name).ToInt32() != offset)
                throw new InvalidOperationException("Native storage field differs: " + name);
        }
        internal static void ValidatePristine(ReadOnlySpan<byte> memory)
        {
            Snapshot(memory, NativeStorageNativeDefinition.ArmoryAdmissionRva, NativeStorageNativeDefinition.ArmoryAdmissionBytes);
            Snapshot(memory, NativeStorageNativeDefinition.ArmoryFreeRva, NativeStorageNativeDefinition.ArmoryFreeBytes);
            Snapshot(memory, NativeStorageNativeDefinition.GranaryFreeRva, NativeStorageNativeDefinition.GranaryFreeBytes);
            Hash(memory, NativeStorageNativeDefinition.GranaryFinderRva, NativeStorageNativeDefinition.GranaryFinderLength, NativeStorageNativeDefinition.GranaryFinderSha256);
            Hash(memory, NativeStorageNativeDefinition.GranaryRecomputeRva, NativeStorageNativeDefinition.GranaryRecomputeLength, NativeStorageNativeDefinition.GranaryRecomputeSha256);
            Hash(memory, NativeStorageNativeDefinition.AddGoodRva, NativeStorageNativeDefinition.AddGoodLength, NativeStorageNativeDefinition.AddGoodSha256);
            Snapshot(memory, NativeStorageNativeDefinition.GranaryFinderApertureRva, NativeStorageNativeDefinition.GranaryFinderApertureBytes);
            Snapshot(memory, NativeStorageNativeDefinition.GranaryFinderContinuationRva, NativeStorageNativeDefinition.GranaryFinderContinuationBytes);
        }
        private static void Snapshot(ReadOnlySpan<byte> memory, int rva, byte[] bytes)
        {
            if (rva < 0 || rva > memory.Length - bytes.Length || !memory.Slice(rva, bytes.Length).SequenceEqual(bytes))
                throw new InvalidOperationException("Native storage pristine bytes differ at RVA 0x" + rva.ToString("X") + ".");
        }
        private static void Hash(ReadOnlySpan<byte> memory, int rva, int length, string hash)
        {
            if (rva < 0 || rva > memory.Length - length ||
                !string.Equals(Sha256Hex.Of(memory.Slice(rva, length).ToArray()), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native storage function differs at RVA 0x" + rva.ToString("X") + ".");
        }

        // Executes the production mixed-cohort publication order. Native image/ownership and
        // managed SE patching are explicit startup boundaries, so simulated backends can test
        // this owner without loading a game. No boundary delegate runs on a native hot path.
        internal sealed class Installation
        {
            internal readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
            private HookTransaction transaction;
            private object callbackOwner;
            private Exception failure;
            private int ready, unpublishedCleared;
            internal bool Ready => Volatile.Read(ref ready) != 0 && Publication.IsReady;
            internal bool RetainsCandidate => transaction != null && callbackOwner != null;

            internal void Install(HookTransaction pending, Func<bool> prepared, Action<bool> validateNative,
                Action applyManaged, Func<bool> managedApplied, object owner, Action deactivate, Action cleanupUnpublished)
            {
                if (pending == null || prepared == null || validateNative == null || applyManaged == null ||
                    managedApplied == null || owner == null || deactivate == null || cleanupUnpublished == null)
                    throw new ArgumentException("Every native storage installation boundary is required.");
                Publication.ThrowIfUnavailable();
                if (Ready) return;
                if (failure != null) throw new InvalidOperationException("This native storage candidate already failed preparation.", failure);
                transaction = pending;
                callbackOwner = owner;
                try
                {
                    CommitResult result = pending.Commit();
                    if (!result.IsCompleteSuccess || !prepared())
                        throw new InvalidOperationException("The native storage cohort did not prepare completely.");
                    validateNative(false);
                    // Root this owner, and therefore the complete candidate, before the first
                    // write. A throwing native Enable or managed Apply can already be published.
                    NativeHookPublication.Enable(pending, result, Publication, this, () =>
                    {
                        validateNative(true);
                        applyManaged();
                        if (!managedApplied())
                            throw new InvalidOperationException("The SE native-storage adapter did not apply.");
                    });
                    Volatile.Write(ref ready, 1);
                }
                catch (Exception error)
                {
                    failure = error;
                    Volatile.Write(ref ready, 0);
                    try { deactivate(); }
                    finally { RollbackUnpublished(pending, cleanupUnpublished); }
                    throw;
                }
            }

            internal void RollbackUnpublished(HookTransaction pending, Action cleanup)
            {
                if (Publication.HasStarted || Interlocked.CompareExchange(ref unpublishedCleared, 1, 0) != 0) return;
                try { Publication.RollbackUnpublished(pending, null); }
                finally
                {
                    // Keep candidate cleanup even if an unpublished backend's Dispose throws.
                    // Both steps use the permanent-owner gate; a publication attempt forbids
                    // transaction disposal, claim release and callback-root removal alike.
                    Publication.RollbackUnpublished(null, () =>
                    {
                        try { cleanup?.Invoke(); }
                        finally { transaction = null; callbackOwner = null; }
                    });
                }
            }
        }

        private sealed class Candidate
        {
            private readonly CrusaderLibraryLoadContext loaded;
            private readonly ManualLogSource log;
            private readonly Installation installation = new Installation();
            private readonly DetourHandle<NativeStorageNativeDefinition.ArmoryAdmissionCall> admission = new DetourHandle<NativeStorageNativeDefinition.ArmoryAdmissionCall>();
            private readonly DetourHandle<NativeStorageNativeDefinition.ArmoryFreeCall> armoryFree = new DetourHandle<NativeStorageNativeDefinition.ArmoryFreeCall>();
            private readonly DetourHandle<NativeStorageNativeDefinition.GranaryFreeCall> granaryFree = new DetourHandle<NativeStorageNativeDefinition.GranaryFreeCall>();
            private readonly HookHandle<X64InlineHook> finder = new HookHandle<X64InlineHook>();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AggregateFilter(IntPtr manager, int slot, int aggregate);
            private readonly AggregateFilter aggregateFilter;
            private readonly Func<Delegate, IntPtr, int, int, int, int, int, int, long> addAdapter;
            private Func<Delegate, IntPtr, int, int, int, int, int, int, long> forward;
            private HookTransaction transaction;
            private ILHook managed;
            private IntPtr demand;
            private bool claimed;
            private int faults;
            internal string Failure { get; private set; }
            internal bool Ready => installation.Ready;
            private bool Active
            {
                get
                {
                    if (!Ready || demand == IntPtr.Zero || Volatile.Read(ref *(int*)demand) == 0) return false;
                    var clock = MissionLifecycleService.NativeClock;
                    return CanActivateWorld(clock != null && clock.Context.Mode.Kind != GameModes.GameModeKind.Unknown,
                        clock?.World, GoodsWalletNative.Instance.World);
                }
            }
            internal Candidate(CrusaderLibraryLoadContext loaded, ManualLogSource log)
            { this.loaded = loaded; this.log = log; aggregateFilter = FilterGranaryAggregate; addAdapter = AddGood; }
            internal void SetDemand(bool active)
            { if (demand != IntPtr.Zero) Volatile.Write(ref *(int*)demand, Ready && active ? 1 : 0); }

            internal void Install()
            {
                try
                {
                    VerifyLive();
                    var method = NativeStorageSeContract.Resolve();
                    NativeStorageSeContract.Validate(method);
                    NativeStorageSeContract.ValidateNativeOwner(method, loaded.ModuleHandle + NativeStorageNativeDefinition.AddGoodRva);
                    forward = NativeStorageSeContract.CreateForwarder(NativeStorageSeContract.OriginalDelegateType(method));
                    using (var definition = new DynamicMethodDefinition(method))
                    using (var il = new ILContext(definition.Definition))
                    { il.ReferenceBag = RuntimeILReferenceBag.Instance; il.Invoke(c => NativeStorageSeContract.Apply(c, addAdapter, EnterAdd, LeaveAdd)); definition.Generate(); }
                    managed = new ILHook(method, il => NativeStorageSeContract.Apply(il, addAdapter, EnterAdd, LeaveAdd),
                        new ILHookConfig { ManualApply = true, ID = "APIShared.Economy.NativeStorage.SEOriginal" });
                    demand = Marshal.AllocHGlobal(sizeof(int)); Marshal.WriteInt32(demand, 0);
                    ulong image = Address(loaded.ModuleHandle), callback = Address(Marshal.GetFunctionPointerForDelegate(aggregateFilter));
                    NativeStorageAssembly.ExpectedStub(image, image + NativeInlineStub.PreflightStubOffset, Address(demand), callback);
                    if (NativeInlineStub.ProbeDisplacement(image + NativeStorageNativeDefinition.GranaryFinderApertureRva,
                        NativeStorageNativeDefinition.GranaryFinderApertureLength) != NativeStorageNativeDefinition.GranaryFinderApertureLength)
                        throw new InvalidOperationException("The native storage inline displacement differs.");
                    ApiSharedNativeSites.Claim(ApiSharedRuntime.ProcessInstance.NativeOwnership, SiteName, Spans(), s => new InvalidOperationException(s));
                    claimed = true;
                    var backend = new NativeDetourBackend(new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect,
                        FollowCalls = false, FollowJumps = false, Chaining = HookChainingPolicy.Refuse,
                        FunctionScanLimit = NativeStorageNativeDefinition.ArmoryAdmissionLength });
                    transaction = new HookTransaction(loaded.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                        new HookTransactionOptions { InstallOnCommit = false, OwnsHooks = true,
                            FailureMode = TransactionFailureMode.RollbackAndThrow, Backend = NativeStartupPatchWindow.Backend(backend) });
                    transaction.AddDetour(admission, HookTarget.FromAddress(image + NativeStorageNativeDefinition.ArmoryAdmissionRva),
                        (NativeStorageNativeDefinition.ArmoryAdmissionCall)Admission);
                    transaction.AddDetour(armoryFree, HookTarget.FromAddress(image + NativeStorageNativeDefinition.ArmoryFreeRva),
                        (NativeStorageNativeDefinition.ArmoryFreeCall)ArmoryFree);
                    transaction.AddDetour(granaryFree, HookTarget.FromAddress(image + NativeStorageNativeDefinition.GranaryFreeRva),
                        (NativeStorageNativeDefinition.GranaryFreeCall)GranaryFree);
                    transaction.AddInline(finder, HookTarget.FromAddress(image + NativeStorageNativeDefinition.GranaryFinderApertureRva),
                        (a, original, back) => NativeStorageAssembly.Generate(a, original, back, image, Address(demand), callback),
                        hookSize: NativeStorageNativeDefinition.GranaryFinderApertureLength);
                    installation.Install(transaction,
                        () => admission.Success && armoryFree.Success && granaryFree.Success && finder.Success,
                        ValidatePrepared, () => managed.Apply(), () => managed.IsApplied, this,
                        () => SetDemand(false), ClearUnpublished);
                    try { NativeApiLog.Info(log, "Native pooled-storage cohort installed; logical demand remains zero until world activation."); } catch { }
                }
                catch
                {
                    SetDemand(false);
                    installation.RollbackUnpublished(transaction, ClearUnpublished);
                    throw;
                }
            }
            internal void Fail(Exception error) { SetDemand(false); Failure = error.Message; }

            private void ClearUnpublished()
            {
                try
                {
                    if (managed != null) installation.Publication.RollbackUnpublished(null, managed.Dispose);
                }
                finally
                {
                    managed = null; forward = null;
                    try { if (demand != IntPtr.Zero) Marshal.FreeHGlobal(demand); }
                    finally
                    {
                        demand = IntPtr.Zero;
                        try { if (claimed) ApiSharedNativeSites.Release(ApiSharedRuntime.ProcessInstance.NativeOwnership, SiteName); }
                        finally { claimed = false; }
                    }
                }
            }

            private void VerifyLive()
            {
                NativeIndirectEntry.VerifyBytes(loaded.ModuleHandle + NativeStorageNativeDefinition.ArmoryAdmissionRva, NativeStorageNativeDefinition.ArmoryAdmissionBytes);
                NativeIndirectEntry.VerifyBytes(loaded.ModuleHandle + NativeStorageNativeDefinition.ArmoryFreeRva, NativeStorageNativeDefinition.ArmoryFreeBytes);
                NativeIndirectEntry.VerifyBytes(loaded.ModuleHandle + NativeStorageNativeDefinition.GranaryFreeRva, NativeStorageNativeDefinition.GranaryFreeBytes);
                Hash(new ReadOnlySpan<byte>((void*)loaded.ModuleHandle, loaded.Memory.Length), NativeStorageNativeDefinition.GranaryFinderRva,
                    NativeStorageNativeDefinition.GranaryFinderLength, NativeStorageNativeDefinition.GranaryFinderSha256);
                Hash(new ReadOnlySpan<byte>((void*)loaded.ModuleHandle, loaded.Memory.Length), NativeStorageNativeDefinition.GranaryRecomputeRva,
                    NativeStorageNativeDefinition.GranaryRecomputeLength, NativeStorageNativeDefinition.GranaryRecomputeSha256);
            }
            private void ValidatePrepared(bool installed)
            {
                NativeIndirectEntry.Validate(admission.Hook, loaded.ModuleHandle + NativeStorageNativeDefinition.ArmoryAdmissionRva,
                    NativeStorageNativeDefinition.ArmoryAdmissionBytes, AdmissionDisplaced, installed);
                NativeIndirectEntry.Validate(armoryFree.Hook, loaded.ModuleHandle + NativeStorageNativeDefinition.ArmoryFreeRva,
                    NativeStorageNativeDefinition.ArmoryFreeBytes, ArmoryFreeDisplaced, installed);
                NativeIndirectEntry.Validate(granaryFree.Hook, loaded.ModuleHandle + NativeStorageNativeDefinition.GranaryFreeRva,
                    NativeStorageNativeDefinition.GranaryFreeBytes, GranaryFreeDisplaced, installed);
                NativeInlineStub.Verify(finder, Address(loaded.ModuleHandle) + NativeStorageNativeDefinition.GranaryFinderApertureRva,
                    NativeStorageNativeDefinition.GranaryFinderApertureLength, installed,
                    stub => NativeStorageAssembly.ExpectedStub(Address(loaded.ModuleHandle), stub, Address(demand),
                        Address(Marshal.GetFunctionPointerForDelegate(aggregateFilter))),
                    check => "The native storage inline " + check + " contract differs.");
                NativeIndirectEntry.VerifyBytes(loaded.ModuleHandle + NativeStorageNativeDefinition.GranaryFinderContinuationRva,
                    NativeStorageNativeDefinition.GranaryFinderContinuationBytes);
            }

            private int Admission(IntPtr manager, int slot, int good, int owner, int amount)
            {
                int native = admission.Original(manager, slot, good, owner, amount);
                if (native == 0 || !Active) return native;
                try
                {
                    if (!TryReadCore(manager, slot, out NativeStorageProbe probe, out bool unsupported)) return unsupported ? native : 0;
                    if (probe.Kind != StoragePool.Armory) return native;
                    if (!NativeStorageRuntime.Instance.TryGetNativeOccupied(probe, out int occupied) || occupied < 0) return 0;
                    return occupied == 0 || (long)probe.NativeUsed + occupied + amount <= probe.Capacity ? native : 0;
                }
                catch (Exception error) { Report(error); return 0; }
            }
            private int ArmoryFree(IntPtr manager, int slot)
            {
                int native = armoryFree.Original(manager, slot);
                return !Active ? native : AdjustFree(manager, slot, native, StoragePool.Armory);
            }
            private int GranaryFree(IntPtr manager, int slot, int good, int capacity)
            {
                int native = granaryFree.Original(manager, slot, good, capacity);
                return !Active ? native : AdjustFree(manager, slot, native, StoragePool.Granary);
            }
            private int AdjustFree(IntPtr manager, int slot, int native, StoragePool kind)
            {
                try
                {
                    if (!TryReadCore(manager, slot, out NativeStorageProbe probe, out bool unsupported)) return unsupported ? native : 0;
                    if (probe.Kind != kind) return native;
                    if (!NativeStorageRuntime.Instance.TryGetNativeOccupied(probe, out int occupied) || occupied < 0) return 0;
                    return occupied == 0 ? native : (int)Math.Max(0L, (long)native - occupied);
                }
                catch (Exception error) { Report(error); return 0; }
            }
            private int FilterGranaryAggregate(IntPtr manager, int slot, int aggregate)
            {
                if (!Active) return aggregate;
                try
                {
                    if (!TryReadCore(manager, slot, out NativeStorageProbe probe, out bool unsupported)) return unsupported ? aggregate : int.MaxValue;
                    if (probe.Kind != StoragePool.Granary) return aggregate;
                    if (!NativeStorageRuntime.Instance.TryGetNativeOccupied(probe, out int occupied) || occupied < 0) return int.MaxValue;
                    if (occupied == 0) return aggregate;
                    return aggregate < 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, (long)aggregate + occupied);
                }
                catch (Exception error) { Report(error); return int.MaxValue; }
            }
            private long AddGood(Delegate original, IntPtr manager, int slot, int global, int good, int amount, int capacity, int add)
            {
                if (!Active || amount <= 0 || capacity <= 0) return forward(original, manager, slot, global, good, amount, capacity, add);
                bool forwarding = false;
                try
                {
                    if (!TryReadCore(manager, slot, out NativeStorageProbe probe, out bool unsupported))
                    {
                        if (!unsupported) return 0L;
                        forwarding = true; return forward(original, manager, slot, global, good, amount, capacity, add);
                    }
                    // The SE argument is signed Int32 but represents the native UInt32 bits.
                    if (global == 0 || probe.GlobalId != unchecked((uint)global))
                    { forwarding = true; return forward(original, manager, slot, global, good, amount, capacity, add); }
                    if (!NativeStorageRuntime.Instance.TryGetNativeOccupied(probe, out int occupied) || occupied < 0) return 0L;
                    if (occupied == 0)
                    { forwarding = true; return forward(original, manager, slot, global, good, amount, capacity, add); }
                    if (good < 0 || good >= NativeStorageNativeDefinition.NativeGoodsCount) return 0L;
                    long available = Math.Max(0L, (long)probe.Capacity - probe.NativeUsed - occupied);
                    if (amount > available) return 0L; // Preserve the whole lot; never shrink its amount.
                    var building = (GameBuilding*)(Address(manager) + NativeStorageNativeDefinition.BuildingManagerArrayOffset +
                        (ulong)slot * NativeStorageNativeDefinition.BuildingStride);
                    uint selected = ((uint*)((byte*)building + NativeStorageNativeDefinition.LocalGoodsOffset))[good];
                    long ceiling = Math.Min((long)capacity, selected + available);
                    if (ceiling <= 0 || ceiling > int.MaxValue) return 0L;
                    forwarding = true;
                    return forward(original, manager, slot, global, good, amount, (int)ceiling, add);
                }
                catch (Exception error)
                {
                    // Never retry Original after an exception: it may already have mutated native state.
                    Report(error);
                    if (forwarding) throw;
                    return 0L;
                }
            }
            private void Report(Exception error)
            {
                if (Volatile.Read(ref faults) != 0 || Interlocked.CompareExchange(ref faults, 1, 0) != 0) return;
                try { NativeApiLog.Error(log, "Native storage callback fault (no automatic native retry): " + error); } catch { }
            }
        }

        internal static NativeSiteSpan[] Spans()
        {
            var spans = new List<NativeSiteSpan>();
            AddEntry(spans, NativeStorageNativeDefinition.ArmoryAdmissionRva, AdmissionDisplaced, NativeStorageNativeDefinition.ArmoryAdmissionLength);
            AddEntry(spans, NativeStorageNativeDefinition.ArmoryFreeRva, ArmoryFreeDisplaced, NativeStorageNativeDefinition.ArmoryFreeLength);
            AddEntry(spans, NativeStorageNativeDefinition.GranaryFreeRva, GranaryFreeDisplaced, NativeStorageNativeDefinition.GranaryFreeLength);
            spans.Add(new NativeSiteSpan(NativeStorageNativeDefinition.GranaryFinderApertureRva, NativeStorageNativeDefinition.GranaryFinderApertureLength, NativeSiteMode.Exclusive));
            spans.Add(new NativeSiteSpan(NativeStorageNativeDefinition.GranaryFinderRva,
                NativeStorageNativeDefinition.GranaryFinderApertureRva - NativeStorageNativeDefinition.GranaryFinderRva, NativeSiteMode.Observe));
            spans.Add(new NativeSiteSpan(NativeStorageNativeDefinition.GranaryFinderContinuationRva,
                NativeStorageNativeDefinition.GranaryFinderRva + NativeStorageNativeDefinition.GranaryFinderLength - NativeStorageNativeDefinition.GranaryFinderContinuationRva, NativeSiteMode.Observe));
            spans.Add(new NativeSiteSpan(NativeStorageNativeDefinition.GranaryRecomputeRva, NativeStorageNativeDefinition.GranaryRecomputeLength, NativeSiteMode.Observe));
            spans.Add(new NativeSiteSpan(NativeStorageNativeDefinition.AddGoodRva, 1, NativeSiteMode.CallOnly));
            return spans.ToArray();
        }
        private static void AddEntry(List<NativeSiteSpan> spans, int rva, int displaced, int length)
        { spans.Add(new NativeSiteSpan(rva, displaced, NativeSiteMode.Exclusive)); spans.Add(new NativeSiteSpan(rva + displaced, length - displaced, NativeSiteMode.Observe)); }
    }
}
