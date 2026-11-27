using System;
using System.Threading;
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
        RechargeSiegeAmmo,
        /// <summary>MainViewModel.ButtonNewTradeGoodsType(object); selects a good in the market UI.</summary>
        MarketSelectGoods = 7,
        /// <summary>MainViewModel.ButtonCycleTradeGoodsType(object); cycles the market UI's selected good.</summary>
        MarketCycleGoods = 8,
        /// <summary>MainViewModel.ButtonBuySell(object); submits the selected market UI action.</summary>
        MarketBuySell = 9,
        /// <summary>MainViewModel.ButtonNewTradeType(object); changes the market UI's trade view.</summary>
        MarketChangeTradeType = 10,
        /// <summary>MainViewModel.ButtonExtendedFeaturesFunction(object); shared extended-features button, not limited to the market.</summary>
        ExtendedFeaturesAction = 11
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
        /// <summary>UI receiver for recruitment, troop-panel hover, recharge and market actions; otherwise null.</summary>
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
        /// <summary>UI receiver for recruitment, troop-panel hover, recharge and market actions; otherwise null.</summary>
        public MainViewModel ViewModel => Instance as MainViewModel;
        /// <summary>Building rollover receiver; otherwise null.</summary>
        public HUD_Main BuildingHud => Instance as HUD_Main;
        /// <summary>GUI-check receiver; otherwise null.</summary>
        public FatControler Controller => Instance as FatControler;
        /// <summary>Final UI parameter passed to the original or selected replacement.</summary>
        public object Parameter { get; }
        /// <summary>True when a Pre observer or final input guard vetoed the complete managed operation.</summary>
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
        // Static factories with explicit context: observed calls create no closure or delegate.
        private static readonly Func<Completion, object, PresentationPostEventArgs> PostFactory =
            (completion, state) => new PresentationPostEventArgs(completion.Pre, state, completion.Error);
        private static readonly Action<Action<object>, object> InvokeAction = (original, parameter) => original(parameter);
        private static readonly Site[] sites = CreateSites();
        /// <summary>Registers during Unity startup for the process lifetime. Callbacks run synchronously on the HUD caller thread, outside locks, ordered by order then ordinal owner/ID. No replay or automatic dispatch. Pre may veto the whole operation; vetoes are sticky. Failed Pre changes roll back. Post also reports vetoes and original exceptions; it cannot cancel completed writes. Added registrations join the next invocation. Nested operations from notifications run their originals without recursive notification. Observe in Post when augmenting Vanilla UI, and leave other mods' controls untouched. Duplicate identities or unsupported signatures return false with a reason.</summary>
        public static bool TryRegister(PresentationOperation operation, string ownerGuid, string registrationId,
            Action<PresentationPreEventArgs> pre, Action<PresentationPostEventArgs> post,
            out string reason, int order = 0)
            => TryRegisterWhenActive(operation, ownerGuid, registrationId, pre, post, out reason, order, null);

        internal static bool TryRegisterWhenActive(PresentationOperation operation, string ownerGuid, string registrationId,
            Action<PresentationPreEventArgs> pre, Action<PresentationPostEventArgs> post,
            out string reason, int order, Func<bool> isActive)
        {
            int index = (int)operation;
            if (index < 0 || index >= sites.Length) { reason = "Unknown presentation operation."; return false; }
            return sites[index].Events.TryRegister(ownerGuid, registrationId, pre, post, out reason, order, isActive: isActive);
        }

        /// <summary>
        /// Registers a synchronous input guard on a MainViewModel operation during Unity startup, for the process lifetime.
        /// Return true to allow the input. Guards run in order, then by ordinal owner and ID.
        /// A rejection or exception before Pre skips the complete call. After Pre, guards check the final input
        /// before the original or replacement, and Post observes a late rejection as a veto.
        /// Calls from nested notifications still check the input before running the original.
        /// Guards must be side-effect-free and must not issue UI commands.
        /// </summary>
        public static bool TryRegisterViewInputGuard(PresentationOperation operation, string ownerGuid, string registrationId,
            Func<MainViewModel, object, bool> allows, out string reason, int order = 0)
        {
            int index = (int)operation;
            if (index < 0 || index >= sites.Length || sites[index].Guards == null)
            {
                reason = "The operation has no MainViewModel input.";
                return false;
            }
            return sites[index].Guards.TryRegister(ownerGuid, registrationId, allows, out reason, order);
        }

        /// <summary>Total isolated callback errors at this named site; zero for an unknown operation.</summary>
        public static long GetCallbackFailures(PresentationOperation operation)
        {
            int index = (int)operation;
            if (index < 0 || index >= sites.Length) return 0;
            return sites[index].Events.CallbackFailures + (sites[index].Guards?.Failures ?? 0);
        }
        private static Site[] CreateSites() => new[] {
            new Site(PresentationOperation.GuiChecks, typeof(FatControler), "NoesisGUIUpdateChecksInGame"),
            new Site(PresentationOperation.BuildingRollover, typeof(HUD_Main), "UpdateRollover"),
            new Site(PresentationOperation.RecruitmentEnter, typeof(MainViewModel), "ButtonEnterCreateTroop"),
            new Site(PresentationOperation.RecruitmentLeave, typeof(MainViewModel), "ButtonLeaveCreateTroop"),
            new Site(PresentationOperation.TroopPanelEnter, typeof(MainViewModel), "ButtonTroopPanelMouseEnter"),
            new Site(PresentationOperation.TroopPanelLeave, typeof(MainViewModel), "ButtonTroopPanelMouseLeave"),
            new Site(PresentationOperation.RechargeSiegeAmmo, typeof(MainViewModel), "ButtonUnitRechargeRock"),
            new Site(PresentationOperation.MarketSelectGoods, typeof(MainViewModel), "ButtonNewTradeGoodsType"),
            new Site(PresentationOperation.MarketCycleGoods, typeof(MainViewModel), "ButtonCycleTradeGoodsType"),
            new Site(PresentationOperation.MarketBuySell, typeof(MainViewModel), "ButtonBuySell"),
            new Site(PresentationOperation.MarketChangeTradeType, typeof(MainViewModel), "ButtonNewTradeType"),
            new Site(PresentationOperation.ExtendedFeaturesAction, typeof(MainViewModel), "ButtonExtendedFeaturesFunction")
        };
        // Contract-test entry; production sites use the receiver overload below.
        internal static void Execute(InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs> events,
            PresentationPreEventArgs args, Action<object> original) => Execute(events, args, original, InvokeAction);
        // Shared production path. Sites pass the receiver and a delegate created once per site,
        // so an observed call allocates no closure for the original or for Post.
        internal static void Execute<TReceiver>(InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs> events,
            PresentationPreEventArgs args, TReceiver receiver, Action<TReceiver, object> original)
        {
            if (!events.CanNotify) { original(receiver, args.Parameter); return; }
            ExecuteObserved(events, args, receiver, original);
        }
        internal static void ExecuteView(InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs> events,
            ViewInputGuards guards, PresentationOperation operation, MainViewModel receiver, object parameter,
            Action<MainViewModel, object> original)
        {
            var snapshot = guards.Snapshot;
            if (!ViewInputGuards.Allows(snapshot, receiver, parameter)) return;
            if (!events.CanNotify) { original(receiver, parameter); return; }
            ExecuteObservedView(events, operation, receiver, parameter, original, snapshot);
        }

        private static void ExecuteObservedView(InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs> events,
            PresentationOperation operation, MainViewModel receiver, object parameter, Action<MainViewModel, object> original,
            ViewInputGuards.Entry[] guards) =>
            ExecuteObserved(events, new PresentationPreEventArgs(operation, receiver, parameter), receiver, original, guards);

        private static void ExecuteObserved<TReceiver>(InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs> events,
            PresentationPreEventArgs args, TReceiver receiver, Action<TReceiver, object> original,
            ViewInputGuards.Entry[] guards = null)
        {
            var invocation = events.Begin(args);
            if (!args.SkipOriginalFunction && guards != null && !ViewInputGuards.Allows(guards, args.ViewModel, args.Parameter))
                args.RejectAfterPre();
            Exception error = null;
            try
            {
                if (!args.SkipOriginalFunction)
                {
                    if (args.Replacement != null) args.Replacement(args.Parameter);
                    else original(receiver, args.Parameter);
                }
            }
            catch (Exception ex) { error = ex; throw; }
            finally
            {
                if (invocation.HasPostObservers)
                    invocation.Complete(new Completion(args, error), PostFactory);
            }
        }
        private readonly struct Completion
        {
            internal readonly PresentationPreEventArgs Pre;
            internal readonly Exception Error;
            internal Completion(PresentationPreEventArgs pre, Exception error) { Pre = pre; Error = error; }
        }
        private sealed class Site
        {
            internal readonly InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs> Events;
            internal readonly ViewInputGuards Guards;
            private readonly PresentationOperation operation;
            private readonly Type type;
            private readonly string method;
            private readonly object installLock = new object();
            private bool installed;
            private readonly ManagedInterceptionHook<ViewCall> viewHook = new ManagedInterceptionHook<ViewCall>();
            private readonly ManagedInterceptionHook<HudCall> hudHook = new ManagedInterceptionHook<HudCall>();
            private readonly ManagedInterceptionHook<GuiCall> guiHook = new ManagedInterceptionHook<GuiCall>();
            // Created once per site; observed dispatch passes the receiver instead of capturing it.
            private readonly Action<MainViewModel, object> viewOriginal;
            private readonly Action<HUD_Main, object> hudOriginal;
            private readonly Action<FatControler, object> guiOriginal;
            internal Site(PresentationOperation operation, Type type, string method)
            { this.operation = operation; this.type = type; this.method = method;
                Events = new InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs>(Install);
                if (type == typeof(MainViewModel)) Guards = new ViewInputGuards(Install);
                viewOriginal = (self, parameter) => viewHook.Original(self, parameter);
                hudOriginal = (self, _) => hudHook.Original(self);
                guiOriginal = (self, _) => guiHook.Original(self); }
            private void Install()
            {
                lock (installLock)
                {
                    if (installed) return;
                    if (type == typeof(FatControler))
                        guiHook.Install(type, method, typeof(void), Type.EmptyTypes, DispatchGui, method);
                    else if (type == typeof(HUD_Main))
                        hudHook.Install(type, method, typeof(void), Type.EmptyTypes, DispatchHud, method);
                    else viewHook.Install(type, method, typeof(void), new[] { typeof(object) }, DispatchView, method);
                    installed = true;
                }
            }
            private void DispatchView(MainViewModel self, object parameter) =>
                ExecuteView(Events, Guards, operation, self, parameter, viewOriginal);
            private void DispatchHud(HUD_Main self)
            {
                if (!Events.CanNotify) { hudHook.Original(self); return; }
                DispatchObservedHud(self);
            }
            private void DispatchObservedHud(HUD_Main self) =>
                Execute(Events, new PresentationPreEventArgs(operation, self, null), self, hudOriginal);
            private void DispatchGui(FatControler self)
            {
                if (!Events.CanNotify) { guiHook.Original(self); return; }
                DispatchObservedGui(self);
            }
            private void DispatchObservedGui(FatControler self) =>
                Execute(Events, new PresentationPreEventArgs(operation, self, null), self, guiOriginal);
        }

        internal sealed class ViewInputGuards
        {
            [ThreadStatic] private static bool checking;
            private readonly Action install;
            private readonly object sync = new object();
            private Entry[] entries = Array.Empty<Entry>();
            internal ViewInputGuards(Action install = null) { this.install = install; }
            internal Entry[] Snapshot => Volatile.Read(ref entries);
            internal long Failures
            {
                get
                {
                    long total = 0;
                    foreach (Entry entry in Snapshot) total += Interlocked.Read(ref entry.Failures);
                    return total;
                }
            }

            internal bool TryRegister(string owner, string id, Func<MainViewModel, object, bool> allows,
                out string reason, int order = 0)
            {
                if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || allows == null)
                {
                    reason = "Owner GUID, registration ID and an input guard are required.";
                    return false;
                }
                lock (sync)
                {
                    if (OrderedRegistrations.Contains(entries, Entry.KeyOf, owner, id))
                    {
                        reason = "This owner already registered that input guard ID.";
                        return false;
                    }
                    try { install?.Invoke(); }
                    catch (Exception error) { reason = "Interception unavailable: " + error.Message; return false; }
                    Volatile.Write(ref entries, OrderedRegistrations.WithAdded(entries,
                        new Entry(new RegistrationKey(owner, id, order), allows), Entry.KeyOf));
                }
                reason = string.Empty;
                return true;
            }

            internal static bool Allows(Entry[] snapshot, MainViewModel receiver, object parameter)
            {
                if (snapshot.Length == 0) return true;
                if (checking) return false;
                checking = true;
                try
                {
                    foreach (Entry entry in snapshot)
                    {
                        try { if (!entry.Allows(receiver, parameter)) return false; }
                        catch (Exception error)
                        {
                            if (FailureThrottle.ShouldReport(ref entry.Failures))
                            {
                                try { InterceptionDiagnostics.Report(entry.Key.Owner + "/" + entry.Key.Id + " InputGuard", error); }
                                catch { }
                            }
                            return false;
                        }
                    }
                    return true;
                }
                finally { checking = false; }
            }

            internal sealed class Entry
            {
                internal static readonly Func<Entry, RegistrationKey> KeyOf = entry => entry.Key;
                internal readonly RegistrationKey Key;
                internal readonly Func<MainViewModel, object, bool> Allows;
                internal long Failures;
                internal Entry(RegistrationKey key, Func<MainViewModel, object, bool> allows)
                { Key = key; Allows = allows; }
            }
        }
    }
}
