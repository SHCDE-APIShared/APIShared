using System;
using System.Threading;
using APIShared.Events;
using CrusaderDE;

namespace APIShared.Presentation
{
    /// <summary>One managed ally-panel click before any handler or Vanilla action runs.</summary>
    public sealed class AllyPanelButtonPreEventArgs : InterceptionPreEventArgs
    {
        internal AllyPanelButtonPreEventArgs(HUD_AlliesPanel panel, string button)
        {
            Panel = panel;
            Button = button;
        }

        /// <summary>The panel that owns the current click. Use it synchronously on the caller thread.</summary>
        public HUD_AlliesPanel Panel { get; }
        /// <summary>The original button command string.</summary>
        public string Button { get; }

        internal Action<AllyPanelButtonInvocation> Handler { get; private set; }

        /// <summary>Claims the click if no earlier registration did. The handler runs after all Pre callbacks only if the click was not vetoed.</summary>
        public bool TryClaim(Action<AllyPanelButtonInvocation> execute)
        {
            CheckMutable();
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            if (Handler != null || SkipOriginalFunction) return false;
            Handler = execute;
            return true;
        }

        internal override object Capture() => Handler;
        internal override void Restore(object snapshot) => Handler = (Action<AllyPanelButtonInvocation>)snapshot;
    }

    /// <summary>Synchronous execution of a claimed ally-panel click on its original caller thread.</summary>
    public sealed class AllyPanelButtonInvocation
    {
        private readonly int callerThread = Thread.CurrentThread.ManagedThreadId;
        private Action<HUD_AlliesPanel, string> original;

        internal AllyPanelButtonInvocation(HUD_AlliesPanel panel, string button, Action<HUD_AlliesPanel, string> original)
        {
            Panel = panel;
            Button = button;
            this.original = original;
        }

        /// <summary>The panel that owns the current click. Use it synchronously on the caller thread.</summary>
        public HUD_AlliesPanel Panel { get; }
        /// <summary>The original button command string.</summary>
        public string Button { get; }

        internal bool OriginalCompleted { get; private set; }

        /// <summary>Calls the original directly at most once, with the supplied command, during this handler. Vanilla's own nested clicks can still reach the broker.</summary>
        public void InvokeOriginal(string button)
        {
            if (Thread.CurrentThread.ManagedThreadId != callerThread || original == null)
                throw new InvalidOperationException("The ally-panel original is only available once, on the handler's caller thread.");
            var execute = original;
            original = null;
            execute(Panel, button);
            OriginalCompleted = true;
        }

        internal void Close() => original = null;
    }

    /// <summary>Result of one ally-panel click. Managed completion does not acknowledge a gameplay effect.</summary>
    public sealed class AllyPanelButtonPostEventArgs
    {
        internal AllyPanelButtonPostEventArgs(AllyPanelButtonPreEventArgs pre, bool originalCompleted, Exception error, object state)
        {
            Panel = pre.Panel;
            Button = pre.Button;
            WasSkipped = pre.SkipOriginalFunction;
            WasClaimed = !WasSkipped && pre.Handler != null;
            OriginalCompleted = originalCompleted;
            CompletionException = error;
            State = state;
        }

        /// <summary>The panel that owns the current click. Use it synchronously on the caller thread.</summary>
        public HUD_AlliesPanel Panel { get; }
        /// <summary>The original button command string.</summary>
        public string Button { get; }
        /// <summary>Whether a Pre veto prevented both the claim and Vanilla from running.</summary>
        public bool WasSkipped { get; }
        /// <summary>Whether a claimed handler ran, even if it failed or forwarded to Vanilla.</summary>
        public bool WasClaimed { get; }
        /// <summary>Whether Vanilla returned normally for the outer invocation.</summary>
        public bool OriginalCompleted { get; }
        /// <summary>Any error escaping the original or the selected handler.</summary>
        public Exception CompletionException { get; }
        /// <summary>This observer's state from its own successful Pre callback.</summary>
        public object State { get; }
    }

    /// <summary>Process-owned interception of HUD_AlliesPanel.ButtonClicked(string).</summary>
    public static class AllyPanelButtonEvents
    {
        private static readonly ManagedInterceptionHook<Action<HUD_AlliesPanel, string>> Hook =
            new ManagedInterceptionHook<Action<HUD_AlliesPanel, string>>();
        private static readonly InterceptionEvent<AllyPanelButtonPreEventArgs, AllyPanelButtonPostEventArgs> Registry =
            new InterceptionEvent<AllyPanelButtonPreEventArgs, AllyPanelButtonPostEventArgs>(() =>
                Hook.Install(typeof(HUD_AlliesPanel), nameof(HUD_AlliesPanel.ButtonClicked), typeof(void),
                    new[] { typeof(string) }, Dispatch, "AllyPanelButton"));
        private static readonly Func<Completion, object, AllyPanelButtonPostEventArgs> PostFactory =
            (completion, state) => new AllyPanelButtonPostEventArgs(completion.Pre, completion.OriginalCompleted, completion.Error, state);

        [ThreadStatic] private static bool notifying;

        /// <summary>Registers on Unity for the process lifetime. Order is ascending order, then ordinal GUID and ID.
        /// Pre and Post run synchronously on the caller thread with isolated observer errors and owner-local State.
        /// Claims are first-wins and sticky vetoes suppress all execution. Clicks issued from this broker's Pre or Post are suppressed.
        /// The selected handler and Vanilla execute outside notification, so their synchronous nested clicks are separate invocations.
        /// Handler and original exceptions propagate after Post and are never retried.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, Action<AllyPanelButtonPreEventArgs> pre,
            Action<AllyPanelButtonPostEventArgs> post, out string reason, int order = 0) =>
            Registry.TryRegister(ownerGuid, registrationId, pre, post, out reason, order);

        private static void Dispatch(HUD_AlliesPanel panel, string button)
        {
            if (notifying) return;
            if (!Registry.CanNotify)
            {
                Hook.Original(panel, button);
                return;
            }

            Execute(Registry, panel, button, Hook.Original);
        }

        internal static void Execute(InterceptionEvent<AllyPanelButtonPreEventArgs, AllyPanelButtonPostEventArgs> registry,
            HUD_AlliesPanel panel, string button, Action<HUD_AlliesPanel, string> original)
        {
            if (notifying) return;
            InterceptionEvent<AllyPanelButtonPreEventArgs, AllyPanelButtonPostEventArgs>.Invocation observers;
            notifying = true;
            try { observers = registry.Begin(new AllyPanelButtonPreEventArgs(panel, button)); }
            finally { notifying = false; }

            var pre = observers.Pre;
            AllyPanelButtonInvocation claim = null;
            bool originalCompleted = false;
            Exception error = null;
            try
            {
                if (pre.SkipOriginalFunction) return;
                if (pre.Handler == null)
                {
                    original(panel, button);
                    originalCompleted = true;
                    return;
                }

                claim = new AllyPanelButtonInvocation(panel, button, original);
                pre.Handler(claim);
            }
            catch (Exception exception)
            {
                error = exception;
                throw;
            }
            finally
            {
                claim?.Close();
                if (observers.HasPostObservers)
                {
                    notifying = true;
                    try
                    {
                        observers.Complete(new Completion(pre, originalCompleted || (claim?.OriginalCompleted ?? false), error), PostFactory);
                    }
                    finally { notifying = false; }
                }
            }
        }

        private readonly struct Completion
        {
            internal readonly AllyPanelButtonPreEventArgs Pre;
            internal readonly bool OriginalCompleted;
            internal readonly Exception Error;

            internal Completion(AllyPanelButtonPreEventArgs pre, bool originalCompleted, Exception error)
            {
                Pre = pre;
                OriginalCompleted = originalCompleted;
                Error = error;
            }
        }
    }
}
