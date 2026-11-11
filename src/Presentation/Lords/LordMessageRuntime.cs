using System;
using System.Reflection;
using System.Threading;
using APIShared.Events;
using APIShared.Internal;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;

namespace APIShared.Presentation
{
    internal static class LordMessageRuntime
    {
        private delegate void Render(GameMap self, short[] records, int count, EngineInterface.PlayState state, byte[] radar);
        private delegate void Add(OnScreenText self, global::Enums.eOnScreenText id, int a, int b, int c, int d, int e);
        private delegate void UpdateText(OnScreenText self, EngineInterface.PlayState state, bool allowExpire);
        private delegate void InitText(OnScreenText self);
        private static readonly ManagedInterceptionHook<Render> renderHook = new ManagedInterceptionHook<Render>();
        private static readonly ManagedInterceptionHook<Add> addHook = new ManagedInterceptionHook<Add>();
        private static readonly ManagedInterceptionHook<UpdateText> updateHook = new ManagedInterceptionHook<UpdateText>();
        private static readonly ManagedInterceptionHook<InitText> initHook = new ManagedInterceptionHook<InitText>();
        private static ILHook ilHook;
        private static FieldInfo slots;
        private static int attempted, confirmed;
        private static long nextInvocation;
        private static string status = "Pending";
        private static string protocolStatus = "Pending native message protocol validation.";
        private static ManualLogSource log;
        [ThreadStatic] private static LordMessageFrame frame;
        [ThreadStatic] private static int renderDepth;
        internal static readonly LordMessageTextBindings TextBindings = new LordMessageTextBindings();
        internal static string Status => Volatile.Read(ref status) == "Ready" ? Volatile.Read(ref protocolStatus) : Volatile.Read(ref status);
        // Table-address patterns alone cannot prove the complete record-production/control-flow contract.
        // Until another native identity receives that audit, keep only this broker hash-bound.
        internal static void InitializeNative(string nativeHash)
        {
            Volatile.Write(ref protocolStatus, string.Equals(nativeHash, NativeLordMessageCatalog.NativeHash, StringComparison.OrdinalIgnoreCase)
                ? "Ready" : "Unavailable: native Lord presentation protocol has not been audited for this game binary.");
            if (Status != "Ready") Error("LORD_MESSAGES_NATIVE_PROTOCOL: " + protocolStatus);
            else Info("LORD_MESSAGES_READY: audited native message protocol and permanent Unity presentation hooks ready.");
        }

        internal static void Initialize(ManualLogSource logger)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            log = logger;
            try
            {
                var method = typeof(GameMap).GetMethod("processTestMap", new[] { typeof(short[]), typeof(int), typeof(EngineInterface.PlayState), typeof(byte[]) });
                if (method == null || !method.IsPublic || method.ReturnType != typeof(void)) throw new MissingMethodException("Public GameMap.processTestMap contract changed.");
                LordMessageIlContract.Validate(method);
                slots = typeof(OnScreenText).GetField("OSTs", BindingFlags.Instance | BindingFlags.NonPublic);
                if (slots == null || slots.FieldType != typeof(OnScreenText.OST[]) || slots.IsStatic) throw new MissingFieldException("OST slot contract changed.");
                ValidatePublicMembers();
                LordMessageAssets.Initialize();
                ilHook = new ILHook(method, LordMessageIlContract.Apply, new ILHookConfig { ManualApply = true, ID = "APIShared.LordMessages" });
                ilHook.Apply(); // Publication roots precede all later optional initialization; never unpatch.
                renderHook.Install(typeof(GameMap), "processTestMap", typeof(void),
                    new[] { typeof(short[]), typeof(int), typeof(EngineInterface.PlayState), typeof(byte[]) }, RenderMessages, "LordMessageRender");
                addHook.Install(typeof(OnScreenText), "addOSTEntry", typeof(void),
                    new[] { typeof(global::Enums.eOnScreenText), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) }, AddText, "LordMessageTextBind");
                updateHook.Install(typeof(OnScreenText), "updateOST", typeof(void),
                    new[] { typeof(EngineInterface.PlayState), typeof(bool) }, Update, "LordMessageTextRender");
                initHook.Install(typeof(OnScreenText), "initOST", typeof(void), Type.EmptyTypes, Init, "LordMessageTextReset");
                if (!ApiShared.Current.TryGetMissionLifecycle(APISharedPlugin.PluginGuid, out var lifecycle, out var diagnostic) ||
                    !lifecycle.TryRegisterObserver("Presentation.LordMessages", Reset, Reset, Reset, out diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason ?? "Mission lifetime unavailable.");
                Volatile.Write(ref status, "Ready");
                Info("LORD_MESSAGES_MANAGED_READY: permanent Unity hooks; awaiting independent native protocol validation.");
            }
            catch (Exception error) { Volatile.Write(ref status, "Unavailable: " + error.Message); Error("Lord message presentation unavailable: " + error); }
        }
        private static void ValidatePublicMembers()
        {
            foreach (var spec in new[] {
                Tuple.Create(typeof(SFXManager), "playSpeech", new[] { typeof(int), typeof(string), typeof(float), typeof(bool), typeof(bool) }),
                Tuple.Create(typeof(SFXManager), "playBink", new[] { typeof(string), typeof(bool), typeof(bool) }),
                Tuple.Create(typeof(OnScreenText), "getOST", new[] { typeof(global::Enums.eOnScreenText), typeof(bool).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(bool) }) })
            {
                MethodInfo method = spec.Item1.GetMethod(spec.Item2, BindingFlags.Public | BindingFlags.Instance, null, spec.Item3, null);
                if (method == null) throw new MissingMethodException("Real game public member changed: " + spec.Item2);
            }
            var property = typeof(MainViewModel).GetProperty("OST_Message_Bar_Text", BindingFlags.Public | BindingFlags.Instance);
            if (property == null || property.PropertyType != typeof(string) || property.GetSetMethod() == null) throw new MissingMemberException("Public message text setter changed.");
        }
        private static void Reset(MissionLifecycleNotification notification) => TextBindings.Reset(notification.Context.SessionId);
        private static void Init(OnScreenText self) { TextBindings.Clear(); initHook.Original(self); }
        private static void AddText(OnScreenText self, global::Enums.eOnScreenText id, int a, int b, int c, int d, int e)
        {
            bool retiringText = id == global::Enums.eOnScreenText.OST_MESSAGE_BAR && TextBindings.HasBinding;
            if (id == global::Enums.eOnScreenText.OST_MESSAGE_BAR) TextBindings.Clear();
            addHook.Original(self, id, a, b, c, d, e);
            if (!retiringText) return;
            try {
                var array = (OnScreenText.OST[])slots.GetValue(self);
                int index = (int)global::Enums.eOnScreenText.OST_MESSAGE_BAR;
                // Vanilla does not flag MESSAGE_BAR selector changes while its reused slot stays active.
                // Retiring our own visible text must therefore request one normal refresh, even for Keep.
                if (index >= 0 && index < array.Length && array[index] != null) array[index].wasTurnedOnOrChanged = true;
            } catch (Exception error) { Error("Lord message original text refresh failed: " + error); }
        }
        private static void Update(OnScreenText self, EngineInterface.PlayState state, bool allowExpire)
        {
            updateHook.Original(self, state, allowExpire);
            if (!TextBindings.HasBinding) return;
            try {
                bool off = false, changed = false;
                var slot = self.getOST(global::Enums.eOnScreenText.OST_MESSAGE_BAR, ref off, ref changed, false);
                if (slot == null) { TextBindings.Clear(); return; }
                if (!TextBindings.TryGet(slot, out var text)) return;
                string value = text.Resolve();
                // Existing text update is the only clock; no frame/tick publisher or visibility extension.
                if (MainViewModel.Instance.OST_Message_Bar_Text != value) MainViewModel.Instance.OST_Message_Bar_Text = value;
            } catch (Exception error) { TextBindings.Clear(); Error("Lord message text replacement failed: " + error); }
        }
        private static void RenderMessages(GameMap self, short[] records, int count, EngineInterface.PlayState state, byte[] radar)
        {
            bool nested = renderDepth++ != 0 || frame != null;
            var outer = frame;
            // A recursive original and any further recursion must not consume the outer IL-site context.
            if (nested) frame = null;
            try {
                if (!nested && Status == "Ready" && LordMessageEvents.Registry.CanNotify) Begin(records, count, state, LordMessageEvents.Registry);
                renderHook.Original(self, records, count, state, radar);
            }
            finally { frame = outer; renderDepth--; }
        }
        internal static LordMessageFrame Begin(short[] records, int count, EngineInterface.PlayState state,
            InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs> registry)
        {
            int capacity = records == null ? 0 : records.Length / LordMessageGroups.Words;
            if (count < 0) { count = 0; while (count < capacity && records[count * LordMessageGroups.Words] >= 0) count++; }
            var current = new LordMessageFrame(frame, records, Math.Min(count, capacity), state,
                Interlocked.Increment(ref nextInvocation), registry);
            frame = current; return current;
        }
        internal static void End(LordMessageFrame current)
        { if (ReferenceEquals(frame, current)) frame = current.Previous; }

        internal static void Text(OnScreenText self, global::Enums.eOnScreenText id, int a, int b, int c, int d, int e, int index)
        {
            var current = frame;
            long session = TextBindings.Session;
            LordMessageMutation replacement = null;
            if (current != null && id == global::Enums.eOnScreenText.OST_MESSAGE_BAR)
            {
                try { replacement = current.Select(index); }
                catch (Exception error) { current.Abandon(); Error("Lord message selection failed; original retained: " + error); }
            }
            self.addOSTEntry(id, a, b, c, d, e); // Original call remains outside selection error handling.
            if (replacement == null || replacement.Text.Kind == LordMessageSourceKind.Original) return;
            try {
                var array = (OnScreenText.OST[])slots.GetValue(self);
                int slot = (int)global::Enums.eOnScreenText.OST_MESSAGE_BAR;
                if (slot >= 0 && slot < array.Length) TextBindings.Bind(array[slot], replacement.Text, session);
            } catch (Exception error) { Error("Lord message text binding failed: " + error); }
        }
        internal static void Speech(SFXManager self, int channel, string path, float volume, bool ignoreMuting, bool ignorePause, int index)
        {
            var current = frame;
            var selected = current?.AtAudio(index);
            self.playSpeech(channel, selected == null || selected.Audio.Kind == LordMessageSourceKind.Original ? path : selected.Audio.Path,
                volume, ignoreMuting, ignorePause);
        }
        internal static void Video(SFXManager self, string path, bool loop, bool waitForSpeech, int index)
        {
            var current = frame;
            var selected = current?.AtVideo(index);
            self.playBink(selected == null || selected.Video.Kind == LordMessageSourceKind.Original ? path : selected.Video.Path, loop, waitForSpeech);
            if (selected == null) return;
            current.Complete();
            if (Volatile.Read(ref confirmed) == 0 && Interlocked.Exchange(ref confirmed, 1) == 0)
                Info("LORD_MESSAGES_PRESENTATION_CONFIRMED: real grouped message dispatched after startup cleanup.");
        }
        private static void Info(string message) { try { NativeApiLog.Info(log, message); } catch { } }
        private static void Error(string message) { try { NativeApiLog.Error(log, message); } catch { } }
    }

    internal sealed class LordMessageFrame
    {
        internal readonly LordMessageFrame Previous;
        private readonly short[] records;
        private readonly int count;
        private readonly EngineInterface.PlayState state;
        private readonly long invocation;
        private readonly InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs> registry;
        private LordMessageGroup group;
        private InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs>.Invocation pending;
        internal LordMessageFrame(LordMessageFrame previous, short[] records, int count, EngineInterface.PlayState state, long invocation,
            InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs> registry)
        { Previous = previous; this.records = records; this.count = count; this.state = state; this.invocation = invocation; this.registry = registry; }
        internal LordMessageMutation Select(int index)
        {
            Abandon();
            if (!registry.CanNotify || !LordMessageGroups.TryMatch(records, count, index, out group)) return null;
            var context = new LordMessageContext(invocation, index, group.Section, group.TextIndex, group.Lord, group.Player, group.Type,
                state.speechFileName, state.binkFileName);
            pending = registry.Begin(new LordMessageMutation(context));
            return pending.Pre;
        }
        internal LordMessageMutation AtAudio(int index) => pending != null && group.Audio == index ? pending.Pre : null;
        internal LordMessageMutation AtVideo(int index) => pending != null && group.Video == index ? pending.Pre : null;
        internal void Complete()
        {
            var value = pending; pending = null;
            if (value != null && value.HasPostObservers) value.Complete(value.Pre, LordMessageEvents.PostFactory);
        }
        internal void Abandon() { pending = null; group = default; }
    }

    // Slot identity alone is insufficient: Vanilla reuses the same OST for every message.
    // Every Add/init/session boundary retires the immutable binding, including identical text selectors.
    internal sealed class LordMessageTextBindings
    {
        private sealed class Binding
        {
            internal readonly object Slot;
            internal readonly LordMessageText Text;
            internal readonly long Generation;
            internal Binding(object slot, LordMessageText text, long generation) { Slot = slot; Text = text; Generation = generation; }
        }
        private Binding binding;
        private long generation;
        private long session;
        private readonly object sync = new object();
        internal long Session => Interlocked.Read(ref session);
        internal bool HasBinding => Volatile.Read(ref binding) != null;
        internal void Reset(long sessionId)
        {
            lock (sync) {
                if (sessionId < session) return; // Delayed cleanup for an older session cannot retire a newer message.
                Interlocked.Exchange(ref session, sessionId);
                ClearCore();
            }
        }
        internal void Clear() { lock (sync) ClearCore(); }
        private void ClearCore() { Interlocked.Increment(ref generation); Volatile.Write(ref binding, null); }
        internal void Bind(object slot, LordMessageText text, long expectedSession = -1)
        {
            if (slot == null || text == null) return;
            lock (sync) {
                if (expectedSession >= 0 && expectedSession != session) return;
                Volatile.Write(ref binding, new Binding(slot, text, generation));
            }
        }
        internal bool TryGet(object slot, out LordMessageText text)
        {
            var value = Volatile.Read(ref binding);
            text = value != null && value.Generation == Interlocked.Read(ref generation) && ReferenceEquals(value.Slot, slot) ? value.Text : null;
            return text != null;
        }
    }
}
