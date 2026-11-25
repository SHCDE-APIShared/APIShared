using System;

namespace APIShared.Units
{
    /// <summary>Effective inputs immediately before one allowed native move original.</summary>
    /// <remarks>Calls run synchronously on the native publisher thread. No pointer, lock or mutation
    /// authority is granted. IDs and wrapper identities are process-local; no events are replayed.</remarks>
    public sealed class UnitMoveContext
    {
        internal UnitMoveContext(long invocation,long parent,long session,long generation,int unit,int x,int y,int extra,string owner)
        {InvocationId=invocation;ParentInvocationId=parent;SessionId=session;WorldGeneration=generation;
            UnitId=unit;X=x;Y=y;Extra=extra;OriginOwnerGuid=owner;}
        /// <summary>Identity of the enclosing SE wrapper.</summary>
        public long InvocationId { get; }
        /// <summary>Enclosing move wrapper identity; zero for a root call.</summary>
        public long ParentInvocationId { get; }
        /// <summary>Central mission attempt identity, local to this process.</summary>
        public long SessionId { get; }
        /// <summary>Common native-world generation, including load and repair invalidation.</summary>
        public long WorldGeneration { get; }
        /// <summary>Actual unit ID after mutable Pre callbacks.</summary>
        public int UnitId { get; }
        /// <summary>Actual native destination X after mutable Pre callbacks.</summary>
        public int X { get; }
        /// <summary>Actual native destination Y after mutable Pre callbacks.</summary>
        public int Y { get; }
        /// <summary>Unmodified final native extra argument.</summary>
        public int Extra { get; }
        /// <summary>Owner of a guarded TryRun bound to this wrapper, or null for an unrelated call.</summary>
        /// <remarks>A nested foreign Pre/Post order does not inherit the outer caller's origin.</remarks>
        public string OriginOwnerGuid { get; }
    }
    /// <summary>Native original outcome, captured before mutable legacy Post processing.</summary>
    public sealed class UnitMoveCompletion
    {
        internal UnitMoveCompletion(UnitMoveContext context){Context=context;}
        internal void Set(bool invoked,bool returned,long result,Exception failure,bool current)
        {NativeInvoked=invoked;NativeReturned=returned;NativeResult=result;Failure=failure;IsCurrentWorld=current;}
        /// <summary>The exact immutable begin context.</summary>
        public UnitMoveContext Context { get; }
        /// <summary>Whether this original was attempted.</summary>
        public bool NativeInvoked { get; private set; }
        /// <summary>Whether this original returned normally.</summary>
        public bool NativeReturned { get; private set; }
        /// <summary>Actual Int64 original result; never the SE Post replacement.</summary>
        public long NativeResult { get; private set; }
        /// <summary>Original exception when available; the broker isolates diagnostic formatting.</summary>
        public Exception Failure { get; private set; }
        /// <summary>The same identified world still exists at this original completion.</summary>
        /// <remarks>Later Post callbacks can change it; revalidate before delayed work.</remarks>
        public bool IsCurrentWorld { get; private set; }
    }
    /// <summary>Consumer-private state retained until its synchronous original completes.</summary>
    public interface IUnitMoveInvocation
    {
        /// <summary>Receives the matching original outcome once, in reverse observer order.</summary>
        void Complete(UnitMoveCompletion completion);
    }
    /// <summary>Observes actual final original arguments, without veto or replacement authority.</summary>
    public interface IUnitMoveObserver
    {
        /// <summary>Captures private state before the original, or returns null.</summary>
        IUnitMoveInvocation TryBegin(UnitMoveContext context);
    }
    /// <summary>Classification of a guarded single-wrapper move operation.</summary>
    public enum UnitMoveRunStatus
    {
        /// <summary>The original adapter has not initialized.</summary>
        Unavailable,
        /// <summary>Arguments or caller callbacks are missing or outside declared domains.</summary>
        InvalidRequest,
        /// <summary>No identified world was available before invoking the caller.</summary>
        UnidentifiedWorld,
        /// <summary>The wrapper vetoed or never reached its original.</summary>
        NoOriginal,
        /// <summary>Mutable Pre redirected the guarded original arguments.</summary>
        Redirected,
        /// <summary>The caller attempted more than one root wrapper/original.</summary>
        MultipleCalls,
        /// <summary>The caller's protected identity no longer matched.</summary>
        IdentityChanged,
        /// <summary>The native world changed during the operation.</summary>
        WorldChanged,
        /// <summary>The original threw or returned an unsupported result.</summary>
        NativeFailed,
        /// <summary>Exactly one matching current original returned zero.</summary>
        NativeRefused,
        /// <summary>Exactly one matching current original returned one.</summary>
        NativeSucceeded
    }
    /// <summary>Guarded operation facts; native and wrapper results remain distinct.</summary>
    public sealed class UnitMoveRunResult
    {
        internal UnitMoveRunResult(){ }
        /// <summary>Final guarded classification.</summary>
        public UnitMoveRunStatus Status { get; internal set; }
        /// <summary>Whether the guarded original was actually attempted.</summary>
        public bool NativeInvoked { get; internal set; }
        /// <summary>Whether the guarded original returned normally, regardless of legacy Post.</summary>
        public bool NativeReturned { get; internal set; }
        /// <summary>Actual Int64 original result, unaffected by Post replacement.</summary>
        public long NativeResult { get; internal set; }
        /// <summary>Native or identity-guard exception when available.</summary>
        public Exception Failure { get; internal set; }
        /// <summary>Whether the caller's entire managed invocation returned normally.</summary>
        public bool WrapperReturned { get; internal set; }
        /// <summary>Managed invocation result, which can be overridden by legacy Post.</summary>
        public long WrapperResult { get; internal set; }
        /// <summary>Managed invocation exception, including a failing legacy Post.</summary>
        public Exception WrapperFailure { get; internal set; }
        /// <summary>The initially identified native world remains current after the invocation.</summary>
        public bool IsCurrentWorld { get; internal set; }
    }
}
