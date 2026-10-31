using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using APIShared.NativeSites;
using SHCDESE.API.LowLevel;

namespace APIShared.Presentation
{
    /// <summary>Immutable visible render bounds plus one registration's private invocation state. No UI access is implied.</summary>
    public sealed class VisibleMapRenderEventArgs : EventArgs
    {
        internal VisibleMapRenderEventArgs(IntPtr map, int left, int right, int top, int bottom, int zoom)
        { Map = map; Left = left; Right = right; Top = top; Bottom = bottom; Zoom = zoom; }
        /// <summary>Borrowed native map pointer; valid only during this synchronous invocation.</summary>
        internal IntPtr Map { get; }
        /// <summary>Original left screen bound.</summary>
        public int Left { get; }
        /// <summary>Original right screen bound.</summary>
        public int Right { get; }
        /// <summary>Original top screen bound.</summary>
        public int Top { get; }
        /// <summary>Original bottom screen bound.</summary>
        public int Bottom { get; }
        /// <summary>Unmodified native zoom argument.</summary>
        public int Zoom { get; }
        /// <summary>Private state shared only by this registration's Before and Finally callbacks.
        /// Set rollback state before making temporary changes; Finally also runs when Before throws.</summary>
        public object State { get; set; }
    }

    /// <summary>Process-lived render observer. Initially disabled; activation changes data, never executable patches.</summary>
    public sealed class VisibleMapRenderRegistration
    {
        internal readonly string Owner, Id;
        internal readonly int Order;
        internal readonly Action<VisibleMapRenderEventArgs> Before, Finally;
        internal bool Enabled;
        internal int Failures;
        internal VisibleMapRenderRegistration(string owner, string id, int order,
            Action<VisibleMapRenderEventArgs> before, Action<VisibleMapRenderEventArgs> finallyCallback)
        { Owner = owner; Id = id; Order = order; Before = before; Finally = finallyCallback; }
        /// <summary>Thread-safe activation. An already captured invocation still receives its matching Finally.</summary>
        public void SetEnabled(bool enabled) => VisibleMapRenderEvents.SetEnabled(this, enabled);
    }

    /// <summary>Single owner of the visible-map render entry. Register during LibraryLoaded, after APIShared.
    /// Callbacks execute synchronously on the native caller thread, outside registration locks, in order/owner/ID order;
    /// Finally runs in reverse order, including after callback or original failures. The original is never vetoed,
    /// replaced, or repeated. Registrations and executable hooks live until process exit. No replay or UI dispatch.</summary>
    public static class VisibleMapRenderEvents
    {
        private const string AuditedHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const int Rva = 0x41D60;
        internal static readonly byte[] Entry = { 0x48,0x89,0x5C,0x24,0x10,0x48,0x89,0x6C,0x24,0x18 };
        private static readonly object gate = new object();
        private static VisibleMapRenderRegistration[] registrations = Array.Empty<VisibleMapRenderRegistration>();
        private static readonly IntPtr demand = AllocateDemand();
        private static IntPtr module;
        private static string imageHash;
        private static ManualLogSource log;
        private static NativeDetour<RenderCall> hook;
        private static readonly RenderCall callback = Render;
        private static bool publicationStarted, ready;
        private static Exception installationFailure;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void RenderCall(IntPtr map, int left, int right, int top, int bottom, int zoom);
        private static IntPtr AllocateDemand() { var p = Marshal.AllocHGlobal(1); Marshal.WriteByte(p, 0); return p; }
        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        { module = context.ModuleHandle; imageHash = hash; log = logger; }

        /// <summary>Registers an initially disabled observer, validating the native image and exclusive entry first.
        /// Returns false with a diagnostic for missing initialization, conflicts, duplicate identity or unsupported image.</summary>
        public static bool TryRegister(string ownerGuid, string id, Action<VisibleMapRenderEventArgs> before,
            Action<VisibleMapRenderEventArgs> finallyCallback, out VisibleMapRenderRegistration registration,
            out string reason, int order = 0)
        {
            registration = null; reason = null;
            lock (gate)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(id) || (before == null && finallyCallback == null))
                        throw new ArgumentException("Owner, registration ID and a callback are required.");
                    foreach (var existing in registrations)
                        if (existing.Owner == ownerGuid && existing.Id == id) throw new ArgumentException("Duplicate render registration.");
                    EnsureInstalled();
                    registration = new VisibleMapRenderRegistration(ownerGuid, id, order, before, finallyCallback);
                    var next = new List<VisibleMapRenderRegistration>(registrations) { registration };
                    next.Sort((a,b) => { int n = a.Order.CompareTo(b.Order); if (n == 0) n = StringComparer.Ordinal.Compare(a.Owner,b.Owner); return n != 0 ? n : StringComparer.Ordinal.Compare(a.Id,b.Id); });
                    Volatile.Write(ref registrations, next.ToArray());
                    return true;
                }
                catch (Exception error) { reason = error.Message; return false; }
            }
        }
        internal static void SetEnabled(VisibleMapRenderRegistration registration, bool enabled)
        {
            if (Volatile.Read(ref registration.Enabled) == enabled) return;
            lock (gate)
            {
                if (registration.Enabled == enabled) return;
                registration.Enabled = enabled;
                bool any = false;
                foreach (var item in registrations) any |= item.Enabled;
                Marshal.WriteByte(demand, ready && any ? (byte)1 : (byte)0);
            }
        }
        private static void EnsureInstalled()
        {
            if (installationFailure != null) throw new InvalidOperationException("Render hook unavailable.", installationFailure);
            if (ready) return;
            if (module == IntPtr.Zero || !string.Equals(imageHash, AuditedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Visible-map events require the audited native image and LibraryLoaded initialization.");
            var target = module + Rva;
            NativeIndirectEntry.VerifyBytes(target, Entry);
            var claim = NativeSiteRegistry.Register(APISharedPlugin.PluginGuid, "visible-map-render", Rva, Entry.Length, NativeSiteMode.Exclusive);
            if (!claim.Succeeded) throw new InvalidOperationException("Visible-map render entry is occupied: " + claim.Status);
            try
            {
                var bridge = new VisibleMapRenderBridge(callback, demand);
                var request = new DetourRequest<RenderCall> { Name = "APIShared visible map", TargetAddress = unchecked((ulong)target.ToInt64()), Callback = callback, IntermediaryFactory = bridge };
                hook = new NativeDetourBackend(new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false, FollowCalls = false, SuspendThreadsDuringPatch = NativeStartupPatchWindow.SuspendThreads }).CreateDetour(in request) as NativeDetour<RenderCall>;
                if (hook == null || hook.IsInstalled || hook.DisplacedByteCount != Entry.Length || hook.Scheme != DetourScheme.Indirect)
                    throw new InvalidOperationException("Visible-map Indirect/10 contract changed.");
                // All callback roots and the original trampoline exist before publication. A failed
                // publication remains rooted with native demand zero and must never be undone.
                publicationStarted = true;
                hook.Enable();
                NativeIndirectEntry.Validate(hook, target, Entry);
                bridge.Validate(hook);
                ready = true;
                try { log?.LogInfo("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] Shared visible-map render installed: RVA=0x41D60 span=10 resume=0x41D6A; native demand gate."); } catch { }
            }
            catch (Exception error)
            {
                installationFailure = error;
                if (!publicationStarted) { hook?.Dispose(); hook = null; NativeSiteRegistry.Unregister(APISharedPlugin.PluginGuid, "visible-map-render"); }
                throw;
            }
        }
        private static void Render(IntPtr map, int left, int right, int top, int bottom, int zoom)
            => Dispatch(Volatile.Read(ref registrations), map, left, right, top, bottom, zoom, hook.Original);

        internal static void Dispatch(VisibleMapRenderRegistration[] snapshot, IntPtr map, int left, int right,
            int top, int bottom, int zoom, RenderCall original)
        {
            var calls = new VisibleMapRenderEventArgs[snapshot.Length];
            try
            {
                for (int i = 0; i < snapshot.Length; i++)
                {
                    if (!Volatile.Read(ref snapshot[i].Enabled)) continue;
                    var args = calls[i] = new VisibleMapRenderEventArgs(map, left, right, top, bottom, zoom);
                    try { snapshot[i].Before?.Invoke(args); } catch (Exception error) { Report(snapshot[i], error); }
                }
                original(map, left, right, top, bottom, zoom);
            }
            finally
            {
                for (int i = snapshot.Length - 1; i >= 0; i--)
                    if (calls[i] != null)
                        try { snapshot[i].Finally?.Invoke(calls[i]); } catch (Exception error) { Report(snapshot[i], error); }
            }
        }
        private static void Report(VisibleMapRenderRegistration registration, Exception error)
        {
            if (Interlocked.Increment(ref registration.Failures) != 1) return;
            try { log?.LogError("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] Render callback failed: " + registration.Owner + "/" + registration.Id + ": " + error); } catch { }
        }
    }
}
