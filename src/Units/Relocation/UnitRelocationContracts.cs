using System;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Units
{
    /// <summary>Audited relocation origin, captured at the original native entry.</summary>
    public enum UnitRelocationOrigin
    {
        /// <summary>Ordinary native caller, including unaffiliated direct native calls.</summary>
        Native,
        /// <summary>Audited Vanilla invalid-standing-component rescue, identified at the original entry.</summary>
        InvalidStandingTerrainRecovery,
        /// <summary>The exact synchronous request made through this shared service.</summary>
        ExplicitApiRequest
    }
    /// <summary>Immutable synchronous observation; neither a native pointer nor mutation authority.</summary>
    public sealed class UnitRelocationContext
    {
        internal UnitRelocationContext(int id,uint global,eChimps type,int owner,int x,int y,ushort height,UnitRelocationOrigin origin,string originOwnerGuid=null)
        {UnitId=id;GlobalId=global;UnitType=type;ControllingPlayerId=owner;TargetX=x;TargetY=y;Height=height;Origin=origin;OriginOwnerGuid=originOwnerGuid;}
        /// <summary>One-based native unit slot; slot reuse is checked separately by GlobalId.</summary>
        public int UnitId {get;}
        /// <summary>Exact nonzero native lifetime identifier.</summary>
        public uint GlobalId {get;}
        /// <summary>Audited native carrier type at this synchronous boundary.</summary>
        public eChimps UnitType {get;}
        /// <summary>Current native controlling-player identity.</summary>
        public int ControllingPlayerId {get;}
        /// <summary>Destination local tile X coordinate.</summary>
        public int TargetX {get;}
        /// <summary>Destination local tile Y coordinate.</summary>
        public int TargetY {get;}
        /// <summary>Native base height argument; not a surface/passability guarantee.</summary>
        public ushort Height {get;}
        /// <summary>Original entry provenance, captured before callback wrappers.</summary>
        public UnitRelocationOrigin Origin {get;}
        /// <summary>Cooperative caller attribution for this exact ExplicitApiRequest; null for native callers. It is not authorization.</summary>
        public string OriginOwnerGuid {get;}
    }
    /// <summary>Exact-lifetime request in an identified local native world. Session/generation are not network identities.</summary>
    public readonly struct UnitRelocationRequest
    {
        /// <summary>Creates an exact-lifetime request; construction grants no mutation permission.</summary>
        public UnitRelocationRequest(long sessionId,long generation,int unitId,uint globalId,eChimps unitType,int controllingPlayerId,int targetX,int targetY)
        {SessionId=sessionId;Generation=generation;UnitId=unitId;GlobalId=globalId;UnitType=unitType;ControllingPlayerId=controllingPlayerId;TargetX=targetX;TargetY=targetY;}
        /// <summary>Current local mission session from WorldMutationEvents.</summary>
        public long SessionId {get;}
        /// <summary>Current local native-world generation from WorldMutationEvents.</summary>
        public long Generation {get;}
        /// <summary>One-based native unit slot; slot reuse is checked separately by GlobalId.</summary>
        public int UnitId {get;}
        /// <summary>Exact nonzero native lifetime identifier.</summary>
        public uint GlobalId {get;}
        /// <summary>Audited native carrier type at this synchronous boundary.</summary>
        public eChimps UnitType {get;}
        /// <summary>Current native controlling-player identity.</summary>
        public int ControllingPlayerId {get;}
        /// <summary>Destination local tile X coordinate.</summary>
        public int TargetX {get;}
        /// <summary>Destination local tile Y coordinate.</summary>
        public int TargetY {get;}
    }
    /// <summary>Guarded outcome; no value implies job or route continuation.</summary>
    public enum UnitRelocationStatus
    {
        /// <summary>The shared owner or its integrity is unavailable.</summary>
        Unavailable,
        /// <summary>Timing, occupancy, recursion or request admission failed before Original.</summary>
        Rejected,
        /// <summary>The requested lifetime, type or controller no longer matches.</summary>
        IdentityChanged,
        /// <summary>A registered policy cancelled the Original.</summary>
        Vetoed,
        /// <summary>Original returned and exact lifetime, pose and occupancy match, including a same-target request.</summary>
        VerifiedRelocated,
        /// <summary>An invoked Original failed or its final state could not be qualified.</summary>
        PostconditionFailed
    }
    /// <summary>VerifiedRelocated means a qualified Original returned and final identity/pose/occupancy match, including same-target calls. It promises no job or route continuation.</summary>
    public sealed class UnitRelocationResult
    {
        internal UnitRelocationResult(UnitRelocationStatus status,bool invoked=false,bool returned=false)
        {Status=status;NativeInvoked=invoked;NativeReturned=returned;}
        /// <summary>Qualified outcome of this request.</summary>
        public UnitRelocationStatus Status {get;}
        /// <summary>Whether the matching native Original was admitted and invoked, excluding policy veto.</summary>
        public bool NativeInvoked {get;}
        /// <summary>Whether that admitted native Original returned normally.</summary>
        public bool NativeReturned {get;}
    }
    /// <summary>Process-lived policy. Initially disabled; no Dispose or native unpatch.</summary>
    public sealed class UnitRelocationPolicyRegistration
    {
        private readonly UnitRelocationPolicies registry;
        internal readonly UnitRelocationPolicies.Policy Policy;
        internal UnitRelocationPolicyRegistration(UnitRelocationPolicies registry,UnitRelocationPolicies.Policy policy){this.registry=registry;Policy=policy;}
        /// <summary>Current logical activation of this registration, independent of shared service availability.</summary>
        public bool IsEnabled=>System.Threading.Volatile.Read(ref Policy.Enabled)!=0;
        /// <summary>Changes this policy's logical activation and native demand. Call after every owned settings/session transition.</summary>
        public void SetEnabled(bool enabled)=>registry.SetEnabled(Policy,enabled);
    }
    /// <summary>Single owner of native unit relocation. Consumers retain game rules, network authorization and task continuation.</summary>
    public static class UnitRelocationService
    {
        /// <summary>Actual entry/stub integrity. A detected loss permanently revokes the service until process exit.</summary>
        public static bool IsAvailable=>UnitRelocationRuntime.IsAvailable;
        /// <summary>Explicit startup acquisition, on the LibraryLoaded thread before native mission initialization. Unknown existing owners are refused.</summary>
        public static bool EnsureAvailable(string ownerGuid,out string reason)=>UnitRelocationRuntime.Ensure(ownerGuid,out reason);
        /// <summary>Registers an initially disabled read-only veto in order, ordinal owner then ID order. Null unitType observes all types.</summary>
        /// <remarks>True cancels. Exceptions are isolated, allowing independent policies and Vanilla. Callbacks run outside locks on the native caller thread;
        /// they must not mutate game state, block, dispatch UI or recursively relocate. Registrations persist; SetEnabled controls activation.</remarks>
        public static bool TryRegisterPolicy(string ownerGuid,string registrationId,eChimps? unitType,Func<UnitRelocationContext,bool> shouldCancel,
            out UnitRelocationPolicyRegistration registration,out string reason,int order=0)
        {
            registration=null;
            if(!UnitRelocationPolicies.ValidArguments(ownerGuid,registrationId,unitType,shouldCancel))
            {reason="A stable owner/ID, veto and UInt16 unit type are required.";return false;}
            if(!EnsureAvailable(ownerGuid,out reason))return false;
            return UnitRelocationRuntime.Policies.Register(ownerGuid,registrationId,unitType,shouldCancel,order,out registration,out reason);
        }
        /// <summary>One exact-lifetime relocation in the existing simulation-tick mutation scope while holding the audited engine monitor, outside construction batches.</summary>
        /// <remarks>Caller retains deterministic authority and destination/state admission. Identity, world and bounded source/destination chains are checked again after policies.
        /// Mounted/special units are refused. True establishes final identity/pose/occupancy, not movement, path search, worker resumption or replacement crew.</remarks>
        public static bool TryRelocate(string ownerGuid,UnitRelocationRequest request,out UnitRelocationResult result,out string reason)=>
            UnitRelocationRuntime.TryRelocate(ownerGuid,request,out result,out reason);
    }
}
