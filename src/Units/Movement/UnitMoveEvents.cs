using System;

namespace APIShared.Units
{
    /// <summary>Process-owned final native unit-move observations through the existing SE owner.</summary>
    /// <remarks>Available from 0.74.0. Register once after APIShared initialization; use enabled for logical
    /// activation. No replay, Unity dispatch or implicit multiplayer/native mutation permission is provided.</remarks>
    public static class UnitMoveEvents
    {
        /// <summary>Whether the audited final-original adapter and common world lifecycle initialized.</summary>
        public static bool IsAvailable=>UnitMoveRuntime.Broker.IsAvailable;
        /// <summary>Registers private synchronous observers in order, owner GUID and ID ordinal order.</summary>
        /// <remarks>Callbacks, enabled predicates and reporters are isolated. Enabled must be cheap and
        /// nonmutating. Receipts already begun still complete when a registration becomes disabled.</remarks>
        public static bool TryRegister(string ownerGuid,string registrationId,IUnitMoveObserver observer,out string reason,
            int order=0,Func<bool> enabled=null)=>UnitMoveRuntime.Broker.Register(ownerGuid,registrationId,observer,enabled,order,out reason);
        /// <summary>Runs one synchronous guarded SE move wrapper and reports its actual native outcome.</summary>
        /// <remarks>The caller owns native admission, serialization and identity validation. identityCurrent
        /// must be cheap, read-only and validate stable unit identity, not fields the command legitimately changes.
        /// Redirected, duplicate and stale guarded originals are refused; independently nested Pre/Post commands
        /// retain their ordinary behavior and do not inherit this origin. Invocation exceptions are returned in
        /// result instead of escaping. True means exactly one matching current original returned one, even if
        /// legacy Post replaced the managed return or threw. Native zero is distinct from a veto or failure.
        /// A normal original result does not establish the unit's final route after foreign Post processing.</remarks>
        public static bool TryRun(string ownerGuid,int unitId,int x,int y,int extra,Func<bool> identityCurrent,Func<long> invoke,
            out UnitMoveRunResult result,out string reason)=>UnitMoveRuntime.Broker.TryRun(ownerGuid,unitId,x,y,extra,identityCurrent,invoke,out result,out reason);
    }
}
