using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using MonoMod.RuntimeDetour;
using SHCDESE.API.LowLevel;

namespace APIShared.Presentation
{
    /// <summary>One registration's camera decision before an unchanged mapper selection.</summary>
    public sealed class MapperSelectionCameraPreEventArgs
    {
        private bool mutable = true, suppress;
        private object state;
        internal MapperSelectionCameraPreEventArgs(int mapper, long session) { Mapper = mapper; SessionId = session; }
        /// <summary>Original EngineInterface.StartMapperItem integer mapper value; never rewritten.</summary>
        public int Mapper { get; }
        /// <summary>Current mission session captured before Pre; consumers must revalidate it in Post.</summary>
        public long SessionId { get; }
        /// <summary>Request suppression of this call's family-building camera centering only.
        /// Successful requests combine with OR; this cannot cancel selection or change its owner.</summary>
        public bool SuppressCamera { get => suppress; set { CheckMutable(); suppress = value; } }
        /// <summary>Private state passed only to this registration's Post, including when Pre throws.
        /// Assign cleanup state before making temporary changes. Writable only during Pre.</summary>
        public object State { get => state; set { CheckMutable(); state = value; } }
        private void CheckMutable() { if (!mutable) throw new InvalidOperationException("Mapper camera Pre has completed."); }
        internal void Freeze(bool failed) { if (failed) suppress = false; mutable = false; }
        internal MapperCameraBroker.Entry Entry;
        internal MapperSelectionCameraPreEventArgs Previous;
    }

    /// <summary>Immutable completion after restoring the invocation's native camera demand.</summary>
    public sealed class MapperSelectionCameraPostEventArgs
    {
        internal MapperSelectionCameraPostEventArgs(MapperSelectionCameraPreEventArgs pre, int result,
            Exception error, bool active)
        { Mapper = pre.Mapper; SessionId = pre.SessionId; State = pre.State; Result = result; Exception = error; SuppressionActive = active; }
        /// <summary>Unmodified mapper requested at entry.</summary>
        public int Mapper { get; }
        /// <summary>Session captured at entry, which may have ended during the original.</summary>
        public long SessionId { get; }
        /// <summary>Original result, or -1 if the original threw. A nonnegative result is not construction success.</summary>
        public int Result { get; }
        /// <summary>Original exception, rethrown unchanged after every Post. Callback exceptions are isolated.</summary>
        public Exception Exception { get; }
        /// <summary>The scoped camera suppression was armed for the original, not proof that a camera call occurred.
        /// Demand is already restored before Post; an enclosing invocation can retain its own scope.</summary>
        public bool SuppressionActive { get; }
        /// <summary>Only this registration's private Pre state, retained even when its Pre failed.</summary>
        public object State { get; }
    }

    /// <summary>Process-lifetime, camera-only observations of EngineInterface.StartMapperItem.</summary>
    /// <remarks>Register on Unity after APIShared's LibraryLoaded initialization. Ordered by order, ordinal
    /// owner GUID and ID; no replay or deregistration. Pre runs in order, Post in reverse order, outside registry
    /// locks but under the existing engine monitor. Post also runs on Pre/original errors, after native-demand
    /// cleanup. Original exceptions propagate once; observer failures are isolated and logged once per registration.
    /// Calls from observers bypass notifications and suppression. Recursive original calls have independent scopes.
    /// No cancellation or mapper/owner/result mutation is exposed. Only the audited mapper camera call is affected;
    /// other native camera callers, player identity, placement checks and simulation remain Vanilla. Consumers own
    /// mode permissions, stale-session checks and any immediate post-selection camera movement. Non-Unity calls
    /// and calls outside a current native world/mission bypass notifications and retain original behavior.</remarks>
    public static class MapperSelectionCameraEvents
    {
        private delegate int MapperCall(int mapper);
        private static readonly MapperCall dispatch = Dispatch;
        private static Hook hook;
        private static MapperCall original;
        private static Func<int, int> invokeOriginal;
        private static MapperCameraBroker broker;
        private static bool ready;
        private static Exception failure;

        /// <summary>Registers permanent Pre/Post callbacks. At least one callback and unique nonempty owner/ID
        /// are required. Returns false without registering when initialization, native validation or ownership fails.</summary>
        public static bool TryRegister(string ownerGuid, string id, Action<MapperSelectionCameraPreEventArgs> pre,
            Action<MapperSelectionCameraPostEventArgs> post, out string reason, int order = 0)
        {
            if (!Internal.UnityMainThreadDispatch.IsMainThread) { reason = "Register mapper camera observers on Unity."; return false; }
            if (!Volatile.Read(ref ready)) { reason = failure?.Message ?? "Mapper camera service is not ready."; return false; }
            return broker.TryRegister(ownerGuid, id, pre, post, out reason, order);
        }

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            if (ready) return;
            if (failure != null || hook != null) throw new InvalidOperationException("Mapper camera publication cannot be retried.", failure);
            Hook candidate = null; bool attempted = false;
            try
            {
                if (!NativeStartupPatchWindow.IsOpen || !Internal.UnityMainThreadDispatch.IsMainThread)
                    throw new InvalidOperationException("Mapper camera initialization requires the Unity startup patch window.");
                object engine = Internal.EngineMonitor.Current;
                if (engine == null) throw new MissingFieldException("EngineInterface", "private static object threadLock");
                var target = typeof(EngineInterface).GetMethod("StartMapperItem", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(int) }, null);
                if (target == null || target.ReturnType != typeof(int))
                    throw new MissingMethodException("Public EngineInterface.StartMapperItem(int) : int");
                MapperCameraNative.Initialize(context, hash, log);
                broker = new MapperCameraBroker(engine, IsMainThread, ReadSession, ReadWorld,
                    MapperCameraNative.ReadDemand, MapperCameraNative.WriteDemand,
                    (entry, error) => NativeApiLog.Error(log, "MAPPER_CAMERA_CALLBACK_FAILED: " + entry + ": " + error));
                if (!ApiShared.Current.TryGetMissionLifecycle(APISharedPlugin.PluginGuid, out var lifecycle, out var diagnostic) ||
                    !lifecycle.TryRegisterObserver("mapper-camera-demand", Reset, Reset,
                        notification => { if (notification.IsBeforeInitialization) Reset(notification); }, out diagnostic))
                    throw new InvalidOperationException("Mapper camera lifecycle unavailable: " + diagnostic?.Reason);
                candidate = new Hook(target, dispatch, new HookConfig { ManualApply = true, ID = "APIShared.MapperSelectionCamera" });
                original = candidate.GenerateTrampoline<MapperCall>();
                invokeOriginal = original.Invoke;
                hook = candidate; attempted = true;
                candidate.Apply();
                if (!candidate.IsApplied || !MapperCameraNative.IsReady)
                    throw new InvalidOperationException("Mapper camera publication incomplete.");
                Volatile.Write(ref ready, true);
                NativeApiLog.Info(log, "MAPPER_SELECTION_CAMERA_READY: permanent managed broker and scoped native camera suppression.");
            }
            catch (Exception error)
            {
                failure = error;
                if (!attempted) { candidate?.Dispose(); }
                throw;
            }
        }
        private static bool IsMainThread() => Internal.UnityMainThreadDispatch.IsMainThread;
        private static long ReadSession() => MissionLifecycleService.ActiveContext?.SessionId ?? 0;
        private static long ReadWorld() => MissionLifecycleService.NativeWorld?.Sequence ?? 0;
        private static void Reset(MissionLifecycleNotification notification) => broker?.Reset();
        private static int Dispatch(int mapper)
        {
            if (!Volatile.Read(ref ready)) return original(mapper);
            return broker.Invoke(mapper, invokeOriginal);
        }
    }

    // The production state/ordering/scoping path is exercised without game objects or installed hooks.
    internal sealed class MapperCameraBroker
    {
        internal sealed class Entry
        {
            internal string Owner, Id;
            internal int Order, Failures;
            internal Action<MapperSelectionCameraPreEventArgs> Pre;
            internal Action<MapperSelectionCameraPostEventArgs> Post;
            public override string ToString() => Owner + "/" + Id;
        }
        private readonly object engine, registryGate = new object();
        private readonly Func<bool> isMain;
        private readonly Func<long> session, world;
        private readonly Func<int> readDemand;
        private readonly Action<int> writeDemand;
        private readonly Action<Entry, Exception> report;
        private Entry[] entries = Array.Empty<Entry>();
        private long resetGeneration;
        [ThreadStatic] private static int notifying;
        internal MapperCameraBroker(object engine, Func<bool> isMain, Func<long> session, Func<long> world,
            Func<int> readDemand, Action<int> writeDemand, Action<Entry, Exception> report)
        { this.engine = engine; this.isMain = isMain; this.session = session; this.world = world;
            this.readDemand = readDemand; this.writeDemand = writeDemand; this.report = report; }

        internal bool TryRegister(string owner, string id, Action<MapperSelectionCameraPreEventArgs> pre,
            Action<MapperSelectionCameraPostEventArgs> post, out string reason, int order = 0)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || (pre == null && post == null))
            { reason = "Owner, ID and a mapper camera callback are required."; return false; }
            lock (registryGate)
            {
                foreach (var entry in entries) if (entry.Owner == owner && entry.Id == id)
                { reason = "Duplicate mapper camera registration."; return false; }
                var next = new List<Entry>(entries) { new Entry { Owner = owner, Id = id, Order = order, Pre = pre, Post = post } };
                next.Sort((a, b) => { int n = a.Order.CompareTo(b.Order); if (n == 0) n = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
                    return n != 0 ? n : StringComparer.Ordinal.Compare(a.Id, b.Id); });
                Volatile.Write(ref entries, next.ToArray());
                return true;
            }
        }
        internal void Reset() { lock (engine) { resetGeneration++; writeDemand(0); } }

        internal int Invoke(int mapper, Func<int, int> original)
        {
            var snapshot = Volatile.Read(ref entries);
            if (snapshot.Length == 0 || !isMain()) return original(mapper);
            lock (engine)
            {
                long currentWorld = world(), currentSession = session();
                if (notifying != 0 || currentWorld <= 0 || currentSession <= 0)
                    return RunOriginal(mapper, original, false, currentWorld, currentSession);
                return Observed(snapshot, mapper, currentSession, currentWorld, original);
            }
        }
        private int RunOriginal(int mapper, Func<int, int> original, bool suppress, long capturedWorld, long capturedSession)
        {
            long generation = resetGeneration;
            int previous = readDemand();
            writeDemand(suppress ? 1 : 0);
            try { return original(mapper); }
            finally { writeDemand(world() == capturedWorld && session() == capturedSession &&
                generation == resetGeneration ? previous : 0); }
        }
        private int Observed(Entry[] snapshot, int mapper, long currentSession, long currentWorld, Func<int, int> original)
        {
            MapperSelectionCameraPreEventArgs last = null;
            int result = -1; Exception error = null; bool suppress = false, active = false;
            long generation = resetGeneration;
            try
            {
                foreach (var entry in snapshot)
                {
                    var args = last = new MapperSelectionCameraPreEventArgs(mapper, currentSession) { Entry = entry, Previous = last };
                    bool failed = false;
                    notifying++;
                    try { entry.Pre?.Invoke(args); }
                    catch (Exception callbackError) { failed = true; Report(entry, callbackError); }
                    finally { notifying--; args.Freeze(failed); }
                    suppress |= args.SuppressCamera;
                }
                active = suppress && generation == resetGeneration && world() == currentWorld && session() == currentSession;
                result = RunOriginal(mapper, original, active, currentWorld, currentSession);
                return result;
            }
            catch (Exception originalError) { error = originalError; throw; }
            finally
            {
                while (last != null)
                {
                    var args = last; last = args.Previous; args.Previous = null;
                    notifying++;
                    try { args.Entry.Post?.Invoke(new MapperSelectionCameraPostEventArgs(args, result, error, active)); }
                    catch (Exception callbackError) { Report(args.Entry, callbackError); }
                    finally { notifying--; }
                }
            }
        }
        private void Report(Entry entry, Exception error)
        {
            if (Interlocked.Increment(ref entry.Failures) != 1) return;
            try { report?.Invoke(entry, error); } catch { }
        }
    }
}
