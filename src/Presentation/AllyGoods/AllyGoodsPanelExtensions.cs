using System;
using System.Collections.Generic;
using System.Reflection;
using APIShared.Events;
using APIShared.Internal;
using CrusaderDE;
using Noesis;
using SHCDESE.API;
using SHCDESE.NoesisUtil;

namespace APIShared.Presentation
{
    /// <summary>Shared, additive sections below Vanilla's ally goods-send dialog.</summary>
    /// <remarks>Register on the initialized Unity thread after Script Extender readiness. All factories,
    /// state reads and observers run synchronously on Unity outside registry locks. Registrations are
    /// process-lived; controls are rebuilt per host. Existing controls, bindings and commands are never
    /// replaced. Consumer commands own gameplay authorization and synchronization. No native hooks are installed.</remarks>
    public static partial class AllyGoodsPanelExtensions
    {
        private const string HostName = "APISharedAllyGoodsExtensionsHost";
        private static readonly AllyGoodsSectionRegistry registry = new AllyGoodsSectionRegistry();
        private static readonly HostBinding binding = new HostBinding();
        private static readonly Dictionary<AllyGoodsSectionEntry, Visual> visuals = new Dictionary<AllyGoodsSectionEntry, Visual>();
        private static readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
        private static Observer[] observers = Array.Empty<Observer>();
        private static SelectionReader reader;
        private static bool initialized, dirty = true, notifying, blocked;
        private static Grid host, layout;
        private static StackPanel sections;
        private static Border surface;
        private static FrameworkElement viewport;
        private static float baseHeight, appliedHeight;
        private static long generation;
        private static AllyGoodsPanelContext current;
        private static string initializationFailure;

        /// <summary>Registers an initially visible/enabled owner-local section. Factory results must be
        /// fresh unattached controls; their DataContexts and resources remain consumer-owned. Sections sort
        /// by ascending order, ordinal owner GUID, then ID. Invalid/duplicate registrations return false.
        /// Factory failures hide only that section until explicit refresh or host recreation.</summary>
        public static bool TryRegisterSection(string ownerGuid, string registrationId,
            Func<AllyGoodsSectionContext, FrameworkElement> factory, out AllyGoodsSectionRegistration registration,
            out string reason, int order = 0)
        {
            registration = null;
            if (!EnsureInitialized(out reason)) return false;
            if (!registry.TryRegister(ownerGuid, registrationId, factory, order, out var entry, out reason)) return false;
            registration = new AllyGoodsSectionRegistration(entry, Invalidate); dirty = true; return true;
        }

        /// <summary>Reads a fresh selection on Unity. False means thread/member initialization failed;
        /// a successful closed context has IsOpen=false. This neither changes Vanilla's selected quantity
        /// nor proves the player may issue a command. Observers receive changes on the next GUI publication.</summary>
        public static bool TryGetContext(out AllyGoodsPanelContext context)
        {
            context = null;
            if (!EnsureInitialized(out _)) return false;
            try { context = Capture(); return true; }
            catch (Exception error) { Report("context", error); return false; }
        }

        /// <summary>Registers a process-lifetime, owner-local context observer. Replays the current context
        /// once, then reports changed states after existing GUI checks, including closure/recreation. Ordered
        /// by order/ordinal owner/ID. Observer errors are isolated. No implicit dispatch, permissions or transfer
        /// acknowledgement. Reentrant state reads are allowed; recursive publications are suppressed.</summary>
        public static bool TrySubscribeContext(string ownerGuid, string registrationId,
            Action<AllyGoodsPanelContext> changed, out string reason, int order = 0)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || changed == null)
            { reason = "A nonempty owner GUID, observer ID and callback are required."; return false; }
            if (!EnsureInitialized(out reason)) return false;
            if (OrderedRegistrations.Contains(observers, Observer.Key, ownerGuid, registrationId))
            { reason = "Duplicate ally-goods observer: " + ownerGuid + "/" + registrationId; return false; }
            var observer = new Observer(ownerGuid, registrationId, order, changed);
            observers = OrderedRegistrations.WithAdded(observers, observer, Observer.Key);
            try { Invoke(observer, Capture()); }
            catch (Exception error) { Report("observer-replay", error); }
            reason = ""; return true;
        }

        internal static void RequireUnity()
        {
            if (!UnityMainThreadDispatch.IsMainThread)
                throw new InvalidOperationException("Ally goods UI access requires the initialized Unity thread.");
        }
        private static bool EnsureInitialized(out string reason)
        {
            if (!UnityMainThreadDispatch.IsMainThread)
            { reason = "Ally goods UI initialization requires the initialized Unity thread."; return false; }
            if (initialized) { reason = ""; return true; }
            if (initializationFailure != null) { reason = initializationFailure; return false; }
            try
            {
                reader = new SelectionReader();
                if (!PresentationEvents.TryRegister(PresentationOperation.GuiChecks, APISharedPlugin.PluginGuid,
                    "AllyGoodsPanelExtensions", null, OnGuiChecks, out reason))
                    throw new InvalidOperationException(reason);
                // Extender rebinding roots this receiver independently of any plugin GameObject.
                GameXAMLManagerAPI.Instance.RegisterBinding(HostName, binding);
                initialized = true; reason = ""; return true;
            }
            catch (Exception error)
            { initializationFailure = error.Message; reason = initializationFailure; Report("initialization", error); return false; }
        }
        private static void Invalidate() => dirty = true;
        private static AllyGoodsPanelContext Capture()
        {
            MainViewModel main = MainViewModel.viewModelLoaded ? MainViewModel.Instance : null;
            bool open = main != null && main.AlliesPanelVisible && main.Allies_GoodsViewVis;
            bool send = open && main.Allies_SendGoodsViewVis, request = open && main.Allies_RequestGoodsViewVis;
            int local = -1, viewed = -1, recipient = 0;
            SHCDESE.Interop.eGoods? good = null; int? amount = null;
            if (open && main.HUDAlliesPanel != null)
            {
                local = PlayerPerspectiveAPI.GetControlledPlayerId(); viewed = PlayerPerspectiveAPI.GetViewedPlayerId();
                reader.Read(main.HUDAlliesPanel, out recipient, out good, out amount);
            }
            if (current == null || !current.Same(open, send, request, local, viewed, recipient, good, amount, generation))
                current = new AllyGoodsPanelContext(open, send, request, local, viewed, recipient, good, amount, generation);
            return current;
        }
        private static AllyGoodsPanelContext lastPublished;
        private static void OnGuiChecks(PresentationPostEventArgs args)
        {
            if (!args.OriginalCompleted || !UnityMainThreadDispatch.IsMainThread) return;
            if (registry.Entries.Length == 0 && observers.Length == 0 && adornmentRegistry.Entries.Length == 0) return;
            try
            {
                var next = Capture();
                if (!ReferenceEquals(lastPublished, next) && !notifying)
                {
                    lastPublished = next; notifying = true;
                    // A subscription replay may already have delivered this exact state to a new observer.
                    try { foreach (var observer in observers) if (!ReferenceEquals(observer.Delivered, next)) Invoke(observer, next); }
                    finally { notifying = false; }
                }
                if (host == null)
                {
                    if (next.IsSendMode && registry.Entries.Length != 0)
                        Report("missing-host", new InvalidOperationException("Ally-goods XAML extension host is missing; the existing dialog is preserved."));
                    return;
                }
                UpdateAdornments(next);
                if (blocked) return;
                if (registry.Entries.Length == 0) return;
                if (!next.IsSendMode) { Collapse(); return; }
                if (!ResolveLayout()) return;
                if (dirty) Rebuild(next);
                Layout();
            }
            catch (Exception error) { Collapse(); Report("presentation", error); }
        }
        private static void Invoke(Observer observer, AllyGoodsPanelContext context)
        {
            observer.Delivered = context;
            try { observer.Callback(context); }
            catch (Exception error) { Report("observer:" + observer.Owner + "/" + observer.Id, error); }
        }
        private static bool ResolveLayout()
        {
            if (layout != null) return true;
            var main = MainViewModel.viewModelLoaded ? MainViewModel.Instance : null;
            var root = main?.HUDAlliesPanel?.FindName("LayoutRoot") as Grid;
            if (root == null || !ReferenceEquals(host.Parent, root) || float.IsNaN((float)root.Height) || root.Height <= 0)
            {
                blocked = true; Report("layout-contract", new InvalidOperationException(
                    "The ally-goods host is not attached to the expected fixed-height LayoutRoot; foreign layout is preserved."));
                return false;
            }
            layout = root; baseHeight = appliedHeight = (float)root.Height;
            sections = new StackPanel { Orientation = Orientation.Vertical };
            var scroll = new ScrollViewer { Content = sections, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            surface = new Border { Background = new SolidColorBrush(Color.FromArgb(255, 29, 23, 16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 242, 212, 138)), BorderThickness = new Thickness(1),
                Padding = new Thickness(10), Child = scroll };
            host.Children.Add(surface);
            host.Margin = new Thickness(0, baseHeight, 0, 0);
            viewport = root;
            while (viewport.Parent is FrameworkElement parent) viewport = parent;
            dirty = true; return true;
        }
        private static void Rebuild(AllyGoodsPanelContext context)
        {
            dirty = false;
            sections.Children.Clear();
            foreach (var entry in registry.Entries)
            {
                if (!entry.Visible) continue;
                if (!visuals.TryGetValue(entry, out var visual) || visual.Revision != entry.Revision)
                {
                    visual = new Visual(entry.Revision); visuals[entry] = visual;
                    if (TryCreate(entry, new AllyGoodsSectionContext(host, context), out var element))
                        visual.Container = new ContentControl { Content = element };
                }
                if (visual.Container == null) continue;
                if (visual.Container.Parent != null)
                { Report("section-parent:" + entry.Owner + "/" + entry.Id, new InvalidOperationException("Section control is already attached.")); continue; }
                visual.Container.IsEnabled = entry.Enabled;
                visual.Container.Margin = new Thickness(0, sections.Children.Count == 0 ? 0 : 8, 0, 0);
                sections.Children.Add(visual.Container);
            }
        }
        internal static bool TryCreate(AllyGoodsSectionEntry entry, AllyGoodsSectionContext context, out FrameworkElement element)
        {
            element = null;
            try
            {
                element = entry.Factory(context);
                if (element == null || element.Parent != null)
                    throw new InvalidOperationException("Section factories must return a fresh unattached FrameworkElement.");
                return true;
            }
            catch (Exception error) { element = null; Report("factory:" + entry.Owner + "/" + entry.Id, error); return false; }
        }
        private static void Layout()
        {
            if (sections.Children.Count == 0) { Collapse(); return; }
            if (Math.Abs((float)layout.Height - appliedHeight) > 0.1f)
            {
                host.Visibility = Visibility.Collapsed; blocked = true;
                Report("layout-owner", new InvalidOperationException("Another UI owner changed the root height; extension layout is suspended.")); return;
            }
            float available = 0;
            if (viewport.ActualHeight > 0)
            {
                var bounds = layout.TransformToAncestor(viewport).TransformBounds(new Rect(0, 0, 1, 1));
                available = AllyGoodsContextPolicy.AvailableHeight((float)viewport.ActualHeight,
                    (float)bounds.Y, (float)bounds.Height, baseHeight);
            }
            float desired = (float)sections.DesiredSize.Height + 22;
            float height = AllyGoodsContextPolicy.ExtensionHeight(desired <= 22 ? 128 : desired, available);
            if (height <= 0) { Collapse(); return; }
            host.Height = height; host.Visibility = Visibility.Visible;
            appliedHeight = baseHeight + height; layout.Height = appliedHeight;
        }
        private static void Collapse()
        {
            if (host != null) { host.Visibility = Visibility.Collapsed; host.Height = 0; }
            // Only restore our own value; a foreign writer's change is never overwritten.
            if (layout != null && Math.Abs((float)layout.Height - appliedHeight) < 0.1f)
            { layout.Height = baseHeight; appliedHeight = baseHeight; }
        }
        private static void BindHost(FrameworkElement element)
        {
            RequireUnity();
            if (ReferenceEquals(host, element)) return;
            Detach();
            host = element as Grid;
            if (host == null) { Report("host-type", new InvalidOperationException("Ally-goods extension host must be a Grid.")); return; }
            generation++; host.Unloaded += OnHostUnloaded; dirty = true; blocked = false;
        }
        private static void OnHostUnloaded(object sender, RoutedEventArgs args) => Detach();
        private static void Detach()
        {
            DetachAdornments();
            Collapse();
            if (host != null) host.Unloaded -= OnHostUnloaded;
            if (surface != null && host != null) host.Children.Remove(surface);
            host = null; layout = null; viewport = null; sections = null; surface = null;
            visuals.Clear(); dirty = true; blocked = false;
        }
        private static void Report(string key, Exception error)
        {
            if (!reported.Add(key)) return;
            NativeApiLog.Error(ApiSharedRuntime.ProcessInstance.Logger, "ALLY_GOODS_UI " + key + ": " + error);
        }
        /// <summary>Extender binding receiver. Consumers register sections instead of constructing this type.</summary>
        public sealed class HostBinding : INoesisElementBindingAware
        {
            void INoesisElementBindingAware.OnNoesisElementBound(FrameworkElement element) => BindHost(element);
        }
        private sealed class Visual
        {
            internal readonly int Revision; internal ContentControl Container;
            internal Visual(int revision) { Revision = revision; }
        }
        private sealed class Observer
        {
            internal readonly string Owner, Id; internal readonly int Order;
            internal readonly Action<AllyGoodsPanelContext> Callback;
            internal AllyGoodsPanelContext Delivered; // Last state this observer received (Unity thread only).
            internal Observer(string owner, string id, int order, Action<AllyGoodsPanelContext> callback)
            { Owner = owner; Id = id; Order = order; Callback = callback; }
            internal static readonly Func<Observer, RegistrationKey> Key = entry => new RegistrationKey(entry.Owner, entry.Id, entry.Order);
        }
        // These private fields are read-only views of the real Assembly-CSharp contract. The publicized assembly is not used.
        internal sealed class SelectionReader
        {
            private readonly FieldInfo selected = Field("selectedGoods", typeof(int)), amount = Field("selectedGoodsAmount", typeof(int)),
                allyIndex = Field("curr_ally", typeof(int)), allyCount = Field("num_allies", typeof(int)), allies = Field("ally", typeof(int[]));
            private static FieldInfo Field(string name, Type type)
            {
                var value = typeof(HUD_AlliesPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (value == null || !value.IsPrivate || value.FieldType != type)
                    throw new MissingFieldException("Real HUD_AlliesPanel selection contract differs: " + name);
                return value;
            }
            internal void Read(HUD_AlliesPanel panel, out int recipient, out SHCDESE.Interop.eGoods? good, out int? quantity)
            {
                recipient = AllyGoodsContextPolicy.Recipient((int)allyIndex.GetValue(panel), (int)allyCount.GetValue(panel), (int[])allies.GetValue(panel));
                good = AllyGoodsContextPolicy.Good((int)selected.GetValue(panel));
                quantity = AllyGoodsContextPolicy.Amount((int)amount.GetValue(panel));
            }
        }
    }
}
