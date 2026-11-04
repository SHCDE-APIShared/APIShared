using System;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Health
{
    // Audited 19A240 is shared by create, same-slot recruit/worker/disband and type reset.
    // It is not a final Lord HP boundary: C23C0 applies the profile after its return.
    internal static class HealthNativeObserver
    {
        internal const int Rva = 0x19A240;
        internal static readonly byte[] Entry = { 0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x6c,0x24,0x10,
            0x48,0x89,0x74,0x24,0x18,0x48,0x89,0x7c,0x24,0x20,0x41,0x56,0x48,0x83,0xec,0x20 };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void InitializeUnit(IntPtr manager,int unitId,int type);
        internal static readonly NativeDetourBackend Backend = new NativeDetourBackend(
            new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false });
        private static readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
        private static DetourHandle<InitializeUnit> hook;
        private static HookTransaction transaction;
        private static Action<int> initialized;

        internal static void Install(CrusaderLibraryLoadContext context,string hash,Action<int> callback,ManualLogSource log)
        {
            Publication.ThrowIfUnavailable(); if (Publication.IsReady) return;
            if (!string.Equals(hash,ApiSharedRuntime.SupportedHash,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("HP record layout requires the audited native image.");
            ValidateLayout();
            IntPtr target = context.ModuleHandle + Rva;
            if (context.Memory.Length < Rva + Entry.Length) throw new InvalidOperationException("HP initializer outside image.");
            for (int i=0;i<Entry.Length;i++)
                if (context.Memory[Rva+i] != Entry[i]) throw new InvalidOperationException("HP initializer reference bytes differ.");
            NativeSites.NativeIndirectEntry.VerifyBytes(target,Entry);
            ApiSharedNativeSites.Claim(ApiSharedRuntime.ProcessInstance.NativeOwnership,ApiSharedNativeSites.HealthInitialization,
                ApiSharedNativeSites.HealthInitializationSpans(),
                reason => new InvalidOperationException(reason));
            var candidate = new DetourHandle<InitializeUnit>(); HookTransaction pending = null;
            try
            {
                pending = new HookTransaction(context.Region,SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode=TransactionFailureMode.RollbackAndThrow,OwnsHooks=true,
                        InstallOnCommit=false,Backend=NativeStartupPatchWindow.Backend(Backend) });
                pending.AddDetour(candidate,HookTarget.FromAddress(unchecked((ulong)target.ToInt64())),(InitializeUnit)Invoke);
                CommitResult result = pending.Commit();
                if (!result.IsCompleteSuccess || !candidate.Success) throw new InvalidOperationException("HP observer preparation failed.");
                initialized=callback; Volatile.Write(ref hook,candidate); transaction=pending;
                NativeHookPublication.Enable(pending,result,Publication,typeof(HealthNativeObserver),
                    () => NativeSites.NativeIndirectEntry.Validate(candidate.Hook,target,Entry));
                NativeApiLog.Info(log,"HP initialization observer installed: method=reference-rva; rva=0x19A240; Indirect/10; process lifetime.");
            }
            catch
            {
                Publication.RollbackUnpublished(pending,() => {
                    ApiSharedNativeSites.Release(ApiSharedRuntime.ProcessInstance.NativeOwnership,ApiSharedNativeSites.HealthInitialization);
                    hook=null; transaction=null; initialized=null;
                });
                throw;
            }
        }
        internal static void ValidateLayout()
        {
            // Validate the actual loaded Extender view before any native hook or health write.
            if (Marshal.SizeOf(typeof(GameUnit)) != 0x490 || Marshal.SizeOf(typeof(GameBuilding)) != 0x32C ||
                Enum.GetUnderlyingType(typeof(AliveState)) != typeof(short) ||
                Enum.GetUnderlyingType(typeof(eChimps)) != typeof(ushort) ||
                Enum.GetUnderlyingType(typeof(eStructs)) != typeof(ushort))
                throw new InvalidOperationException("HP unit/building record size or enum width differs from the audited layout.");
            Field<GameUnit>(nameof(GameUnit.r_AliveState),0x88,typeof(AliveState));
            Field<GameUnit>(nameof(GameUnit.r_UnitChimp),0x8A,typeof(eChimps));
            Field<GameUnit>(nameof(GameUnit.r_ControllableForPlayerId),0x92,typeof(ushort));
            Field<GameUnit>(nameof(GameUnit.r_GlobalId),0x94,typeof(uint));
            Field<GameUnit>(nameof(GameUnit.r_IsKilledByProjectile),0x29C,typeof(ushort));
            Field<GameUnit>(nameof(GameUnit.r_CurrentHealthPercentage),0x2D0,typeof(ushort));
            Field<GameUnit>(nameof(GameUnit.r_LinkedProductionBuildingId),0x334,typeof(ushort));
            Field<GameUnit>(nameof(GameUnit.r_LinkedProductionBuildingGlobalId),0x364,typeof(uint));
            Field<GameUnit>(nameof(GameUnit.r_CurrentHealth),0x3C4,typeof(uint));
            Field<GameUnit>(nameof(GameUnit.r_MaxHealth),0x3C8,typeof(uint));
            Field<GameUnit>(nameof(GameUnit.r_HealthBarBlocks),0x34,typeof(uint));
            Field<GameBuilding>(nameof(GameBuilding.r_AliveState),0xD0,typeof(AliveState));
            Field<GameBuilding>(nameof(GameBuilding.r_BuildingType),0xD2,typeof(eStructs));
            Field<GameBuilding>(nameof(GameBuilding.r_PlayerIdOwner),0xD6,typeof(ushort));
            Field<GameBuilding>(nameof(GameBuilding.r_GlobalId),0xD8,typeof(uint));
            Field<GameBuilding>(nameof(GameBuilding.r_CurrentHealth),0x10C,typeof(short));
            Field<GameBuilding>(nameof(GameBuilding.r_MaxHealth),0x10E,typeof(ushort));
            Field<GameBuilding>(nameof(GameBuilding.N00001757),0x110,typeof(ushort));
            Field<GameBuilding>(nameof(GameBuilding.N000017CB),0x114,typeof(uint));
        }
        private static void Field<T>(string name,int offset,Type expectedType)
        {
            var field=typeof(T).GetField(name);
            if (field == null || field.FieldType != expectedType || Marshal.OffsetOf(typeof(T),name).ToInt64() != offset)
                throw new InvalidOperationException("HP native field layout differs: " + typeof(T).Name + "." + name);
        }
        private static void Invoke(IntPtr manager,int unitId,int type)
        {
            // Original exactly once. Neither queue nor logging failure can prevent Vanilla initialization.
            Volatile.Read(ref hook).Original(manager,unitId,type);
            if (!Publication.IsReady || !HealthRuntime.IsObserving) return;
            try { initialized(unitId); }
            catch (Exception error) { HealthRuntime.Report("native initialization notification",error); }
        }
    }
}
