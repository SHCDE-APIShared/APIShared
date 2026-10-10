using System;
using CrusaderDE;
using APIShared.Events;

namespace APIShared.Presentation
{
    /// <summary>Named managed HUD interception points. These are presentation operations, not native simulation events.</summary>
    public enum PresentationOperation
    {
        /// <summary>FatControler.NoesisGUIUpdateChecksInGame, including updates while paused.</summary>
        GuiChecks,
        /// <summary>HUD_Main.UpdateRollover after Vanilla tooltip selection.</summary>
        BuildingRollover,
        /// <summary>MainViewModel.ButtonEnterCreateTroop.</summary>
        RecruitmentEnter,
        /// <summary>MainViewModel.ButtonLeaveCreateTroop.</summary>
        RecruitmentLeave,
        /// <summary>MainViewModel.ButtonTroopPanelMouseEnter.</summary>
        TroopPanelEnter,
        /// <summary>MainViewModel.ButtonTroopPanelMouseLeave.</summary>
        TroopPanelLeave,
        /// <summary>MainViewModel.ButtonUnitRechargeRock; Pre can replace this UI action.</summary>
        RechargeSiegeAmmo
    }
    /// <summary>Immediate HUD inputs. The original object parameter is preserved. Never retain UI objects across panel/map replacement.</summary>
    public sealed class PresentationPreEventArgs : InterceptionPreEventArgs
    {
        private object parameter;
        private Action<object> replacement;
        internal PresentationPreEventArgs(PresentationOperation operation, object instance, object parameter)
        { Operation = operation; Instance = instance; this.parameter = OriginalParameter = parameter; }
        /// <summary>Named interception point.</summary>
        public PresentationOperation Operation { get; }
        /// <summary>The exact managed receiver; MainViewModel, HUD_Main or FatControler according to Operation.</summary>
        public object Instance { get; }
        /// <summary>UI receiver for recruitment, troop-panel hover and recharge actions; otherwise null.</summary>
        public MainViewModel ViewModel => Instance as MainViewModel;
        /// <summary>Building rollover receiver; otherwise null.</summary>
        public HUD_Main BuildingHud => Instance as HUD_Main;
        /// <summary>GUI-check receiver; otherwise null.</summary>
        public FatControler Controller => Instance as FatControler;
        /// <summary>Original button/hover parameter, or null for parameterless operations.</summary>
        public object OriginalParameter { get; }
        /// <summary>Current button/hover parameter. Parameterless operations must retain null.</summary>
        public object Parameter { get => parameter; set {
            CheckMutable();
            if ((Operation == PresentationOperation.GuiChecks || Operation == PresentationOperation.BuildingRollover) && value != null)
                throw new ArgumentException("This operation has no parameter.");
            parameter = value;
        } }
        /// <summary>Optional replacement executed once after every Pre, with the final parameter. Does not veto; any Pre veto suppresses both it and the original. Last successful assignment wins. Replacement exceptions propagate after Post cleanup; avoid partial effects followed by retry.</summary>
        public Action<object> Replacement { get => replacement; set { CheckMutable(); replacement = value; } }
        internal override object Capture() => new Snapshot(parameter, replacement);
        internal override void Restore(object snapshot) { var saved = (Snapshot)snapshot; parameter = saved.Parameter; replacement = saved.Replacement; }
        private readonly struct Snapshot
        {
            internal readonly object Parameter;
            internal readonly Action<object> Replacement;
            internal Snapshot(object parameter, Action<object> replacement) { Parameter = parameter; Replacement = replacement; }
        }
    }
    /// <summary>Immutable completion after the existing managed hook chain, a veto or an original exception.</summary>
    public sealed class PresentationPostEventArgs
    {
        internal PresentationPostEventArgs(PresentationPreEventArgs pre, object state, Exception error = null)
        { Operation = pre.Operation; Instance = pre.Instance; Parameter = pre.Parameter; WasSkipped = pre.SkipOriginalFunction; State = state; CompletionException = error; WasReplaced = !WasSkipped && pre.Replacement != null; }
        /// <summary>Named interception point.</summary>
        public PresentationOperation Operation { get; }
        /// <summary>Exact receiver. Use only synchronously; panel lifetime is not extended by this contract.</summary>
        public object Instance { get; }
        /// <summary>UI receiver for hover and recharge actions; otherwise null.</summary>
        public MainViewModel ViewModel => Instance as MainViewModel;
        /// <summary>Building rollover receiver; otherwise null.</summary>
        public HUD_Main BuildingHud => Instance as HUD_Main;
        /// <summary>GUI-check receiver; otherwise null.</summary>
        public FatControler Controller => Instance as FatControler;
        /// <summary>Final UI parameter passed to the original.</summary>
        public object Parameter { get; }
        /// <summary>True when an observer vetoed the complete managed operation. Its Vanilla UI writes did not run.</summary>
        public bool WasSkipped { get; }
        /// <summary>The original's exception, if any. It is rethrown after all Post observers; they cannot swallow it.</summary>
        public Exception OriginalException => WasReplaced ? null : CompletionException;
        /// <summary>Exception from the original or selected replacement; rethrown after Post cleanup.</summary>
        public Exception CompletionException { get; }
        /// <summary>Whether the selected replacement executed in place of the original. False on veto.</summary>
        public bool WasReplaced { get; }
        /// <summary>Whether the original returned normally. Check before extending its UI writes.</summary>
        public bool OriginalCompleted => !WasSkipped && !WasReplaced && CompletionException == null;
        /// <summary>This registration's private Pre state.</summary>
        public object State { get; }
    }

    /// <summary>Shared HUD publishers. Only requested sites install hooks; each site has exactly one APIShared owner.</summary>
    public static class PresentationEvents
    {
        private delegate void ViewCall(MainViewModel self, object parameter);
        private delegate void HudCall(HUD_Main self);
        private delegate void GuiCall(FatControler self);
        private static readonly Site[] sites = CreateSites();
        /// <summary>Registers during Unity startup for the process lifetime. Callbacks run synchronously on the HUD caller thread, outside locks, ordered by order then ordinal owner/ID. No replay or automatic dispatch. Pre may veto the whole operation; vetoes are sticky. Failed Pre changes roll back. Post also reports vetoes and original exceptions; it cannot cancel completed writes. Added registrations join the next invocation. Nested operations from notifications run their originals without recursive notification. Observe in Post when augmenting Vanilla UI, and leave other mods' controls untouched. Duplicate identities or unsupported signatures return false with a reason.</summary>
        public static bool TryRegister(PresentationOperation operation, string ownerGuid, string registrationId,
            Action<PresentationPreEventArgs> pre, Action<PresentationPostEventArgs> post,
            out string reason, int order = 0)
        {
            int index = (int)operation;
            if (index < 0 || index >= sites.Length) { reason = "Unknown presentation operation."; return false; }
            return sites[index].Events.TryRegister(ownerGuid, registrationId, pre, post, out reason, order);
        }
        /// <summary>Total isolated callback errors at this named site; zero for an unknown operation.</summary>
        public static long GetCallbackFailures(PresentationOperation operation)
        { int index = (int)operation; return index >= 0 && index < sites.Length ? sites[index].Events.CallbackFailures : 0; }
        private static Site[] CreateSites() => new[] {
            new Site(PresentationOperation.GuiChecks, typeof(FatControler), "NoesisGUIUpdateChecksInGame"),
            new Site(PresentationOperation.BuildingRollover, typeof(HUD_Main), "UpdateRollover"),
            new Site(PresentationOperation.RecruitmentEnter, typeof(MainViewModel), "ButtonEnterCreateTroop"),
            new Site(PresentationOperation.RecruitmentLeave, typeof(MainViewModel), "ButtonLeaveCreateTroop"),
            new Site(PresentationOperation.TroopPanelEnter, typeof(MainViewModel), "ButtonTroopPanelMouseEnter"),
            new Site(PresentationOperation.TroopPanelLeave, typeof(MainViewModel), "ButtonTroopPanelMouseLeave"),
            new Site(PresentationOperation.RechargeSiegeAmmo, typeof(MainViewModel), "ButtonUnitRechargeRock")
        };
        // Shared production path, also exercised without installing game hooks in contract tests.
        internal static void Execute(InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs> events,
            PresentationPreEventArgs args, Action<object> original)
        {
            var invocation = events.Begin(args);
            Exception error = null;
            try
            {
                if (!args.SkipOriginalFunction)
                {
                    if (args.Replacement != null) args.Replacement(args.Parameter);
                    else original(args.Parameter);
                }
            }
            catch (Exception ex) { error = ex; throw; }
            finally { invocation.Complete(state => new PresentationPostEventArgs(args, state, error)); }
        }
        private sealed class Site
        {
            internal readonly InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs> Events;
            private readonly PresentationOperation operation;
            private readonly Type type;
            private readonly string method;
            private readonly ManagedInterceptionHook<ViewCall> viewHook = new ManagedInterceptionHook<ViewCall>();
            private readonly ManagedInterceptionHook<HudCall> hudHook = new ManagedInterceptionHook<HudCall>();
            private readonly ManagedInterceptionHook<GuiCall> guiHook = new ManagedInterceptionHook<GuiCall>();
            internal Site(PresentationOperation operation, Type type, string method)
            { this.operation = operation; this.type = type; this.method = method;
                Events = new InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs>(Install); }
            private void Install()
            {
                if (type == typeof(FatControler))
                    guiHook.Install(type, method, typeof(void), Type.EmptyTypes, DispatchGui, method);
                else if (type == typeof(HUD_Main))
                    hudHook.Install(type, method, typeof(void), Type.EmptyTypes, DispatchHud, method);
                else viewHook.Install(type, method, typeof(void), new[] { typeof(object) }, DispatchView, method);
            }
            private void DispatchView(MainViewModel self, object parameter)
            { Dispatch(self, parameter, effective => viewHook.Original(self, effective)); }
            private void DispatchHud(HUD_Main self) { Dispatch(self, null, _ => hudHook.Original(self)); }
            private void DispatchGui(FatControler self) { Dispatch(self, null, _ => guiHook.Original(self)); }
            private void Dispatch(object instance, object parameter, Action<object> original)
            {
                Execute(Events, new PresentationPreEventArgs(operation, instance, parameter), original);
            }
        }
    }
}
