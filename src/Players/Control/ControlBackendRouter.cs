using System;
using System.Threading;

namespace APIShared.PlayerControl
{
    internal enum ControlBackendKind
    {
        SingleplayerExclusive,
        SingleplayerMixed,
        MultiplayerExclusive,
        MultiplayerMixed
    }

    internal interface IControlBackend
    {
        bool IsAvailable { get; }
        string UnavailableReason { get; }
        bool TryExecute(ControlExecutionPlan plan, out string reason);
    }

    // Failure domains follow actual operation dependencies. In particular the local
    // single-owner backend never acquires a network/mixed provider, negotiates peers,
    // allocates fragments or consults their health to operate. No cross-mode fallback
    // is permitted: a failed multiplayer request must not become a local world edit.
    internal sealed class ControlBackendRouter
    {
        private sealed class Slot
        {
            internal readonly IControlBackend Backend;
            internal readonly string Failure;
            internal readonly bool Attempted;
            internal Slot(IControlBackend backend, string failure, bool attempted)
            { Backend = backend; Failure = failure; Attempted = attempted; }
        }
        private readonly Slot[] slots = new Slot[4];

        internal ControlBackendRouter()
        {
            for (int i = 0; i < slots.Length; i++) slots[i] = new Slot(null, "Backend has not been initialized.", false);
        }

        // Startup calls each factory independently. Throwing from one optional
        // initializer does not abort or dispose any previously published provider.
        internal bool Initialize(ControlBackendKind kind, Func<IControlBackend> create, out string reason)
        {
            int index = Index(kind);
            if (create == null) throw new ArgumentNullException(nameof(create));
            var prior = Volatile.Read(ref slots[index]);
            if (prior.Attempted || !ReferenceEquals(Interlocked.CompareExchange(ref slots[index],
                new Slot(null, "Backend is initializing.", true), prior), prior))
            { reason = "Backend initialization has already been attempted."; return false; }
            try
            {
                IControlBackend backend = create();
                if (backend == null) throw new InvalidOperationException("Backend factory returned null.");
                // Root before evaluating availability, which can itself fail.
                Volatile.Write(ref slots[index], new Slot(backend, null, true));
                return TryGet(kind, out _, out reason);
            }
            catch (Exception error) { reason = Fault(index, error); return false; }
        }

        internal bool TryGet(bool realMultiplayer, PlayerControlSelectionMode mode, out IControlBackend backend, out string reason)
        {
            backend = null; reason = null;
            if (mode != PlayerControlSelectionMode.ExclusivePlayer && mode != PlayerControlSelectionMode.AdditionalOwners)
            { reason = "Unknown selection mode."; return false; }
            return TryGet(Select(realMultiplayer, mode), out backend, out reason);
        }

        internal bool TryExecute(bool realMultiplayer, PlayerControlSelectionMode mode,
            ControlExecutionPlan plan, out string reason)
        {
            reason = null;
            if (plan == null) { reason = "No validated command plan."; return false; }
            if (!TryGet(realMultiplayer, mode, out var backend, out reason)) return false;
            try { return backend.TryExecute(plan, out reason); }
            catch (Exception error) { reason = Fault(Index(Select(realMultiplayer, mode)), error); return false; }
        }

        private bool TryGet(ControlBackendKind kind, out IControlBackend backend, out string reason)
        {
            backend = null;
            int index = Index(kind);
            var slot = Volatile.Read(ref slots[index]);
            reason = slot.Failure;
            if (slot.Failure != null || slot.Backend == null) return false;
            try
            {
                if (!slot.Backend.IsAvailable) { reason = slot.Backend.UnavailableReason ?? "Backend is unavailable."; return false; }
                backend = slot.Backend; reason = null;
                return true;
            }
            catch (Exception error) { reason = Fault(index, error); return false; }
        }

        private string Fault(int index, Exception error)
        {
            string reason = "Backend failed: " + error.GetType().Name + ": " + error.Message;
            // Logical deactivation only. Native hooks/providers already published by
            // this backend remain rooted; no executable bytes or other slots change.
            var previous = Volatile.Read(ref slots[index]);
            Volatile.Write(ref slots[index], new Slot(previous.Backend, reason, true));
            return reason;
        }

        private static ControlBackendKind Select(bool multiplayer, PlayerControlSelectionMode mode) =>
            multiplayer ? mode == PlayerControlSelectionMode.ExclusivePlayer ? ControlBackendKind.MultiplayerExclusive : ControlBackendKind.MultiplayerMixed :
            mode == PlayerControlSelectionMode.ExclusivePlayer ? ControlBackendKind.SingleplayerExclusive : ControlBackendKind.SingleplayerMixed;

        private static int Index(ControlBackendKind kind)
        {
            int index = (int)kind;
            if (index < 0 || index >= 4) throw new ArgumentOutOfRangeException(nameof(kind));
            return index;
        }
    }
}
