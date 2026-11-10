using System;
using System.Collections.Generic;
using CrusaderDE;
using APIShared.Events;
using APIShared.Recruitment;

namespace APIShared.Commands
{
    /// <summary>Read-only arguments after all successful Pre changes, before acceptance and the original call.</summary>
    public sealed class GameActionFinalValidationEventArgs
    {
        internal GameActionFinalValidationEventArgs(GameActionPreEventArgs pre, object state)
        {
            Command = pre.Command;
            OriginalStructureId = pre.OriginalStructureId;
            OriginalActionState = pre.OriginalActionState;
            OriginalValue2 = pre.OriginalValue2;
            StructureId = pre.StructureId;
            ActionState = pre.ActionState;
            Value2 = pre.Value2;
            State = state;
        }
        /// <summary>Action entering the managed hook.</summary>
        public Enums.GameActionCommand Command { get; }
        /// <summary>First payload at entry.</summary>
        public int OriginalStructureId { get; }
        /// <summary>Second payload at entry.</summary>
        public int OriginalActionState { get; }
        /// <summary>Third payload at entry.</summary>
        public int OriginalValue2 { get; }
        /// <summary>Final first payload.</summary>
        public int StructureId { get; }
        /// <summary>Final second payload.</summary>
        public int ActionState { get; }
        /// <summary>Final third payload.</summary>
        public int Value2 { get; }
        /// <summary>This registration's own Pre state, or null if absent or Pre failed.</summary>
        public object State { get; }
    }

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
        {
            Command = command;
            this.structure = OriginalStructureId = structure;
            this.state = OriginalActionState = state;
            this.value2 = OriginalValue2 = value2;
        }
        /// <summary>Vanilla action, not a KeyFunctions input action.</summary>
        public Enums.GameActionCommand Command { get; }
        /// <summary>Original first payload. For MakeTroop this is an amount, not a building ID.</summary>
        public int OriginalStructureId { get; }
        internal int OriginalActionState { get; }
        internal int OriginalValue2 { get; }
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
        /// <summary>True when Pre or final validation vetoed the entire managed call, including native effects and managed feedback.</summary>
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
        internal delegate int Call(Enums.GameActionCommand command, int structure, int state, int value2);
        private static readonly ManagedInterceptionHook<Call> hook = new ManagedInterceptionHook<Call>();
        // Static factory with explicit context: an observed call creates no closure or delegate.
        private static readonly Func<Completion, object, GameActionPostEventArgs> PostFactory =
            (completion, ownerState) => new GameActionPostEventArgs(completion.Pre, completion.Result, ownerState);
        internal static readonly InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs> Registry =
            new InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs>(() => hook.Install(typeof(EngineInterface),
                nameof(EngineInterface.GameAction), typeof(int),
                new[] { typeof(Enums.GameActionCommand), typeof(int), typeof(int), typeof(int) }, Dispatch, "GameAction"));
        /// <summary>Registers on the Unity startup thread. Installs the validated managed hook on first use and retains callbacks for the process lifetime. Ordering: order, ordinal owner GUID, ordinal ID. Publishers run synchronously on the actual caller thread without replay or dispatch. Pre vetoes are sticky; failing Pre mutations roll back. Post follows both executed and vetoed calls, but not an original exception. Optional accepted callbacks see frozen final inputs after all vetoes, before the original; use them only for preparations that must precede the action. Acceptance is not a native success acknowledgement. Callbacks must not issue nested GameAction commands; nested calls from notifications bypass notification and retain original semantics except for explicit protection registered with <see cref="TryRegisterWithFinalValidation"/>. Use deterministic policies on all multiplayer peers. Registrations during notification join the next call. Returns false with a reason on duplicate identity or installation failure.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, Action<GameActionPreEventArgs> pre,
            Action<GameActionPostEventArgs> post, out string reason, int order = 0,
            Action<GameActionAcceptedEventArgs> accepted = null) =>
            Registry.TryRegister(ownerGuid, registrationId, pre, post, out reason, order,
                accepted == null ? (Action<GameActionPreEventArgs, object>)null : (args, state) => accepted(new GameActionAcceptedEventArgs(args, state)));

        /// <summary>
        /// Adds a final validator at the same managed hook with the ordering, thread and lifetime of <see cref="TryRegister"/>.
        /// Validation runs after every Pre and before Accepted. The first rejection or exception vetoes the call.
        /// A Pre veto skips validation, and Post sees all vetoes. Validators must only check inputs and private State.
        /// The optional command list is copied at registration. Calls to those commands from within a notification
        /// return zero without executing or notifying while the protecting registration participates in that invocation.
        /// The optional activation predicate must be thread-safe, side-effect-free and must not call GameAction.
        /// A predicate error is reported and treated as active. Activation is captured before Pre for the entire invocation.
        /// </summary>
        public static bool TryRegisterWithFinalValidation(string ownerGuid, string registrationId,
            Action<GameActionPreEventArgs> pre, Action<GameActionPostEventArgs> post,
            Func<GameActionFinalValidationEventArgs, bool> validate, out string reason, int order = 0,
            Action<GameActionAcceptedEventArgs> accepted = null,
            IEnumerable<Enums.GameActionCommand> strictNestedKinds = null, Func<bool> isActive = null) =>
            TryRegisterValidator(Registry, ownerGuid, registrationId, pre, post, validate, out reason, order,
                accepted, strictNestedKinds, isActive);

        internal static bool TryRegisterValidator(InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs> registry,
            string ownerGuid, string registrationId, Action<GameActionPreEventArgs> pre, Action<GameActionPostEventArgs> post,
            Func<GameActionFinalValidationEventArgs, bool> validate, out string reason, int order = 0,
            Action<GameActionAcceptedEventArgs> accepted = null,
            IEnumerable<Enums.GameActionCommand> strictNestedKinds = null, Func<bool> isActive = null)
        {
            if (validate == null)
            {
                reason = "A final validator is required.";
                return false;
            }
            var protectedCommands = strictNestedKinds == null ? null : new HashSet<Enums.GameActionCommand>(strictNestedKinds);
            return registry.TryRegister(ownerGuid, registrationId, pre, post, out reason, order,
                accepted == null ? (Action<GameActionPreEventArgs, object>)null : (args, state) => accepted(new GameActionAcceptedEventArgs(args, state)),
                isActive, (args, state) => validate(new GameActionFinalValidationEventArgs(args, state)),
                protectedCommands == null ? (Func<int, bool>)null : key => protectedCommands.Contains((Enums.GameActionCommand)key));
        }

        /// <summary>Total isolated callback errors.</summary>
        public static long CallbackFailures => Registry.CallbackFailures;
        private static int Dispatch(Enums.GameActionCommand command, int structure, int state, int value2) =>
            DispatchThrough(Registry, command, structure, state, value2, hook.Original);

        internal static int DispatchThrough(InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs> registry,
            Enums.GameActionCommand command, int structure, int state, int value2, Call original)
        {
            if (!registry.CanNotify)
                return registry.BlocksNestedCall((int)command) ? 0 : original(command, structure, state, value2);
            return DispatchObserved(registry, command, structure, state, value2, original);
        }
        // Production observed path; contract tests pass their own registry and original.
        internal static int DispatchObserved(InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs> registry,
            Enums.GameActionCommand command, int structure, int state, int value2, Call original)
        {
            var invocation = registry.Begin(new GameActionPreEventArgs(command, structure, state, value2));
            var pre = invocation.Pre;
            int result = 0;
            invocation.ValidateFinalInputs();
            if (!pre.SkipOriginalFunction)
            {
                invocation.NotifyAccepted();
                result = original(command, pre.StructureId, pre.ActionState, pre.Value2);
            }
            if (invocation.HasPostObservers)
                invocation.Complete(new Completion(pre, result), PostFactory);
            return result;
        }
        private readonly struct Completion
        {
            internal readonly GameActionPreEventArgs Pre;
            internal readonly int Result;
            internal Completion(GameActionPreEventArgs pre, int result) { Pre = pre; Result = result; }
        }
    }
}
