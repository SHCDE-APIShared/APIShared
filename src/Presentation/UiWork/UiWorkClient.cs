using System;
using System.Diagnostics;
using BepInEx.Logging;

namespace APIShared.Presentation
{
    /// <summary>Owner-bound deferred Unity work and coalesced presentation refreshes. Obtain through ModApiClient.Ui.</summary>
    public sealed class UiWorkClient
    {
        private readonly string owner;
        internal UiWorkClient(string owner) { this.owner = owner; }
        /// <summary>Always defers to the existing validated Unity context, including from Unity. FIFO enqueue order; nested posts may run in the same drain. False means unavailable; acceptance does not survive context failure or shutdown. No replay, gameplay dispatch or next-frame/render-phase guarantee.</summary>
        public bool TryPost(Action callback)
        {
            if (callback == null || !Internal.UnityMainThreadDispatch.IsInitialized) return false;
            return PostActive(callback);
        }
        private bool PostActive(Action callback)
        {
            return Internal.UnityMainThreadDispatch.TryEnqueue(() => {
                try { callback(); }
                catch (Exception error) { UiWorkRuntime.Report(owner + "/posted", error); }
            });
        }
        /// <summary>Registers a process-lived callback, initially dormant, without capturing a particular map's HUD. Thread-safe after managed initialization. Duplicate owner-local IDs fail. Calls run on Unity outside registry locks, ordered by order/ordinal owner/ID, at most once per rendered frame including pause. There is no ordering promise relative to other Unity subscribers or Vanilla GUI writes; use GuiChecks for post-Vanilla presentation. Requests/toggles during a captured pass affect later frames. Callback failures are isolated and diagnosed; consumers retain session/HUD guards and input/MP authority.</summary>
        public bool TryRegisterBeforeRender(string registrationId, Action callback,
            out IUiFrameWorkRegistration registration, out string reason, int order = 0)
            => UiWorkRuntime.TryRegister(owner, registrationId, callback, order, out registration, out reason);
    }

    // One static publisher survives the game's startup destruction of early plugin components.
    // Initialization is independent of native services and never lazily touches Unity on a worker.
    internal static class UiWorkRuntime
    {
        private static readonly object initialization = new object();
        private static UiWorkScheduler scheduler;
        private static ManualLogSource log;
        internal static void Initialize(ManualLogSource logger)
        {
            lock (initialization)
            {
                if (scheduler != null) return;
                if (!Internal.UnityMainThreadDispatch.IsMainThread)
                    throw new InvalidOperationException("UI work must initialize on the validated Unity thread.");
                var candidate = new UiWorkScheduler(Stopwatch.GetTimestamp, Stopwatch.Frequency, Report);
                var pump = new UiWorkFramePump(candidate, () => Internal.UnityMainThreadDispatch.IsMainThread,
                    () => UnityEngine.Time.frameCount);
                log = logger;
                pump.Attach(callback => UnityEngine.Application.onBeforeRender += callback.Invoke);
                System.Threading.Volatile.Write(ref scheduler, candidate);
            }
        }
        internal static bool TryRegister(string owner, string id, Action callback, int order,
            out IUiFrameWorkRegistration registration, out string reason)
        {
            var current = System.Threading.Volatile.Read(ref scheduler);
            if (current == null) { registration = null; reason = "Shared Unity frame work is not initialized."; return false; }
            return current.TryRegister(owner, id, callback, order, out registration, out reason);
        }
        internal static void Report(string identity, Exception error)
        { try { NativeApiLog.Error(log, "UI work callback failed: " + identity + ": " + error); } catch { } }
    }
}
