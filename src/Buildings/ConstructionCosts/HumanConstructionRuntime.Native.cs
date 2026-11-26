using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using APIShared.NativeSites;
using APIShared.Networking;

namespace APIShared.Buildings
{
    /// <summary>Context boundary for authenticated custom self-demolition commands. Call inside the existing
    /// synchronized simulation operation, after validating ownership, permissions and complete affected closure.
    /// This method grants no authority and sends no network message. Nested calls restore their parent scope.</summary>
    public static class ConstructionDemolition
    {
        /// <summary>Runs an existing demolition operation with its verified actor for extra-cost refunds.</summary>
        public static void RunSelfDemolition(int actor, Action operation) => HumanConstructionRuntime.RunSelfDemolition(actor,operation);
    }

    internal static unsafe partial class HumanConstructionRuntime
    {
        internal const string DecorationSite = "Buildings.ConstructionDecorationRemoval";
        internal const int DecorationRemovalRva=0x9F410;
        // Indirect detour displaces exactly sub rsp,8 (4) and movsxd rax,edx (3).
        // All original branches target >=+0x2B, never the entry span's interior.
        private static readonly byte[] DecorationEntry={0x48,0x83,0xEC,0x08,0x48,0x63,0xC2};
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RemoveDecoration(IntPtr manager,int tile);
        private static DetourHandle<RemoveDecoration> decorationHook;
        private static HookTransaction decorationTransaction;
        private static readonly NativeHookPublicationState decorationPublication=new NativeHookPublicationState();
        internal static NativeSiteSpan[] DecorationSpans()=>new[]{new NativeSiteSpan(DecorationRemovalRva,7,NativeSiteMode.Exclusive)};
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FreeConstruction(IntPtr manager,int player,int type);
        private static FreeConstruction freeConstruction;
        internal static bool IsFreeConstruction(ConstructionCostTarget target,int player) => target.SupportsVanillaCosts &&
            freeConstruction((IntPtr)GameBuildingManagerAPI.Instance.GetBuildingManager().Pointer,player,target.Row)!=0;
        internal static NativeDetourOptions DecorationBackendOptions => new NativeDetourOptions { AllowedSchemes=DetourScheme.Indirect,
            FollowJumps=false,FollowCalls=false,Chaining=HookChainingPolicy.Refuse,FunctionScanLimit=121,SuspendThreadsDuringPatch=false };
        private static void InitializeDecorationRemoval(CrusaderLibraryLoadContext context,string hash,ManualLogSource logger)
        {
            if(!NativeStartupPatchWindow.IsOpen || hash!=ApiSharedRuntime.SupportedHash) throw new InvalidOperationException("Decoration removal needs the audited startup image.");
            if(!context.Memory.Slice(DecorationRemovalRva,7).SequenceEqual(DecorationEntry)) throw new InvalidOperationException("Decoration removal entry changed.");
            // The complete 121-byte function, including all branches and its terminal return, is pinned.
            if(ApiSharedRuntime.ComputeSha256(context.Memory.Slice(DecorationRemovalRva,121))!="E3F245F3E19DA54AD3C4AF52E3729A217DFAAC8198B1B21B25734222DE9719E9")
                throw new InvalidOperationException("Decoration removal function changed.");
            byte[] freeEntry={0x8B,0x05,0x12,0xC8,0x5A,0x03,0x83,0xF8,0x06,0x74,0x4D,0x83,0x3D,0x6E,0xB4,0x4B};
            if(!context.Memory.Slice(0xB9710,freeEntry.Length).SequenceEqual(freeEntry)) throw new InvalidOperationException("Free construction decision changed.");
            freeConstruction=Marshal.GetDelegateForFunctionPointer<FreeConstruction>(context.ModuleHandle+0xB9710);
            IntPtr target=context.ModuleHandle+DecorationRemovalRva;
            NativeIndirectEntry.VerifyBytes(target,DecorationEntry);
            var ownership=ApiSharedRuntime.ProcessInstance.NativeOwnership;
            ApiSharedNativeSites.Claim(ownership,DecorationSite,DecorationSpans(),r=>new InvalidOperationException(r));
            HookTransaction candidate=null;
            try
            {
                var backend=new NativeDetourBackend(DecorationBackendOptions);
                candidate=new HookTransaction(context.Region,SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions{OwnsHooks=true,InstallOnCommit=false,FailureMode=TransactionFailureMode.RollbackAndThrow,Backend=backend});
                decorationHook=new DetourHandle<RemoveDecoration>();
                candidate.AddDetour(decorationHook,HookTarget.FromAddress(unchecked((ulong)target.ToInt64())),(RemoveDecoration)RemoveDecorationTile);
                var result=candidate.Commit();
                if(!result.IsCompleteSuccess || !decorationHook.Success) throw new InvalidOperationException("Decoration removal preparation failed.");
                NativeIndirectEntry.Validate(decorationHook.Hook,target,DecorationEntry,7,false);
                decorationTransaction=candidate;
                NativeHookPublication.Enable(candidate,result,decorationPublication,typeof(HumanConstructionRuntime),
                    ()=>NativeIndirectEntry.Validate(decorationHook.Hook,target,DecorationEntry,7,true));
                NativeApiLog.Info(logger,"CONSTRUCTION_DECORATION_REMOVAL: method=reference-rva; rva=0x9F410; Indirect/7; process lifetime.");
            }
            catch
            {
                decorationPublication.RollbackUnpublished(candidate,()=>{decorationHook=null;decorationTransaction=null;ApiSharedNativeSites.Release(ownership,DecorationSite);});
                throw;
            }
        }
        internal static int? CurrentOwner(ConstructionObjectIdentity identity)
        {
            switch(identity.Kind)
            {
                case ConstructionObjectKind.Building:
                    if(BuildingAccess.TryGetById(identity.Slot,out GameBuilding* b,out _) && b->r_GlobalId==identity.GlobalId &&
                        (b->r_AliveState==AliveState.IsAlive || b->r_AliveState==AliveState.NeedsInit)) return b->r_PlayerIdOwner;
                    break;
                case ConstructionObjectKind.Unit:
                    if(UnitAccess.TryGetById(identity.Slot,out GameUnit* u,out _) && u->r_GlobalId==identity.GlobalId &&
                        (u->r_AliveState==AliveState.IsAlive || u->r_AliveState==AliveState.NeedsInit)) return u->r_ControllableForPlayerId;
                    break;
                case ConstructionObjectKind.Decoration:
                    var span=GameProjectileManagerAPI.Instance.GetProjectilesAsSpan();
                    if(identity.Slot>=25 && identity.Slot<span.Length && span[identity.Slot].r_GlobalId==identity.GlobalId &&
                        span[identity.Slot].r_ProjectileType==ProjectileType.Brazier &&
                        (span[identity.Slot].r_AliveState==AliveState.IsAlive || span[identity.Slot].r_AliveState==AliveState.NeedsInit)) return (ushort)span[identity.Slot].r_PlayerSourceId;
                    break;
            }
            return null;
        }
        private static void RemoveDecorationTile(IntPtr manager,int tile)
        {
            int actor=0; List<ConstructionObjectIdentity> candidates=null;
            try
            {
                actor=demolition?.Player??0;
                if(actor==0 && NativeChoreContext.TryCaptureRaw(97,out var chore)) actor=chore.Sender;
                if(ready && HumanConstructionCosts.HasReceipts)
                {
                    var span=GameProjectileManagerAPI.Instance.GetProjectilesAsSpan();
                    candidates=new List<ConstructionObjectIdentity>();
                    for(int slot=25;slot<span.Length;slot++)
                        if(span[slot].r_AliveState==AliveState.IsAlive && span[slot].r_ProjectileType==ProjectileType.Brazier &&
                            span[slot].r_CurrentTileId==(uint)tile)
                            candidates.Add(new ConstructionObjectIdentity(ConstructionObjectKind.Decoration,slot,span[slot].r_GlobalId));
                }
            }
            catch(Exception error) { ReportFailure("decoration demolition capture",error); }
            // Original always runs exactly once, even if optional capture or payout fails.
            decorationHook.Original(manager,tile);
            if(candidates==null) return;
            try
            {
                var span=GameProjectileManagerAPI.Instance.GetProjectilesAsSpan();
                foreach(var identity in candidates)
                {
                    ref GameProjectile p=ref span[identity.Slot];
                    if(p.r_GlobalId==identity.GlobalId && p.r_AliveState==AliveState.MarkedForDeletion)
                    {
                        var target=ConstructionCostTarget.FromStructure(eStructs.STRUCT_BRAZIER);
                        if(Human(actor) && (ushort)p.r_PlayerSourceId==actor)
                            PayRefund(identity,actor,(ushort)p.r_PlayerSourceId,ConstructionRefundPolicies.Evaluate(target,actor,50));
                        else HumanConstructionCosts.Retire(identity);
                    }
                }
            }
            catch(Exception error) { ReportFailure("decoration demolition refund",error); }
        }
    }
}
