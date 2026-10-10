using System;
using CrusaderDE;
using APIShared.Events;
using APIShared.Recruitment;

namespace APIShared.Commands
{
    /// <summary>Final accepted inputs after every Pre, immediately before the original. Read-only; no later participant can veto. Acceptance does not prove native success.</summary>
    public sealed class GameActionAcceptedEventArgs
    {
        internal GameActionAcceptedEventArgs(GameActionPreEventArgs pre, object state)
        { Command = pre.Command; StructureId = pre.StructureId; ActionState = pre.ActionState; Value2 = pre.Value2; State = state; }
        /// <summary>Action about to enter the original managed chain.</summary>
        public Enums.GameActionCommand Command { get; }
        /// <summary>Final first payload.</summary>
        public int StructureId { get; }
        /// <summary>Final second payload.</summary>
        public int ActionState { get; }
        /// <summary>Final third payload.</summary>
        public int Value2 { get; }
        /// <summary>Registration-local state from Pre.</summary>
        public object State { get; }
    }
    /// <summary>Mutable arguments to the managed GameActionCommand overload, before its lock, native call and feedback. Command cannot change; payload meaning depends on it.</summary>
    public sealed class GameActionPreEventArgs : InterceptionPreEventArgs
    {
        private int structure, state, value2;
        private bool concrete;
        internal GameActionPreEventArgs(Enums.GameActionCommand command, int structure, int state, int value2)
        { Command = command; this.structure = OriginalStructureId = structure; this.state = state; this.value2 = value2; }
        /// <summary>Vanilla action, not a KeyFunctions input action.</summary>
        public Enums.GameActionCommand Command { get; }
        /// <summary>Original first payload. For MakeTroop this is an amount, not a building ID.</summary>
        public int OriginalStructureId { get; }
        /// <summary>Current first payload. Explicit assignment marks a MakeTroop amount concrete, even when unchanged.</summary>
        public int StructureId { get => structure; set { CheckMutable(); structure = value; concrete = true; } }
        /// <summary>Current second payload; for MakeTroop the Vanilla unit type.</summary>
        public int ActionState { get => state; set { CheckMutable(); state = value; } }
        /// <summary>Current third payload, unchanged unless a participant assigns it.</summary>
        public int Value2 { get => value2; set { CheckMutable(); value2 = value; } }
        /// <summary>Whether the current MakeTroop request is the unmodified Vanilla Ctrl ceiling.</summary>
        public bool InterpretCtrlSentinel => Command == Enums.GameActionCommand.MakeTroop && !concrete &&
            OriginalStructureId == RecruitmentRequestPolicy.VanillaCtrlAllAmount && structure == OriginalStructureId;
        /// <summary>Whether a participant explicitly supplied a concrete MakeTroop amount.</summary>
        public bool HasConcreteRecruitmentAmount => Command == Enums.GameActionCommand.MakeTroop && concrete;
        internal override object Capture() => new Snapshot(structure, state, value2, concrete);
        internal override void Restore(object snapshot)
        { var saved = (Snapshot)snapshot; structure = saved.Structure; state = saved.State; value2 = saved.Value2; concrete = saved.Concrete; }
        private readonly struct Snapshot
        {
            internal readonly int Structure, State, Value2;
            internal readonly bool Concrete;
            internal Snapshot(int structure, int state, int value2, bool concrete)
            { Structure = structure; State = state; Value2 = value2; Concrete = concrete; }
        }
    }

    /// <summary>Immutable completion of one managed call or veto. It does not acknowledge a network Chore or soldier creation.</summary>
    public sealed class GameActionPostEventArgs
    {
        internal GameActionPostEventArgs(GameActionPreEventArgs pre, int result, object state)
        { Command = pre.Command; OriginalStructureId = pre.OriginalStructureId; StructureId = pre.StructureId;
            ActionState = pre.ActionState; Value2 = pre.Value2; WasSkipped = pre.SkipOriginalFunction;
            HasConcreteRecruitmentAmount = pre.HasConcreteRecruitmentAmount; Result = result; State = state; }
        /// <summary>Action forwarded or vetoed.</summary>
        public Enums.GameActionCommand Command { get; }
        /// <summary>First payload at entry.</summary>
        public int OriginalStructureId { get; }
        /// <summary>Final first payload; MakeTroop amount, not an ID.</summary>
        public int StructureId { get; }
        /// <summary>Final second payload.</summary>
        public int ActionState { get; }
        /// <summary>Final third payload.</summary>
        public int Value2 { get; }
        /// <summary>True when Pre vetoed the entire managed call, including native effects and managed feedback.</summary>
        public bool WasSkipped { get; }
        /// <summary>True for an explicitly replaced MakeTroop amount.</summary>
        public bool HasConcreteRecruitmentAmount { get; }
        /// <summary>Original managed return value, or zero for a veto. Zero is not a failed-recruitment acknowledgement.</summary>
        public int Result { get; }
        /// <summary>This registration's own Pre state; null if absent or its Pre failed.</summary>
        public object State { get; }
    }

    /// <summary>One process-owned hook for EngineInterface.GameAction(GameActionCommand, int, int, int).</summary>
    public static class GameActionEvents
    {
        private delegate int Call(Enums.GameActionCommand command, int structure, int state, int value2);
        private static readonly ManagedInterceptionHook<Call> hook = new ManagedInterceptionHook<Call>();
        internal static readonly InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs> Registry =
            new InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs>(() => hook.Install(typeof(EngineInterface),
                nameof(EngineInterface.GameAction), typeof(int),
                new[] { typeof(Enums.GameActionCommand), typeof(int), typeof(int), typeof(int) }, Dispatch, "GameAction"));
        /// <summary>Registers on the Unity startup thread. Installs the validated managed hook on first use and retains callbacks for the process lifetime. Ordering: order, ordinal owner GUID, ordinal ID. Publishers run synchronously on the actual caller thread without replay or dispatch. Pre vetoes are sticky; failing Pre mutations roll back. Post follows both executed and vetoed calls, but not an original exception. Optional accepted callbacks see frozen final inputs after all vetoes, before the original; use them only for preparations that must precede the action. Acceptance is not a native success acknowledgement. Callbacks must not issue nested GameAction commands; nested calls from notifications bypass notification and retain original semantics. Use deterministic policies on all multiplayer peers. Registrations during notification join the next call. Returns false with a reason on duplicate identity or installation failure.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, Action<GameActionPreEventArgs> pre,
            Action<GameActionPostEventArgs> post, out string reason, int order = 0,
            Action<GameActionAcceptedEventArgs> accepted = null) =>
            Registry.TryRegister(ownerGuid, registrationId, pre, post, out reason, order,
                accepted == null ? (Action<GameActionPreEventArgs, object>)null : (args, state) => accepted(new GameActionAcceptedEventArgs(args, state)));
        /// <summary>Total isolated callback errors.</summary>
        public static long CallbackFailures => Registry.CallbackFailures;
        private static int Dispatch(Enums.GameActionCommand command, int structure, int state, int value2)
        {
            var invocation = Registry.Begin(new GameActionPreEventArgs(command, structure, state, value2));
            var pre = invocation.Pre;
            int result = 0;
            if (!pre.SkipOriginalFunction)
            {
                invocation.NotifyAccepted();
                result = hook.Original(command, pre.StructureId, pre.ActionState, pre.Value2);
            }
            invocation.Complete(ownerState => new GameActionPostEventArgs(pre, result, ownerState));
            return result;
        }
    }
}
