using System;
using System.Threading;

namespace APIShared.Audio
{
    // Only audio handles use this cache: every activation transition is owned here.
    // Read-side guards never lock or enumerate subscribers. Updates serialize rarely.
    internal sealed class SoundDemand
    {
        private readonly object gate = new object();
        private readonly Action<SoundSourceSelection, int> changed;
        private int active;
        // changed(sources, +1/-1) republishes derived native demand for the handle's sources. It runs
        // inside this gate after a count increase and before the handle's enabled flag, and after the
        // flag is cleared on disable, so no enabled handle meets a false zero.
        internal SoundDemand(Action<SoundSourceSelection, int> changed = null) { this.changed = changed; }
        internal bool HasObservers => Volatile.Read(ref active) != 0;
        internal int ActiveCount => Volatile.Read(ref active);
        internal void Attach(SoundRegistration handle)
        {
            lock (handle.ActivationSync) {
                if (handle.Demand != null) throw new InvalidOperationException("Audio handle already attached.");
                lock (gate) {
                    if (handle.Enabled) { Volatile.Write(ref active, active + 1); changed?.Invoke(handle.Sources, 1); }
                    handle.Demand = this;
                }
            }
        }
        // Caller holds the handle lock; all writers acquire handle -> demand, never reverse.
        internal void SetEnabled(SoundRegistration handle, bool value)
        {
            lock (gate) {
                if (value) {
                    // Publish demand first: transient false positives are safe, false zero is not.
                    Volatile.Write(ref active, active + 1);
                    changed?.Invoke(handle.Sources, 1);
                    Volatile.Write(ref handle.EnabledValue, 1);
                } else {
                    Volatile.Write(ref handle.EnabledValue, 0);
                    Volatile.Write(ref active, active - 1);
                    changed?.Invoke(handle.Sources, -1);
                }
            }
        }
    }
}
