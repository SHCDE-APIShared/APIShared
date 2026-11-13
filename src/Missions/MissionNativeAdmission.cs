using System;
using System.Threading;

namespace APIShared
{
    // Internal integration seam for ONE trusted shared default-stat owner. This is not a
    // public cleanup event, native writer, quiescence proof or permission to attach a mod.
    // No production owner is attached by this proposal. The five additional native
    // imports and raw/direct callers remain outside the qualified frontier.
    internal interface IMissionNativeRetirementOwner
    {
        // Nonthrowing, allocation-free demand read. Enabled policy OR outstanding/faulted
        // claims count as demand; disabling a consumer must not erase retirement debt.
        bool RequiresBoundary { get; }

        // Close cooperating claim admission irreversibly BEFORE any lifecycle callback.
        // Freeze the exact outstanding claims once. No public callbacks, waits or native
        // calls here. Failure leaves admission closed; this attempt never reopens it.
        void CloseAdmission(MissionNativeAdmission.Attempt attempt);

        // Called exactly once with the real engine monitor held on the Unity thread.
        // Only an independently qualified bounded table-owner backend may implement this.
        // No callbacks, I/O, stop/join or monitor handoff. false/throw prevents Original;
        // partial retirement remains owned/faulted and must not be forgotten on failure.
        bool TryRetire(MissionNativeAdmission.Attempt attempt, object engine, out string reason);
    }

    internal enum MissionNativeOuterEntry
    {
        Unsupported, EditorCreate, EditorLoad, SaveLoad,
        SkirmishStart, MultiplayerStart, MultiplayerSave
    }

    internal enum MissionNativeInnerEntry { EditorCreate, EditorLoad, SaveLoad, MapLoad, MultiplayerInit }

    internal sealed class MissionNativeAdmission
    {
        internal sealed class Attempt
        {
            internal readonly MissionNativeOuterEntry Entry;
            internal readonly int ThreadId;
            internal bool Retired, Closed;
            internal int NativePhase;
            internal bool OriginalOpen, InnerOpen, Failed, Completed;
            internal Attempt(MissionNativeOuterEntry entry)
            { Entry = entry; ThreadId = Thread.CurrentThread.ManagedThreadId; }
        }

        // This exception is private to the managed owner chain, NEVER an SE observer.
        // Its object identity binds a later inner refusal to the exact outer attempt.
        internal sealed class Abort : Exception
        {
            internal readonly Attempt Attempt;
            internal Abort(Attempt attempt, string reason) : base(reason) { Attempt = attempt; }
        }

        private IMissionNativeRetirementOwner owner;
        private Attempt active;
        internal bool Required => active != null || (owner != null && owner.RequiresBoundary);
        internal Exception LastCleanupFailure { get; private set; }

        // Value scopes: no closure/boxing in the concrete producer's using sites.
        // Logical callbacks before Original and inside another native stage cannot
        // borrow the attempt to start an initializer of their own.
        internal readonly struct Scope : IDisposable
        {
            private readonly Attempt attempt;
            private readonly bool inner;
            internal Scope(Attempt attempt, bool inner) { this.attempt = attempt; this.inner = inner; }
            public void Dispose()
            {
                if (attempt == null) return;
                if (inner) attempt.InnerOpen = false;
                else attempt.OriginalOpen = false;
            }
        }

        internal Scope OpenOriginal()
        {
            if (!Required) return default;
            Attempt attempt = active;
            if (attempt == null || !attempt.Retired || attempt.Closed || attempt.Failed || attempt.Completed || attempt.OriginalOpen ||
                attempt.ThreadId != Thread.CurrentThread.ManagedThreadId)
                throw new InvalidOperationException("Native retirement has no current outer Original.");
            attempt.OriginalOpen = true;
            return new Scope(attempt, false);
        }

        internal void CompleteOriginal(ref bool returnedNormally)
        {
            if (!Required) return;
            Attempt attempt = active;
            if (attempt == null) throw new InvalidOperationException("No admitted outer Original is returning.");
            if (attempt.Failed)
            {
                returnedNormally = false;
                throw new Abort(attempt, "An inner admission refusal was caught inside Original.");
            }
            attempt.Completed = true;
        }

        internal void RequireContinuingInner()
        {
            if (!Required) return;
            Attempt attempt = active;
            if (attempt == null) throw new InvalidOperationException("No admitted native stage is current.");
            if (attempt.Failed || attempt.Closed || attempt.Completed || !attempt.OriginalOpen || !attempt.InnerOpen ||
                attempt.ThreadId != Thread.CurrentThread.ManagedThreadId)
            {
                attempt.Failed = true;
                throw new Abort(attempt, "The native stage lost admission before its continuation.");
            }
        }

        // Process-lifetime attachment on the owning Unity thread; no registry or locks.
        // A real backend and availability/coexistence admission are intentionally absent.
        internal void Attach(IMissionNativeRetirementOwner value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (owner != null && !ReferenceEquals(owner, value))
                throw new InvalidOperationException("The mission retirement owner is already attached.");
            if (active != null) throw new InvalidOperationException("Cannot attach during mission admission.");
            owner = value;
        }

        internal T Execute<T>(MissionNativeOuterEntry entry, bool unityThread, Func<object> engine,
            Func<T> original, Action abandonBeforeOriginal, Action<Exception> reportCleanupFailure = null)
        {
            if (!Required) return original();
            // Reentrant starts from logical callbacks are not a new retirement attempt.
            // Existing nested behavior is completely unchanged while the seam is dormant.
            if (active != null) throw new InvalidOperationException("Mission retirement admission is already active.");
            var attempt = new Attempt(entry);
            active = attempt;
            LastCleanupFailure = null;
            bool entered = false, failing = false;
            try
            {
                if (!unityThread) throw new Abort(attempt, "Native retirement requires the owning Unity thread.");
                owner.CloseAdmission(attempt);
                if (!IsSupported(entry))
                    throw new Abort(attempt, "This outer native initialization path is not qualified for retirement.");
                object monitor = engine();
                if (monitor == null) throw new Abort(attempt, "The qualified engine monitor is unavailable.");
                lock (monitor)
                {
                    if (!owner.TryRetire(attempt, monitor, out string reason))
                        throw new Abort(attempt, reason ?? "Required native retirement failed.");
                    attempt.Retired = true;
                }
                entered = true;
                T result = original();
                if (attempt.Failed) throw new Abort(attempt, "An earlier native admission refusal was caught inside Original.");
                return result;
            }
            catch (Abort failure) when (ReferenceEquals(failure.Attempt, attempt))
            {
                // Do NOT return default/0 to a caller which can continue postLoading or
                // startSimThread without checking errorCode. Leave that managed Original.
                failing = true;
                throw new InvalidOperationException("Mission native admission refused: " + failure.Message, failure);
            }
            catch { failing = true; throw; }
            finally
            {
                attempt.Closed = true;
                active = null;
                // Existing Run finally owns cleanup once entered. Early refusals occur
                // before state.Begin/Original and release only pre-start sidecar context.
                // This runs outside the engine monitor and does not roll back game/UI state.
                if (!entered)
                {
                    try { abandonBeforeOriginal?.Invoke(); }
                    catch (Exception error)
                    {
                        LastCleanupFailure = error;
                        try { reportCleanupFailure?.Invoke(error); } catch { }
                        if (!failing) throw;
                    }
                }
            }
        }

        internal Scope EnterInner(MissionNativeInnerEntry entry)
        {
            if (!Required) return default;
            Attempt attempt = active;
            if (attempt == null)
                throw new InvalidOperationException("Untracked native initialization cannot retire shared defaults.");
            // Start/End and attempt-finished observers are delivered after the outer
            // Original. Refuse a new native call without retroactively changing that
            // completed operation's normal/failed publication.
            if (attempt.Completed) throw new InvalidOperationException("The outer native admission has completed.");
            if (!attempt.Retired || attempt.Closed || attempt.Failed || !attempt.OriginalOpen || attempt.InnerOpen ||
                attempt.ThreadId != Thread.CurrentThread.ManagedThreadId ||
                !Allows(attempt.Entry, entry) || !TakeInner(attempt, entry))
            {
                attempt.Failed = true;
                throw new Abort(attempt, "The native entry does not belong to this retired outer attempt.");
            }
            attempt.InnerOpen = true;
            return new Scope(attempt, true);
        }

        private static bool TakeInner(Attempt attempt, MissionNativeInnerEntry entry)
        {
            // Consume BEFORE existing logical checkpoints can reenter a native wrapper.
            // No retry/rebind: a rejected or throwing native stage stays consumed.
            if (attempt.Entry == MissionNativeOuterEntry.SkirmishStart)
            {
                int expected = entry == MissionNativeInnerEntry.MultiplayerInit ? 0 : 1;
                if (attempt.NativePhase != expected) return false;
            }
            else if (attempt.NativePhase != 0) return false;
            attempt.NativePhase++;
            return true;
        }

        internal static bool Allows(MissionNativeOuterEntry outer, MissionNativeInnerEntry inner)
        {
            switch (outer)
            {
                case MissionNativeOuterEntry.EditorCreate: return inner == MissionNativeInnerEntry.EditorCreate;
                case MissionNativeOuterEntry.EditorLoad: return inner == MissionNativeInnerEntry.EditorLoad;
                case MissionNativeOuterEntry.SaveLoad: return inner == MissionNativeInnerEntry.SaveLoad;
                case MissionNativeOuterEntry.SkirmishStart:
                    return inner == MissionNativeInnerEntry.MultiplayerInit || inner == MissionNativeInnerEntry.MapLoad;
                default: return false;
            }
        }

        internal static bool IsSupported(MissionNativeOuterEntry entry) =>
            entry == MissionNativeOuterEntry.EditorCreate || entry == MissionNativeOuterEntry.EditorLoad ||
            entry == MissionNativeOuterEntry.SaveLoad || entry == MissionNativeOuterEntry.SkirmishStart;
        // Platform StartGame/StartSave can defer client loading to processMessage after
        // the outer Original returns. This proposal has no retained deferred receipt;
        // both families therefore refuse at OUTER admission rather than fail mid-message.
    }
}
