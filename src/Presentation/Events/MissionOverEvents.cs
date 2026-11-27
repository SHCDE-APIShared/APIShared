using System;
using System.Threading;
using APIShared.Events;
using CrusaderDE;

namespace APIShared.Presentation
{
    /// <summary>Presentation boundaries, not mission lifecycle or authoritative defeat events.</summary>
    public enum MissionOverOperation
    {
        /// <summary>HUD_MissionOver.ButtonClicked(string).</summary>
        Button,
        /// <summary>GameData.Scenarios.setGameOverState(int,int,int).</summary>
        State,
        /// <summary>OnScreenText.addOSTEntry for OST_MP_GAME_OVER only.</summary>
        Text
    }

    /// <summary>A retained, at-most-once button original. It bypasses event dispatch, not other existing hook chains.</summary>
    public sealed class MissionOverContinuation
    {
        private Action original;
        private Func<bool> valid;
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private int consumed;
        internal Exception OriginalException { get; private set; }
        internal MissionOverContinuation(Action original, Func<bool> valid) { this.original = original; this.valid = valid; }
        /// <summary>Invoke on the original caller thread only. Rechecks the consumer's session/view guard.
        /// Invalid context consumes the continuation; a wrong-thread attempt does not. A failing guard
        /// rejects execution. Original exceptions propagate and the original is never retried.</summary>
        public bool TryInvoke()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread || Volatile.Read(ref consumed) != 0) return false;
            if (Interlocked.Exchange(ref consumed, 1) != 0) return false;
            Action call = original; Func<bool> check = valid;
            original = null; valid = null;
            try { if (!check()) return false; }
            catch (Exception error) { InterceptionDiagnostics.Report("MissionOver/continuation-guard", error); return false; }
            try { call(); return true; }
            catch (Exception error) { OriginalException = error; throw; }
        }
        internal void Invalidate() { Interlocked.Exchange(ref consumed, 1); original = null; valid = null; }
    }

    /// <summary>Typed presentation inputs. Only Result and button deferral may be changed during Pre.</summary>
    public sealed class MissionOverPreEventArgs : InterceptionPreEventArgs
    {
        private int result;
        internal Action<MissionOverContinuation> Deferred;
        internal Func<bool> Valid;
        internal MissionOverPreEventArgs(MissionOverOperation operation, object receiver, string button,
            int result, int screen, int date, int data4 = 0, int data5 = 0)
        { Operation = operation; Receiver = receiver; Button = button; this.result = OriginalResult = result;
            Screen = screen; Date = date; Data4 = data4; Data5 = data5; }
        /// <summary>Intercepted boundary.</summary>
        public MissionOverOperation Operation { get; }
        /// <summary>Exact managed receiver. Retaining it does not extend its HUD/session lifetime.</summary>
        public object Receiver { get; }
        /// <summary>Button receiver, or null for state/text.</summary>
        public HUD_MissionOver View => Receiver as HUD_MissionOver;
        /// <summary>Unmodified Vanilla button parameter; null for other operations.</summary>
        public string Button { get; }
        /// <summary>Original state/data1; zero for buttons.</summary>
        public int OriginalResult { get; }
        /// <summary>Final state/data1; button calls cannot change it. Does not alter native victory/defeat.</summary>
        public int Result { get => result; set { CheckMutable(); if (Operation == MissionOverOperation.Button)
                throw new InvalidOperationException("A button has no result state."); result = value; } }
        /// <summary>Unmodified screen/data2; zero for buttons.</summary>
        public int Screen { get; }
        /// <summary>Unmodified skirmishDate/data3; zero for buttons.</summary>
        public int Date { get; }
        /// <summary>Unmodified OST data4; zero outside Text.</summary>
        public int Data4 { get; }
        /// <summary>Unmodified OST data5; zero outside Text.</summary>
        public int Data5 { get; }
        /// <summary>Select a button handler executed after every Pre veto. The last successful selection wins.
        /// The handler may retain its continuation. Supply a side-effect-free session AND HUD identity guard,
        /// evaluated immediately before any original execution. A veto suppresses the handler too.
        /// A failing handler falls back to that guarded continuation; it cannot repeat an attempted original.</summary>
        public void DeferOriginal(Action<MissionOverContinuation> handler, Func<bool> isCurrent)
        {
            CheckMutable();
            if (Operation != MissionOverOperation.Button) throw new InvalidOperationException("Only buttons can be deferred.");
            Deferred = handler ?? throw new ArgumentNullException(nameof(handler));
            Valid = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        }
        internal override object Capture() => new Snapshot(result, Deferred, Valid);
        internal override void Restore(object value) { var saved = (Snapshot)value; result = saved.Result; Deferred = saved.Deferred; Valid = saved.Valid; }
        private readonly struct Snapshot
        {
            internal readonly int Result;
            internal readonly Action<MissionOverContinuation> Deferred;
            internal readonly Func<bool> Valid;
            internal Snapshot(int result, Action<MissionOverContinuation> deferred, Func<bool> valid) { Result = result; Deferred = deferred; Valid = valid; }
        }
    }

    /// <summary>Immutable completion of dispatch, including veto and errors. Deferred completion does not imply the original ran.</summary>
    public sealed class MissionOverPostEventArgs
    {
        internal MissionOverPostEventArgs(MissionOverPreEventArgs args, object state, bool completed, Exception error)
        { Operation = args.Operation; Receiver = args.Receiver; Button = args.Button; OriginalResult = args.OriginalResult;
            Result = args.Result; Screen = args.Screen; Date = args.Date; Data4 = args.Data4; Data5 = args.Data5;
            WasSkipped = args.SkipOriginalFunction; WasDeferred = !WasSkipped && args.Deferred != null;
            OriginalCompleted = completed; State = state; CompletionException = error; }
        /// <summary>Intercepted boundary.</summary>
        public MissionOverOperation Operation { get; }
        /// <summary>Exact managed receiver; use only within its valid lifetime.</summary>
        public object Receiver { get; }
        /// <summary>Original button parameter.</summary>
        public string Button { get; }
        /// <summary>Original state/data1.</summary>
        public int OriginalResult { get; }
        /// <summary>Final state/data1.</summary>
        public int Result { get; }
        /// <summary>Unmodified screen/data2.</summary>
        public int Screen { get; }
        /// <summary>Unmodified date/data3.</summary>
        public int Date { get; }
        /// <summary>Unmodified data4.</summary>
        public int Data4 { get; }
        /// <summary>Unmodified data5.</summary>
        public int Data5 { get; }
        /// <summary>A successful Pre veto suppressed original and deferral.</summary>
        public bool WasSkipped { get; }
        /// <summary>A deferral handler was selected.</summary>
        public bool WasDeferred { get; }
        /// <summary>The original completed synchronously before this notification.</summary>
        public bool OriginalCompleted { get; }
        /// <summary>This registration's private Pre state.</summary>
        public object State { get; }
        /// <summary>Original/deferral exception. Only original exceptions are rethrown after Post.
        /// Later continuation errors reach its caller only.</summary>
        public Exception CompletionException { get; }
    }

    /// <summary>One permanent managed hook owner per end-screen boundary; no mod policy or native state mutation.</summary>
    public static class MissionOverEvents
    {
        private delegate void ButtonCall(HUD_MissionOver self, string button);
        private delegate void StateCall(GameData.Scenarios self, int state, int screen, int date);
        private delegate void TextCall(OnScreenText self, Enums.eOnScreenText text, int a, int b, int c, int d, int e);
        private static readonly ManagedInterceptionHook<ButtonCall> buttonHook = new ManagedInterceptionHook<ButtonCall>();
        private static readonly ManagedInterceptionHook<StateCall> stateHook = new ManagedInterceptionHook<StateCall>();
        private static readonly ManagedInterceptionHook<TextCall> textHook = new ManagedInterceptionHook<TextCall>();
        private static readonly InterceptionEvent<MissionOverPreEventArgs, MissionOverPostEventArgs>[] sites = {
            new InterceptionEvent<MissionOverPreEventArgs, MissionOverPostEventArgs>(() => buttonHook.Install(typeof(HUD_MissionOver), "ButtonClicked", typeof(void), new[] { typeof(string) }, Button, "MissionOverButton")),
            new InterceptionEvent<MissionOverPreEventArgs, MissionOverPostEventArgs>(() => stateHook.Install(typeof(GameData.Scenarios), "setGameOverState", typeof(void), new[] { typeof(int), typeof(int), typeof(int) }, State, "MissionOverState")),
            new InterceptionEvent<MissionOverPreEventArgs, MissionOverPostEventArgs>(() => textHook.Install(typeof(OnScreenText), "addOSTEntry", typeof(void), new[] { typeof(Enums.eOnScreenText), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) }, Text, "MissionOverText"))
        };
        /// <summary>Register during Unity startup. Ordered by order, owner GUID, ID; process lifetime, no replay.
        /// Callbacks run synchronously on the caller thread outside registry locks. Pre failures roll back;
        /// vetoes are sticky; accepted runs after all Pre only without veto; Post also runs on exceptions.
        /// Nested calls from notifications bypass notifications. No hook is installed before first registration.
        /// Consumers dispatch UI themselves and retain published callbacks for the process lifetime.</summary>
        public static bool TryRegister(MissionOverOperation operation, string ownerGuid, string registrationId,
            Action<MissionOverPreEventArgs> pre, Action<MissionOverPostEventArgs> post, out string reason,
            int order = 0, Action<MissionOverPreEventArgs> accepted = null)
        {
            int index = (int)operation;
            if (index < 0 || index >= sites.Length) { reason = "Unknown mission-over operation."; return false; }
            return sites[index].TryRegister(ownerGuid, registrationId, pre, post, out reason, order,
                accepted == null ? (Action<MissionOverPreEventArgs, object>)null : (args, _) => accepted(args));
        }
        private static void Button(HUD_MissionOver self, string button)
        {
            if (!sites[0].CanNotify) { buttonHook.Original(self, button); return; }
            ObservedButton(self, button);
        }
        private static void ObservedButton(HUD_MissionOver self, string button)
        {
            Execute(sites[0], new MissionOverPreEventArgs(MissionOverOperation.Button, self, button, 0, 0, 0),
                args => buttonHook.Original(self, args.Button));
        }
        private static void State(GameData.Scenarios self, int state, int screen, int date)
        {
            if (!sites[1].CanNotify) { stateHook.Original(self, state, screen, date); return; }
            ObservedState(self, state, screen, date);
        }
        private static void ObservedState(GameData.Scenarios self, int state, int screen, int date)
        {
            Execute(sites[1], new MissionOverPreEventArgs(MissionOverOperation.State, self, null, state, screen, date),
                args => stateHook.Original(self, args.Result, args.Screen, args.Date));
        }
        private static void Text(OnScreenText self, Enums.eOnScreenText text, int a, int b, int c, int d, int e)
        {
            if (text != Enums.eOnScreenText.OST_MP_GAME_OVER || !sites[2].CanNotify) { textHook.Original(self, text, a, b, c, d, e); return; }
            ObservedText(self, text, a, b, c, d, e);
        }
        private static void ObservedText(OnScreenText self, Enums.eOnScreenText text, int a, int b, int c, int d, int e)
        {
            Execute(sites[2], new MissionOverPreEventArgs(MissionOverOperation.Text, self, null, a, b, c, d, e),
                args => textHook.Original(self, text, args.Result, args.Screen, args.Date, args.Data4, args.Data5));
        }
        internal static void Execute(InterceptionEvent<MissionOverPreEventArgs, MissionOverPostEventArgs> events,
            MissionOverPreEventArgs args, Action<MissionOverPreEventArgs> original)
        {
            var invocation = events.Begin(args);
            bool completed = false;
            Exception error = null;
            MissionOverContinuation continuation = null;
            try
            {
                if (!args.SkipOriginalFunction)
                {
                    invocation.NotifyAccepted();
                    if (args.Deferred == null) { original(args); completed = true; }
                    else
                    {
                        continuation = new MissionOverContinuation(() => { original(args); completed = true; }, args.Valid);
                        try { args.Deferred(continuation); }
                        catch (Exception deferredError)
                        {
                            if (ReferenceEquals(continuation.OriginalException, deferredError)) throw;
                            error = deferredError;
                            InterceptionDiagnostics.Report("MissionOver/deferred-handler", deferredError);
                            continuation.TryInvoke();
                        }
                    }
                }
            }
            catch (Exception ex) { error = ex; continuation?.Invalidate(); throw; }
            finally { if (invocation.HasPostObservers) invocation.Complete(state => new MissionOverPostEventArgs(args, state, completed, error)); }
        }
    }
}
