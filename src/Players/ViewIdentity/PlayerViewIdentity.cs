using System;
using System.Threading;

namespace APIShared.PlayerControl
{
    // One owner-bound positive participant identity protects the shared native view.
    // The process coordinator owns this state; role adapters receive lease handles,
    // never an independently constructed production guard or the native delegates.
    // Mutation/scopes require the engine monitor. Delayed failure reports cannot
    // overwrite a replacement lease's fault.
    internal sealed class PlayerViewIdentity
    {
        internal sealed class Lease
        {
            internal readonly object Owner;
            internal readonly long Epoch, Token;
            internal readonly int Human;
            internal Lease(object owner, long epoch, long token, int human)
            { Owner = owner; Epoch = epoch; Token = token; Human = human; }
        }

        internal sealed class FailureRecord
        {
            internal readonly Lease Lease;
            internal readonly string Reason;
            internal FailureRecord(Lease lease, string reason) { Lease = lease; Reason = reason; }
        }

        // Lease and fault publish atomically. An old asynchronous error cannot
        // erase a new fault by racing with lease replacement or release.
        private sealed class BoundState
        {
            internal readonly Lease Lease;
            internal readonly FailureRecord Failure;
            internal BoundState(Lease lease, FailureRecord failure) { Lease = lease; Failure = failure; }
        }

        private readonly Func<bool> inEngineContext;
        private readonly Func<long, bool> isCurrentLocalWorld;
        private readonly Func<int> readView;
        private readonly Action<int> writeView;
        private readonly Action<int> publishNetworkIdentity;
        private BoundState binding;
        private long nextToken;
        private int saveDepth;
        private bool changingIdentity;

        internal PlayerViewIdentity(Func<bool> inEngineContext, Func<long, bool> isCurrentLocalWorld,
            Func<int> readView, Action<int> writeView, Action<int> publishNetworkIdentity = null)
        {
            this.inEngineContext = inEngineContext ?? throw new ArgumentNullException(nameof(inEngineContext));
            this.isCurrentLocalWorld = isCurrentLocalWorld ?? throw new ArgumentNullException(nameof(isCurrentLocalWorld));
            this.readView = readView ?? throw new ArgumentNullException(nameof(readView));
            this.writeView = writeView ?? throw new ArgumentNullException(nameof(writeView));
            // Process-lifetime native data cell; the adapter is nonthrowing and never
            // calls observers. Publish only while the same engine monitor is held.
            this.publishNetworkIdentity = publishNetworkIdentity;
        }

        // Hot identity observation performs no lock, game lookup or allocation.
        internal Lease CurrentLease => Volatile.Read(ref binding)?.Lease;
        internal FailureRecord LastFailure => Volatile.Read(ref binding)?.Failure;
        internal int ProtectedHumanId => CurrentLease?.Human ?? 0;
        internal bool IsScopeActive => saveDepth != 0 || changingIdentity;
        internal string Failure => LastFailure?.Reason;

        internal bool IsOwned(object owner, Lease lease) => owner != null && lease != null &&
            ReferenceEquals(lease.Owner, owner) && ReferenceEquals(CurrentLease, lease);

        internal bool IsCurrent(object owner, Lease lease) => IsOwned(owner, lease) &&
            isCurrentLocalWorld(lease.Epoch) && IsOwned(owner, lease);

        internal bool TryBind(object owner, long epoch, int actualHuman, out Lease lease, out string reason)
        {
            lease = null; reason = null;
            if (owner == null || changingIdentity || saveDepth != 0 || CurrentLease != null || nextToken == long.MaxValue)
            { reason = "A non-null owner and an unbound stable view identity are required."; return false; }
            changingIdentity = true;
            try
            {
                if (!inEngineContext() || epoch <= 0 || actualHuman < 1 || actualHuman > 8 || !isCurrentLocalWorld(epoch))
                { reason = "A serialized current positive-participant view context is required."; return false; }
                // Bind before changing the view. Never adopt another owner's
                // viewpoint as the actual controller or reuse its token.
                if (!TryRead(out int current, out reason) || current != actualHuman || !isCurrentLocalWorld(epoch))
                { reason = reason ?? "The native view is not the actual participant in the requested world."; return false; }
                var next = new Lease(owner, epoch, ++nextToken, actualHuman);
                if (Interlocked.CompareExchange(ref binding, new BoundState(next, null), null) != null)
                { reason = "Another view identity was acquired during validation."; return false; }
                publishNetworkIdentity?.Invoke(actualHuman);
                lease = next;
                return true;
            }
            finally { changingIdentity = false; }
        }

        // This no-write release remains legal inside a writer callback after the
        // writer installed the own view. Its outer finally sees the retired exact
        // lease and must not restore a previously borrowed foreign view.
        internal bool TryRelease(object owner, Lease lease, out string reason)
        {
            reason = null;
            if (changingIdentity || !inEngineContext() || !IsOwned(owner, lease))
            { reason = "No matching owner-bound protected view identity."; return false; }
            changingIdentity = true;
            try
            {
                if (!IsCurrent(owner, lease))
                { reason = "The protected world has ended; retire its lease without writing into the replacement."; return false; }
                if (!TryRead(out int nativeView, out reason) || nativeView != lease.Human || !IsCurrent(owner, lease))
                { reason = reason ?? "Restore the actual participant view before releasing its identity."; return false; }
                if (!TryRemove(lease)) { reason = "The protected view lease changed during release."; return false; }
                return true;
            }
            finally { changingIdentity = false; }
        }

        // Ordinary role retirement owns only this exact lease. The root-only
        // world invalidation below may retire any lease of the ended world.
        internal bool TryRestoreAndRelease(object owner, Lease lease, out string reason)
        {
            reason = null;
            if (changingIdentity || saveDepth != 0 || !inEngineContext() || !IsOwned(owner, lease))
            { reason = "No stable matching owner-bound view lease is available for restoration."; return false; }
            changingIdentity = true;
            try
            {
                bool currentWorld = isCurrentLocalWorld(lease.Epoch);
                if (!IsOwned(owner, lease)) { reason = "The view lease changed before restoration."; return false; }
                if (!currentWorld)
                {
                    reason = "Only the central lifecycle may retire an invalidated native world's lease.";
                    return false;
                }
                if (!TryWrite(lease, lease.Human, out reason))
                { ReportFailure(lease, reason); return false; }
                if (!IsCurrent(owner, lease) || !TryRemove(lease))
                { reason = "The view lease changed during restoration."; return false; }
                return true;
            }
            finally { changingIdentity = false; }
        }

        // Root-only lifecycle replacement, never a role adapter's generic cleanup.
        // No native write; a reentrantly replaced lease remains untouched.
        internal bool RetireEndedWorld(long expectedEpoch)
        {
            var captured = CurrentLease;
            if (!inEngineContext() || captured == null || captured.Epoch != expectedEpoch || isCurrentLocalWorld(expectedEpoch)) return false;
            return TryRemove(captured);
        }

        // Delayed errors identify the exact immutable lease, not only its world
        // or controller (which a later lease may legitimately share).
        internal bool ReportFailure(Lease lease, string reason)
        {
            if (lease == null || string.IsNullOrEmpty(reason)) return false;
            while (true)
            {
                var current = Volatile.Read(ref binding);
                if (current == null || !ReferenceEquals(current.Lease, lease)) return false;
                if (current.Failure != null && string.Equals(current.Failure.Reason, reason, StringComparison.Ordinal)) return true;
                var next = new BoundState(lease, new FailureRecord(lease, reason));
                if (ReferenceEquals(Interlocked.CompareExchange(ref binding, next, current), current)) return true;
            }
        }

        // Native adapters retain the writer's result even if restoration fails
        // afterwards. Exceptions are contained by those adapter boundaries.
        internal bool TryRunWriter<TState, TResult>(TState state, Func<TState, TResult> original,
            out TResult result, out string reason)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            reason = null; result = default;
            var captured = CurrentLease;
            if (captured == null)
            {
                // The original can synchronously call back into a view consumer.
                // Keep acquisition blocked until even an unbound Save/send ends.
                saveDepth++;
                try { result = original(state); return true; }
                finally { saveDepth--; }
            }
            try { return RunCapturedWriter(captured, state, original, out result, out reason); }
            catch (Exception error)
            {
                ReportFailure(captured, "Protected operation failed: " + error.GetType().Name + ": " + error.Message);
                throw;
            }
        }

        private bool RunCapturedWriter<TState, TResult>(Lease captured, TState state, Func<TState, TResult> original,
            out TResult result, out string reason)
        {
            reason = null; result = default;
            if (changingIdentity || !inEngineContext() || !IsCurrent(captured.Owner, captured))
            {
                reason = "The protected operation has no current serialized view world.";
                ReportFailure(captured, reason);
                return false;
            }
            // Nested sends/writers are valid, but acquisition and restoring role
            // release wait until every enclosing scope has returned.
            saveDepth++;
            int previous = 0;
            bool restoreNeeded = false, restoreSucceeded = true;
            try
            {
                if (!TryRead(out previous, out reason) || previous < 1 || previous > 8 || !IsCurrent(captured.Owner, captured))
                {
                    reason = reason ?? "The previous native view or world cannot be restored.";
                    ReportFailure(captured, reason); return false;
                }
                restoreNeeded = true; // A throwing setter may already have written.
                if (!TryWrite(captured, captured.Human, out reason))
                { ReportFailure(captured, reason); return false; }
                result = original(state);
            }
            finally
            {
                try
                {
                    if (restoreNeeded && IsCurrent(captured.Owner, captured))
                    {
                        restoreSucceeded = TryWrite(captured, previous, out string restore);
                        if (!restoreSucceeded)
                        {
                            reason = (reason == null ? string.Empty : reason + " ") +
                                "Could not restore the borrowed view after the protected operation: " + restore;
                            ReportFailure(captured, reason);
                        }
                    }
                }
                finally { saveDepth--; }
            }
            return restoreSucceeded;
        }

        private bool TryRemove(Lease lease)
        {
            while (true)
            {
                var current = Volatile.Read(ref binding);
                if (current == null || !ReferenceEquals(current.Lease, lease)) return false;
                if (ReferenceEquals(Interlocked.CompareExchange(ref binding, null, current), current))
                {
                    publishNetworkIdentity?.Invoke(0);
                    return true;
                }
            }
        }

        private bool TryRead(out int view, out string reason)
        {
            view = 0; reason = null;
            try { view = readView(); return true; }
            catch (Exception error) { reason = "Native view read failed: " + error.GetType().Name + ": " + error.Message; return false; }
        }

        private bool TryWrite(Lease expected, int view, out string reason)
        {
            reason = null;
            try
            {
                if (!IsCurrent(expected.Owner, expected)) { reason = "The protected view or world changed."; return false; }
                writeView(view);
                if (!IsCurrent(expected.Owner, expected)) { reason = "The protected view or world changed during its native write."; return false; }
                if (readView() == view && IsCurrent(expected.Owner, expected)) return true;
                reason = "Native view write was not confirmed.";
            }
            catch (Exception error) { reason = "Native view write failed: " + error.GetType().Name + ": " + error.Message; }
            return false;
        }
    }
}
