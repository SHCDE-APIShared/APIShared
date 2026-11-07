using System;

namespace APIShared.Economy
{
    // Shared engine-run admission boundary, before Vanilla can reserve its render/MP buffer.
    // Capture the epoch even while dormant: an old call may already be waiting on the engine
    // monitor when a new world closes admission. Dependencies are bound once at startup.
    internal sealed class WorkerProductionRunGate
    {
        private readonly object engineGate;
        private readonly Func<long> generation;
        private readonly Func<bool> enabled, faulted, admitted;
        private readonly Func<bool,int> original;
        private readonly Action observeFault;

        internal WorkerProductionRunGate(object engineGate, Func<long> generation, Func<bool> enabled,
            Func<bool> faulted, Func<bool> admitted, Func<bool,int> original, Action observeFault)
        {
            this.engineGate = engineGate ?? throw new ArgumentNullException(nameof(engineGate));
            this.generation = generation ?? throw new ArgumentNullException(nameof(generation));
            this.enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
            this.faulted = faulted ?? throw new ArgumentNullException(nameof(faulted));
            this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted));
            this.original = original ?? throw new ArgumentNullException(nameof(original));
            this.observeFault = observeFault ?? throw new ArgumentNullException(nameof(observeFault));
        }

        internal int Run(bool mpFrameSkip)
        {
            long expectedGeneration = generation();
            lock (engineGate)
            {
                if (expectedGeneration != generation()) return 0;
                if (enabled())
                {
                    if (faulted()) { observeFault(); return 0; }
                    if (!admitted()) return 0;
                }
                int result = original(mpFrameSkip);
                // Lifecycle callbacks can reenter this monitor during Original. Never return
                // an obsolete epoch's result, including a run that entered while dormant.
                return expectedGeneration != generation() || faulted() ? 0 : result;
            }
        }
    }
}
