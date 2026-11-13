using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.NativeSites;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    internal static unsafe partial class WeaponProductionNativeRuntime
    {
        private static readonly StoredDeliveryAdapter[] StoredDeliveryAdapters = new StoredDeliveryAdapter[WeaponProductionNativeDefinition.Roles.Length];
        private static readonly string[] StoredDeliveryDiagnostics = new string[WeaponProductionNativeDefinition.Roles.Length];
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StoredDeliveryFilter(int physicalUnitId);
        private static ulong Address(IntPtr pointer) => unchecked((ulong)pointer.ToInt64());

        // Optional capability only. Failure never changes existing native recipe admission.
        internal static bool CanDeliverStoredGoods(eStructs building)
        {
            for (int i = 0; i < StoredDeliveryAdapters.Length; i++)
                if (WeaponProductionNativeDefinition.Roles[i].Building == building)
                    return StoredDeliveryAdapters[i]?.Ready == true && StoredDeliveryRouteMatches(building);
            return false;
        }
        internal static string StoredDeliveryAvailabilityReason(eStructs building)
        {
            for (int i = 0; i < StoredDeliveryAdapters.Length; i++)
                if (WeaponProductionNativeDefinition.Roles[i].Building == building)
                    return StoredDeliveryAdapters[i]?.Ready == true
                        ? StoredDeliveryRouteMatches(building) ? string.Empty : "Stored Tanner output requires its original yield/delivery route."
                        : StoredDeliveryDiagnostics[i] ?? "Stored-goods delivery has not initialized.";
            return "Unsupported stored-output workshop type.";
        }
        private static bool StoredDeliveryRouteMatches(eStructs building) => building != eStructs.STRUCT_TANNERS_WORKSHOP ||
            module != IntPtr.Zero && WeaponProductionNativeDefinition.HasVanillaStoredDeliveryRoute(
                new ReadOnlySpan<byte>((void*)module, WeaponProductionNativeDefinition.TannerStoredRouteBoundaryRva +
                    WeaponProductionNativeDefinition.TannerStoredRouteBoundaryBytes.Length), building);

        private static void TryInitializeStoredDelivery(CrusaderLibraryLoadContext context, WeaponProductionNativeRole role, int index)
        {
            try
            {
                if (!NativeStorageDeliveryEngine.ValidateLayout())
                    throw new InvalidOperationException("The native carried/destination unit layout is unavailable.");
                if (Offset<GameUnit>(nameof(GameUnit.r_AIState)) != WeaponProductionNativeDefinition.StoredAiStateOffset ||
                    typeof(GameUnit).GetField(nameof(GameUnit.r_AIState))?.FieldType != typeof(ushort) ||
                    Offset<GameUnit>(nameof(GameUnit.r_TimeSinceDeathTicker)) != WeaponProductionNativeDefinition.StoredRetryTimerOffset ||
                    typeof(GameUnit).GetField(nameof(GameUnit.r_TimeSinceDeathTicker))?.FieldType != typeof(ushort))
                    throw new InvalidOperationException("The native delivery retry state/timer word layout differs.");
                var site = WeaponProductionNativeDefinition.StoredDeliverySites.Single(item => item.Building == role.Building);
                var candidate = new StoredDeliveryAdapter(role, site, Adapters[index]);
                StoredDeliveryAdapters[index] = candidate;
                candidate.Install(context);
            }
            catch (Exception error)
            {
                StoredDeliveryDiagnostics[index] = error.Message;
                try { NativeApiLog.Error(logger, "Stored-goods delivery for " + role.Building + " unavailable: " + error.Message); } catch { }
            }
        }

        private static void SetStoredDeliveryDemand(bool demand)
        { foreach (StoredDeliveryAdapter adapter in StoredDeliveryAdapters) adapter?.SetDemand(demand); }

        internal static string StoredDeliveryGroupName(eStructs building) => "Economy.WeaponProduction.StoredDelivery." + building;
        internal static NativeSiteSpan[] StoredDeliverySpans(WeaponStoredDeliverySite site)
        {
            return new[] {
                new NativeSiteSpan(site.Rva, site.Bytes.Length, NativeSiteMode.Exclusive),
                new NativeSiteSpan(site.Rva + site.Bytes.Length, site.Next.Length, NativeSiteMode.Observe),
                new NativeSiteSpan(site.EpilogueRva, site.EpilogueBytes.Length, NativeSiteMode.Observe)
            };
        }
        internal static IReadOnlyList<KeyValuePair<string, NativeSiteSpan[]>> StoredDeliveryGroups()
        {
            var sites = WeaponProductionNativeDefinition.StoredDeliverySites;
            var groups = new KeyValuePair<string, NativeSiteSpan[]>[sites.Length];
            for (int i = 0; i < groups.Length; i++)
                groups[i] = new KeyValuePair<string, NativeSiteSpan[]>(StoredDeliveryGroupName(sites[i].Building), StoredDeliverySpans(sites[i]));
            return groups;
        }

        // Permission is supplied only by the storage owner's fresh world/save/actor
        // preflight. A denial must precede every pointer read. Preserve a legitimate
        // native hazard diversion (for example state 0x6E); only delivery -> search is
        // authorized. Search may still find no store or choose its own native hazard path.
        internal static bool TryRetargetStoredDelivery(GameUnit* worker, eStructs building, bool authorized)
        {
            if (!authorized || worker == null) return false;
            ushort delivery, search;
            switch (building)
            {
                case eStructs.STRUCT_FLETCHERS_WORKSHOP: delivery = 7; search = 6; break;
                case eStructs.STRUCT_POLETURNERS_WORKSHOP:
                case eStructs.STRUCT_BLACKSMITHS_WORKSHOP:
                case eStructs.STRUCT_ARMOURERS_WORKSHOP:
                case eStructs.STRUCT_TANNERS_WORKSHOP: delivery = 8; search = 7; break;
                default: return false;
            }
            ushort* state = (ushort*)((byte*)worker + WeaponProductionNativeDefinition.StoredAiStateOffset);
            if (*state != delivery || *(short*)((byte*)worker + WeaponProductionNativeDefinition.StoredCargoOffset) <= 0) return false;
            *state = search;
            *(ushort*)((byte*)worker + WeaponProductionNativeDefinition.StoredRetryTimerOffset) = 0;
            return true;
        }

        private sealed class StoredDeliveryAdapter
        {
            private readonly WeaponProductionNativeRole role;
            private readonly WeaponStoredDeliverySite site;
            private readonly Adapter native;
            private readonly NativeHookPublicationState publication = new NativeHookPublicationState();
            private readonly NativeInlineStubGroup group;
            private readonly HookHandle<X64InlineHook> handle = new HookHandle<X64InlineHook>();
            private readonly StoredDeliveryFilter callback;
            private IntPtr enabled;
            private int fault;
            internal bool Ready => publication.IsReady;

            internal StoredDeliveryAdapter(WeaponProductionNativeRole role, WeaponStoredDeliverySite site, Adapter native)
            {
                this.role = role; this.site = site; this.native = native; callback = Deliver;
                group = new NativeInlineStubGroup(StoredDeliveryGroupName(role.Building), publication);
            }
            internal void SetDemand(bool demand)
            { if (Ready) Volatile.Write(ref *(int*)enabled, demand ? 1 : 0); }

            internal void Install(CrusaderLibraryLoadContext context)
            {
                ulong image = Address(module);
                var function = WorkerProductionBonusNativeDefinition.Functions.Single(item => item.Rva == role.WorkerRva);
                if (!native.Ready || Sha256Hex.Of(context.Memory.Slice(function.Rva, function.Length).ToArray()) != function.Sha256)
                    throw new InvalidOperationException("The stored-delivery worker/native recipe cohort differs.");
                // Our accepted target is the existing owner's Complete hook, not a second
                // hook over it. Require that complete prepared/published cohort to match.
                native.ValidatePrepared(true);
                VerifyBytes(context, site.Rva, site.Bytes);
                VerifyBytes(context, site.Rva + site.Bytes.Length, site.Next);
                VerifyBytes(context, site.EpilogueRva, site.EpilogueBytes);
                ValidateCompatibility(context);
                if (NativeInlineStub.ProbeDisplacement(image + (uint)site.Rva, site.Bytes.Length) != site.Bytes.Length)
                    throw new InvalidOperationException("The stored-delivery displacement differs.");
                group.Claim(StoredDeliverySpans(site));
                try
                {
                    enabled = Marshal.AllocHGlobal(sizeof(int)); Marshal.WriteInt32(enabled, 0);
                    ulong address = Address(Marshal.GetFunctionPointerForDelegate(callback));
                    WeaponProductionNativeAssembly.ExpectedStoredDeliveryStub(site, image, image + NativeInlineStub.PreflightStubOffset,
                        Address(enabled), Address(buildingDemand), Address(WeaponProductionRuntime.WorkerWireAddress),
                        Address(WeaponProductionRuntime.StoredOutputOptionsAddress), address);
                    group.Begin(context.Region);
                    group.Add(handle, image + (uint)site.Rva,
                        (a, original, back) => WeaponProductionNativeAssembly.GenerateStoredDelivery(a, original, back,
                            site, image, Address(enabled), Address(buildingDemand), Address(WeaponProductionRuntime.WorkerWireAddress),
                            Address(WeaponProductionRuntime.StoredOutputOptionsAddress), address), site.Bytes.Length);
                    var result = group.Commit("The optional stored-delivery cohort did not prepare.");
                    ValidatePrepared(false);
                    group.Publish(result, this, () => ValidatePrepared(true));
                }
                catch
                {
                    // Publication owns the candidate before the first possible native write.
                    // Failed published gates retain DWORD/callback/stub forever at demand zero.
                    group.RollbackUnpublished(() =>
                    {
                        if (enabled != IntPtr.Zero) Marshal.FreeHGlobal(enabled);
                        enabled = IntPtr.Zero;
                    });
                    throw;
                }
            }

            private static void VerifyBytes(CrusaderLibraryLoadContext context, int rva, byte[] bytes)
            {
                if (!NativeInlineStub.SnapshotMatches(context.Memory, rva, bytes) ||
                    !new ReadOnlySpan<byte>((void*)(module + rva), bytes.Length).SequenceEqual(bytes))
                    throw new InvalidOperationException("Stored-delivery native bytes differ at 0x" + rva.ToString("X") + ".");
            }
            private void ValidatePrepared(bool installed)
            {
                native.ValidatePrepared(true);
                if (site.Building == eStructs.STRUCT_TANNERS_WORKSHOP)
                {
                    NativeIndirectEntry.VerifyBytes(module + WeaponProductionNativeDefinition.TannerStoredYieldBoundaryRva,
                        WeaponProductionNativeDefinition.TannerStoredYieldBoundaryBytes);
                    NativeIndirectEntry.VerifyBytes(module + WeaponProductionNativeDefinition.TannerStoredRouteBoundaryRva,
                        WeaponProductionNativeDefinition.TannerStoredRouteBoundaryBytes);
                }
                NativeInlineStub.Verify(handle, Address(module + site.Rva), site.Bytes.Length, installed,
                    stub => WeaponProductionNativeAssembly.ExpectedStoredDeliveryStub(site, Address(module), stub,
                        Address(enabled), Address(buildingDemand), Address(WeaponProductionRuntime.WorkerWireAddress),
                        Address(WeaponProductionRuntime.StoredOutputOptionsAddress), Address(Marshal.GetFunctionPointerForDelegate(callback))),
                    check => "Stored-delivery " + check + " differs at 0x" + site.Rva.ToString("X") + ".");
                NativeIndirectEntry.VerifyBytes(module + site.Rva + site.Bytes.Length, site.Next);
                NativeIndirectEntry.VerifyBytes(module + site.EpilogueRva, site.EpilogueBytes);
            }

            private void ValidateCompatibility(CrusaderLibraryLoadContext context)
            {
                if (!WeaponProductionNativeDefinition.HasVanillaStoredDeliveryRoute(context.Memory, role.Building) ||
                    !WeaponProductionNativeDefinition.HasVanillaStoredDeliveryRoute(
                        new ReadOnlySpan<byte>((void*)module, context.Memory.Length), role.Building))
                    throw new InvalidOperationException("Stored Tanner output requires its original yield/delivery route; native and ImprovedHunters recipes remain available.");
            }

            // 0 Native; 1 custom credit+cargo debit committed; 2 retain cargo/destination
            // and, only when authorized, schedule native search on an eligible update.
            // The emitter preserves this physical ID across
            // its callback; never reconstruct a cargo footer from a later mutable global.
            private int Deliver(int unitId)
            {
                bool custom = false;
                try
                {
                    if (unitId != CurrentUnit) return 2;
                    if (!TryGetWorker(role, unitId, out GameUnit* worker) ||
                        !WeaponProductionRuntime.TryGetBatch(worker->r_LinkedProductionBuildingId, worker->r_LinkedProductionBuildingGlobalId,
                            unitId, worker->r_GlobalId, out WeaponProductionOption option) || option.StoredOutput == null)
                        return 0;
                    custom = true;
                    if (!StoredDeliveryRouteMatches(role.Building))
                    {
                        ReportFault(true, "Stored Tanner route changed after its recipe was admitted.");
                        return 2;
                    }
                    int destination = *(ushort*)((byte*)worker + WeaponProductionNativeDefinition.StoredDestinationOffset);
                    uint destinationGlobal = *(uint*)((byte*)worker + WeaponProductionNativeDefinition.StoredDestinationGlobalOffset);
                    if (NativeStorageRuntime.Instance.TryDeliver(option.StoredOutput, unitId, worker->r_GlobalId,
                        worker->r_LinkedProductionBuildingId, worker->r_LinkedProductionBuildingGlobalId,
                        destination, destinationGlobal, worker, out _)) return 1;
                    bool retryAllowed = NativeStorageRuntime.Instance.CanMutateProductionActor(option.StoredOutput,
                        unitId, worker->r_GlobalId, worker->r_LinkedProductionBuildingId,
                        worker->r_LinkedProductionBuildingGlobalId, worker, out _);
                    TryRetargetStoredDelivery(worker, role.Building, retryAllowed);
                    return 2;
                }
                catch (Exception error)
                {
                    // Do not disable a demanded custom gate after a fault: reverting to the
                    // native fallback would lose its cargo or issue the template good.
                    ReportFault(custom, "Stored-delivery callback fault.", error);
                    return custom ? 2 : 0;
                }
            }
            private void ReportFault(bool custom, string message, Exception error = null)
            {
                if (Volatile.Read(ref fault) != 0 || Interlocked.CompareExchange(ref fault, 1, 0) != 0) return;
                if (custom) try { WorkerProductionWorldSafety.Fault(message); } catch { }
                try { NativeApiLog.Error(logger, error == null ? message : message + " " + error); } catch { }
            }
        }
    }
}
