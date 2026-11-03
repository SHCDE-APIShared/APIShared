using System;
using System.Collections.Generic;
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
    // Internal role endpoint. The shared core treats owner identity as opaque;
    // permissions and reactions to a failed lease belong to its owning adapter.
    internal sealed class PlayerViewIdentityOwner
    {
        internal readonly string Name;
        private readonly Action<PlayerViewIdentity.FailureRecord> onFailure;
        internal PlayerViewIdentityOwner(string name, Action<PlayerViewIdentity.FailureRecord> onFailure)
        { Name = name; this.onFailure = onFailure; }
        internal void Notify(PlayerViewIdentity.FailureRecord failure) => onFailure?.Invoke(failure);
    }

    // One process root supplies the exact same core to the native save writer, the raw
    // Chore sender and the perspective facade.
    internal sealed class PlayerViewIdentityRuntime
    {
        internal static readonly PlayerViewIdentityRuntime Instance = new PlayerViewIdentityRuntime();
        private delegate bool SendCall(byte[] payload);
        private object engineGate;
        private FieldInfo engineMonitor;
        private volatile PlayerViewIdentity identity;
        private Hook sendHook;
        private Func<byte[], bool> originalSend;
        private bool ready;
        private ulong viewAddress;
        private ManualLogSource logger;
        private readonly Func<Action, bool> enqueue;
        private readonly Action<string> writeError;
        private readonly object failureGate = new object();
        private PlayerViewIdentity.Lease reportedLease;
        private readonly HashSet<string> reportedFailures = new HashSet<string>(StringComparer.Ordinal);

        private PlayerViewIdentityRuntime()
        { enqueue = Internal.UnityMainThreadDispatch.TryEnqueue; writeError = LogError; }
        // Fixtures use the production adapters with a single shared core and no
        // game hooks; they never replace the process root or its private fields.
        internal PlayerViewIdentityRuntime(PlayerViewIdentity identity, object engineGate,
            Func<Action, bool> enqueue, Action<string> writeError)
        {
            this.identity = identity;
            this.engineGate = engineGate ?? throw new ArgumentNullException(nameof(engineGate));
            this.enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
            this.writeError = writeError ?? throw new ArgumentNullException(nameof(writeError));
        }
        internal object EngineGate => engineGate;
        internal PlayerViewIdentity Identity => identity;
        internal int ProtectedPlayerId => identity?.ProtectedHumanId ?? 0;
        internal bool ForeignViewsReady => ready && PlayerViewSaveScope.IsBoundTo(identity);

        internal void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            if (ready) return;
            logger = log;
            if (engineGate == null)
            {
                engineMonitor = Internal.EngineMonitor.Field;
                engineGate = engineMonitor?.GetValue(null);
            }
            if (engineGate == null || engineMonitor == null)
                throw new MissingFieldException("EngineInterface", "private static object threadLock");
            // Original spectators and own views need only the shared monitor;
            // unsupported optional native protection must not remove that path.
            if (!string.Equals(hash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Participant views require the audited native image.");
            viewAddress = SHCDESE.GameGlobals.GameGlobalsManager.Instance.LocalPlayerIdVA;
            if (viewAddress != unchecked((ulong)context.ModuleHandle.ToInt64()) + (ulong)NativeViewCell.Rva ||
                (ulong)NativeViewCell.Rva + 4 > (ulong)context.Memory.Length)
                throw new InvalidOperationException("Participant native view cell differs from the audited image.");
            // Never replace a core after a partially successful startup: the
            // published native writer may already hold this instance forever.
            if (identity == null) identity = new PlayerViewIdentity(InEngineContext, IsCurrentWorld,
                PlayerPerspectiveAPI.GetRawNativeViewPlayerId, EngineInterface.SetEditorPlayer);
            PlayerViewSaveScope.Initialize(context, hash, identity, log);
            MethodInfo method = Networking.NativeChoreTransport.ResolveSubmissionMethod();
            Hook candidate = null;
            try
            {
                candidate = new Hook(method, (SendCall)SendProtected,
                    new HookConfig { ManualApply = true, ID = "APIShared.PlayerPerspective.ChoreIdentity" });
                originalSend = candidate.GenerateTrampoline<Func<byte[], bool>>();
                candidate.Apply(); sendHook = candidate; ready = true;
            }
            catch
            {
                try { candidate?.Undo(); candidate?.Dispose(); } catch { } // Unpublished candidate only.
                throw;
            }
            NativeApiLog.Info(log, "PLAYER_VIEW_IDENTITY: one core for Save, Chore and local identity; native view=reference-rva 0x88E3D70; process lifetime.");
        }
        private bool InEngineContext() => engineGate != null && Monitor.IsEntered(engineGate) &&
            ReferenceEquals(engineMonitor.GetValue(null), engineGate) &&
            SHCDESE.GameGlobals.GameGlobalsManager.Instance.LocalPlayerIdVA == viewAddress;
        private static bool IsCurrentWorld(long expected) => expected > 0 && MissionLifecycleService.NativeWorld?.Sequence == expected;
        private void LogError(string message) => NativeApiLog.Error(logger, message);

        // Root-only retirement: a role may release only its own exact lease.
        internal void RetireEndedWorld()
        {
            var core = identity;
            if (core?.CurrentLease == null) return;
            lock (engineGate)
            {
                var lease = core.CurrentLease;
                if (lease != null) core.RetireEndedWorld(lease.Epoch);
            }
        }
        private bool SendProtected(byte[] payload) => RunSubmission(payload, originalSend);
        internal bool RunSubmission(byte[] payload, Func<byte[], bool> submit)
        {
            bool sent = false;
            try
            {
                bool completed = TryRunWithOriginalIdentity(payload, submit, out sent);
                if (sent) return true;
                if (!completed) throw new InvalidOperationException("Protected Chore submission rejected; no transport fallback is allowed.");
                return false;
            }
            catch when (sent) { return true; }
        }
        internal bool TryRunWithOriginalIdentity<TState, TResult>(TState state,
            Func<TState, TResult> original, out TResult result)
        {
            var core = identity;
            if (core == null) { result = original(state); return true; }
            try
            {
                // Serialize even an unbound core: a view lease can be acquired
                // between a pre-lock check and the raw sender's own lock.
                lock (engineGate) return core.TryRunWriter(state, original, out result, out _);
            }
            finally { ObserveFailure(core); }
        }
        internal void ObserveFailure(PlayerViewIdentity core)
        {
            if (core == null || !ReferenceEquals(core, identity)) return;
            var failure = core.LastFailure;
            if (failure == null) return;
            ObserveFailure(core, failure);
        }
        internal void ObserveFailure(PlayerViewIdentity core, PlayerViewIdentity.FailureRecord failure)
        {
            if (core == null || failure == null || !ReferenceEquals(core, identity)) return;
            lock (failureGate)
            {
                // A captured old report may have waited while a newer owner was
                // reported. Never reset that owner's delivery/deduplication state.
                if (!ReferenceEquals(core.CurrentLease, failure.Lease)) return;
                if (!ReferenceEquals(reportedLease, failure.Lease))
                { reportedLease = failure.Lease; reportedFailures.Clear(); }
                if (!reportedFailures.Add(failure.Reason)) return;
            }
            if (!QueueFailure(failure))
                lock (failureGate)
                    if (ReferenceEquals(reportedLease, failure.Lease)) reportedFailures.Remove(failure.Reason);
        }
        // Allocation stays behind the dormant/already-reported checks.
        private bool QueueFailure(PlayerViewIdentity.FailureRecord failure) => enqueue(() => {
            var owner = failure.Lease.Owner as PlayerViewIdentityOwner;
            try { writeError("PLAYER_VIEW_IDENTITY_FAILED: owner=" + (owner?.Name ?? "internal") +
                ", world=" + failure.Lease.Epoch + ", token=" + failure.Lease.Token + ": " + failure.Reason); }
            catch { }
            // Late delivery must never notify/fault a replacement lease.
            if (identity.IsCurrent(failure.Lease.Owner, failure.Lease))
                try { owner?.Notify(failure); } catch (Exception error) { try { writeError("PLAYER_VIEW_IDENTITY_OBSERVER_FAILED: " + error); } catch { } }
        });
    }
}
