using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared
{
    // Pure state machine: no game objects, native pointers, or mutable event arguments escape.
    internal sealed class MissionLifecycleState
    {
        private readonly Dictionary<string, Action<MissionLifecycleNotification>> observers =
            new Dictionary<string, Action<MissionLifecycleNotification>>(StringComparer.Ordinal);
        private readonly Queue<Action> queue = new Queue<Action>();
        private readonly Action<Exception> error;
        private bool dispatching;
        private long sequence;
        private MissionContext pending;
        private MissionInitializationPhase phase;
        private readonly HashSet<MissionInitializationPhase> reached = new HashSet<MissionInitializationPhase>();
        private NativeClockState nativeClock;
        private int nativeLoadDepth;
        private bool nativeLoadFailed;
        internal NativeClockState NativeClock => Volatile.Read(ref nativeClock);
        internal sealed class NativeClockState
        {
            internal readonly MissionContext Context;
            internal NativeClockState(MissionContext context) { Context = context; }
        }
        internal void InvalidateNativeClock() => Volatile.Write(ref nativeClock, null);
        internal void BeginNativeLoad(long id)
        {
            if (Context?.SessionId != id) return;
            InvalidateNativeClock();
            if (nativeLoadDepth == 0) nativeLoadFailed = false;
            nativeLoadDepth++;
        }
        internal void CompleteNativeLoad(long id, bool success)
        {
            if (Context?.SessionId != id) return;
            if (!success) nativeLoadFailed = true;
            if (nativeLoadDepth > 0) nativeLoadDepth--;
            if (success && !nativeLoadFailed && nativeLoadDepth == 0)
                Volatile.Write(ref nativeClock, new NativeClockState(Context));
        }
        private void RefreshNativeClock(MissionContext context)
        {
            if (NativeClock != null && nativeLoadDepth == 0)
                Volatile.Write(ref nativeClock, new NativeClockState(context));
        }
        internal MissionContext Current { get; private set; }
        internal MissionContext Context => pending ?? Current;
        internal MissionLifecycleState(Action<Exception> error) { this.error = error; }

        internal bool Register(string owner, string id, Action<MissionLifecycleNotification> callback)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || callback == null) return false;
            string key = owner.Length + ":" + owner + id;
            if (observers.ContainsKey(key)) return false;
            observers.Add(key, callback);
            var current = Current;
            if (current != null)
            {
                queue.Enqueue(() => callback(new MissionLifecycleNotification(current, MissionLifecycleKind.Start,
                    MissionInitializationPhase.ManagedReady, MissionEndReason.None, true, true)));
                Drain();
            }
            return true;
        }

        internal long Begin(MissionContext seed)
        {
            // Install the new pending state before dispatching old End, so reentrant replacement wins.
            var old = Context;
            bool started = Current != null;
            var oldPhase = phase;
            InvalidateNativeClock();
            nativeLoadDepth = 0;
            long id = ++sequence;
            Current = null;
            pending = seed.With(id, seed.Mode);
            phase = MissionInitializationPhase.None;
            reached.Clear();
            if (old != null) Publish(new MissionLifecycleNotification(old, MissionLifecycleKind.End,
                oldPhase, MissionEndReason.Replaced, started, pending));
            return id;
        }

        internal bool IsPending(long id) => pending != null && pending.SessionId == id;
        internal bool Finish(long id, MissionContext context, bool returnedNormally,
            bool nativeSucceeded, bool managedSucceeded, bool asynchronousClient)
        {
            if (!IsPending(id)) return false;
            if (!returnedNormally) End(MissionEndReason.Exception, id);
            else if (!nativeSucceeded) End(MissionEndReason.Failed, id);
            else if (managedSucceeded) Ready(id, context);
            else if (asynchronousClient) return true;
            else End(MissionEndReason.Failed, id);
            return false;
        }
        internal void Checkpoint(long id, MissionContext context, MissionInitializationPhase next)
        {
            if (!IsPending(id) || !reached.Add(next)) return;
            pending = context.With(id, context.Mode);
            phase = next;
            RefreshNativeClock(pending);
            Publish(new MissionLifecycleNotification(pending, MissionLifecycleKind.Initialization,
                phase, MissionEndReason.None, false));
        }
        internal void Ready(long id, MissionContext context)
        {
            if (!IsPending(id)) return;
            Current = context.With(id, context.Mode);
            pending = null;
            RefreshNativeClock(Current);
            phase = MissionInitializationPhase.ManagedReady;
            Publish(new MissionLifecycleNotification(Current, MissionLifecycleKind.Start, phase, MissionEndReason.None, true));
        }
        internal void End(MissionEndReason reason, long? expected = null)
        {
            var old = Context;
            if (old == null || (expected.HasValue && old.SessionId != expected.Value)) return;
            bool started = Current != null;
            InvalidateNativeClock();
            nativeLoadDepth = 0;
            Current = null; pending = null;
            var oldPhase = phase;
            phase = MissionInitializationPhase.None;
            reached.Clear();
            Publish(new MissionLifecycleNotification(old, MissionLifecycleKind.End, oldPhase, reason, started));
        }
        private void Publish(MissionLifecycleNotification e)
        {
            foreach (var observer in new List<Action<MissionLifecycleNotification>>(observers.Values))
                queue.Enqueue(() => observer(e));
            Drain();
        }
        private void Drain()
        {
            if (dispatching) return;
            dispatching = true;
            try
            {
                while (queue.Count != 0)
                {
                    try { queue.Dequeue()(); }
                    catch (Exception ex) { try { error?.Invoke(ex); } catch { } }
                }
            }
            finally { dispatching = false; }
        }
    }
}
