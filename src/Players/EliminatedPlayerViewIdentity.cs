using System;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using APIShared.PlayerControl;

namespace APIShared
{
    // One positive participant identity protects both the common save writer and
    // the entire raw Chore submission, including synchronous native delivery.
    // It is independent of the unfinished foreign-command/control backend.
    internal static class EliminatedPlayerViewIdentity
    {
        private delegate bool SendCall(byte[] payload);
        private static object engineGate;
        private static FieldInfo engineMonitor;
        private static ControlSaveIdentity identity;
        private static Hook sendHook;
        private static Func<byte[], bool> originalSend;
        private static long world, session, token;
        private static bool ready;
        private static ulong viewAddress;
        private static ManualLogSource logger;
        private static int failureQueued;
        private static long reportedFailureToken;
        private static string reportedFailure, scopeFailure;
        internal static object EngineGate => engineGate;
        internal static int ProtectedPlayerId => identity?.ProtectedHumanId ?? 0;
        internal static bool Faulted => identity?.Failure != null || Volatile.Read(ref scopeFailure) != null;

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            if (ready) return;
            logger = log;
            if (!string.Equals(hash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Eliminated-player views require the audited native image.");
            engineMonitor = typeof(EngineInterface).GetField("threadLock", BindingFlags.Static | BindingFlags.NonPublic);
            engineGate = engineMonitor?.GetValue(null);
            if (engineGate == null || engineMonitor.FieldType != typeof(object))
                throw new MissingFieldException("EngineInterface", "private static object threadLock");
            viewAddress = SHCDESE.GameGlobals.GameGlobalsManager.Instance.LocalPlayerIdVA;
            if (viewAddress != unchecked((ulong)context.ModuleHandle.ToInt64()) + 0x88E3D70UL ||
                0x88E3D74UL > (ulong)context.Memory.Length)
                throw new InvalidOperationException("Eliminated-player native view cell differs from the audited image.");
            identity = new ControlSaveIdentity(InEngineContext, IsCurrentWorld,
                PlayerPerspectiveAPI.GetRawNativeViewPlayerId, EngineInterface.SetEditorPlayer);

            // Install once in APIShared's startup window. Original spectators and
            // dormant callers retain the complete original writer/send paths.
            ControlSaveNative.Initialize(context, hash, identity, log);
            MethodInfo method = typeof(GameNetworkAPI).GetMethod("SendScriptExtenderChorePayload",
                BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(byte[]) }, null);
            if (method == null || method.ReturnType != typeof(bool))
                throw new MissingMethodException("GameNetworkAPI", "SendScriptExtenderChorePayload(byte[])");
            Networking.NativeChoreTransport.ValidateSubmissionMethod(method);
            Hook candidate = null;
            try
            {
                candidate = new Hook(method, (SendCall)SendProtected,
                    new HookConfig { ManualApply = true, ID = "APIShared.PlayerPerspective.ChoreIdentity" });
                originalSend = candidate.GenerateTrampoline<Func<byte[], bool>>();
                candidate.Apply();
                sendHook = candidate;
                ready = true;
            }
            catch
            {
                // Only this not-yet-published managed candidate may be undone.
                try { candidate?.Undo(); candidate?.Dispose(); } catch { }
                throw;
            }
            NativeApiLog.Info(log, "ELIMINATED_PLAYER_VIEW: save and Chore identity protection ready; native view=reference-rva 0x88E3D70; process lifetime.");
        }

        private static bool InEngineContext() => engineGate != null && Monitor.IsEntered(engineGate) &&
            ReferenceEquals(engineMonitor.GetValue(null), engineGate) &&
            SHCDESE.GameGlobals.GameGlobalsManager.Instance.LocalPlayerIdVA == viewAddress;
        private static bool IsCurrentWorld(long expected) => expected > 0 &&
            MissionLifecycleService.NativeWorld?.Sequence == expected;

        internal static bool TryBind(long expectedSession, int actual, out string reason)
        {
            reason = null;
            if (!ready || !ControlSaveNative.IsReady || !InEngineContext())
            { reason = "Save/send identity protection is unavailable."; return false; }
            if (identity.IsScopeActive)
            { reason = "A save or send identity scope is in progress."; return false; }
            long currentWorld = MissionLifecycleService.NativeWorld?.Sequence ?? 0;
            if (identity.ProtectedHumanId != 0)
                return !Faulted && world == currentWorld && session == expectedSession && identity.ProtectedHumanId == actual;
            if (!identity.TryBind(currentWorld, actual, out long acquired, out reason)) return false;
            world = currentWorld; session = expectedSession; token = acquired;
            Volatile.Write(ref scopeFailure, null);
            return true;
        }

        internal static long CurrentToken => Volatile.Read(ref token);
        internal static bool IsCurrentBinding(long expectedSession, int actual, long expectedToken) =>
            expectedToken != 0 && token == expectedToken && session == expectedSession && ProtectedPlayerId == actual &&
            IsCurrentWorld(world) && MissionLifecycleService.ActiveContext?.SessionId == expectedSession;

        // Caller holds the engine monitor. An ended world's cell is never restored
        // into its replacement; a live world's identity is released only after proof.
        internal static bool TryClear(out string reason)
        {
            reason = null;
            if (identity == null || identity.ProtectedHumanId == 0) return true;
            if (!InEngineContext()) { reason = "No serialized identity context."; return false; }
            return TryClearBoundView(EngineInterface.SetEditorPlayer, IsCurrentWorld, out reason);
        }

        // Native adapters can reenter lifecycle and replace the binding even under
        // the monitor. Keep the captured lease until release has been verified.
        internal static bool TryClearBoundView(Action<int> restoreView, Func<long, bool> currentWorld, out string reason)
        {
            reason = null;
            long expectedWorld = world, expectedSession = session, expectedToken = token;
            int expectedPlayer = identity.ProtectedHumanId;
            bool liveWorld = currentWorld(expectedWorld);
            if (world != expectedWorld || session != expectedSession || token != expectedToken ||
                identity.ProtectedHumanId != expectedPlayer)
            { reason = "The view binding changed before identity restoration."; return false; }
            if (!liveWorld)
            {
                if (!identity.RetireEndedWorld(expectedWorld)) { reason = "Could not retire the ended view world."; return false; }
            }
            else
            {
                if (identity.IsScopeActive) { reason = "A save or send identity scope is in progress."; return false; }
                restoreView(expectedPlayer);
                if (world != expectedWorld || session != expectedSession || token != expectedToken ||
                    identity.ProtectedHumanId != expectedPlayer || !currentWorld(expectedWorld))
                { reason = "The view binding changed during identity restoration."; return false; }
                if (!identity.TryRelease(expectedToken, out reason)) return false;
            }
            if (world != expectedWorld || session != expectedSession || token != expectedToken)
            { reason = "The retired view binding was replaced."; return false; }
            world = session = token = 0;
            Volatile.Write(ref scopeFailure, null);
            return true;
        }

        private static bool SendProtected(byte[] payload) => RunSubmission(payload, originalSend);

        // False has a transport meaning in SE: it permits a Steam fallback. A
        // rejected protection scope must throw instead, and an already queued
        // submission stays successful even when later view restoration fails.
        internal static bool RunSubmission(byte[] payload, Func<byte[], bool> submit)
        {
            bool sent = false;
            try
            {
                bool completed = TryRunWithOriginalIdentity(payload, submit, out sent);
                if (sent) return true;
                if (!completed) throw new InvalidOperationException("Protected Chore submission was rejected; no transport fallback is allowed.");
                return false;
            }
            catch when (sent) { return true; }
            finally { ObserveFailure(identity); }
        }

        internal static void ObserveFailure(ControlSaveIdentity guard, string operationFailure = null)
        {
            if (guard == null || !ReferenceEquals(guard, identity)) return;
            string failure = operationFailure ?? guard.Failure;
            if (failure == null) return;
            if (ProtectedPlayerId > 0) Volatile.Write(ref scopeFailure, failure);
            if ((Volatile.Read(ref reportedFailureToken) == token &&
                    string.Equals(Volatile.Read(ref reportedFailure), failure, StringComparison.Ordinal)) ||
                Interlocked.CompareExchange(ref failureQueued, 1, 0) != 0) return;
            long failedWorld = world, failedToken = token;
            Volatile.Write(ref reportedFailureToken, failedToken);
            Volatile.Write(ref reportedFailure, failure);
            if (!QueueFailureReport(failure, failedWorld, failedToken))
            {
                Volatile.Write(ref reportedFailureToken, 0);
                Volatile.Write(ref reportedFailure, null);
                Interlocked.Exchange(ref failureQueued, 0);
            }
        }

        // Keep closure creation out of the dormant and already-reported paths.
        private static bool QueueFailureReport(string failure, long failedWorld, long failedToken)
            => Internal.UnityMainThreadDispatch.TryEnqueue(() => {
                Interlocked.Exchange(ref failureQueued, 0);
                ControlSaveNative.TakeFailure();
                try { NativeApiLog.Error(logger, "ELIMINATED_PLAYER_VIEW_IDENTITY_FAILED: " + failure); }
                catch { } // A log listener cannot retain stale foreign report values.
                if (world == failedWorld && token == failedToken && IsCurrentWorld(failedWorld))
                    PlayerPerspectiveAPI.InvalidateProtectedView();
            });

        // Transport prerequisites must run inside the same scope as submission.
        // In particular the authenticated channel's raw-view == actual-ID check
        // remains intact. The guard supports nested submissions/writers.
        internal static bool TryRunWithOriginalIdentity<TState, TResult>(TState state,
            Func<TState, TResult> original, out TResult result)
        {
            var guard = identity;
            if (guard == null || guard.ProtectedHumanId == 0)
            { result = original(state); return true; }
            lock (engineGate)
                return guard.TryRunWriter(state, original, out result, out _);
        }
    }
}
