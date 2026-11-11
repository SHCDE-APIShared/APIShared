using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace APIShared.Audio
{
    // Demand bytes read by the native bridges before any managed transition, and per-source demand
    // behind the return filters. Owned transitions: SoundDemand count changes (registration attach,
    // SetEnabled), each carrying the handle's SoundSourceSelection, and native context readiness
    // (SoundContextPolicy.SetReady). Each recomputes both bytes and every attached filter bitmap
    // from the current counts under one lock, so the last writer always reflects the latest state.
    internal static unsafe class SoundNativeDemand
    {
        private static readonly object gate = new object();
        // Process-lifetime unmanaged storage: the permanent bridges embed this address.
        private static readonly byte* bytes = Allocate();
        // Enabled handles per SoundSourceSelection demand index. Playback counts only soldier
        // sources (Unity records never carry kill context); source events count both.
        private static readonly int[] playbackSources = new int[SoundSourceSelection.Count];
        private static readonly int[] eventSources = new int[SoundSourceSelection.Count];
        // Process-lifetime catalog filters (one per installed runtime; tests install private copies).
        private static SoundReturnFilter[] filters = new SoundReturnFilter[0];
        internal static readonly SoundDemand Playback = new SoundDemand((sources, delta) => Update(playbackSources, sources, delta, SoundSources.All.Length));
        internal static readonly SoundDemand Source = new SoundDemand((sources, delta) => Update(eventSources, sources, delta, SoundSourceSelection.Count));
        /// <summary>Wrapper bridges enter managed code only when this byte is nonzero.</summary>
        internal static IntPtr Wrapper => new IntPtr(bytes);
        /// <summary>The emitter bridge enters managed code only when this byte is nonzero.</summary>
        internal static IntPtr Emitter => new IntPtr(bytes + 1);
        internal static byte WrapperValue => Volatile.Read(ref bytes[0]);
        internal static byte EmitterValue => Volatile.Read(ref bytes[1]);
        // Count of wrapper calls (across threads) that currently expect the emitter to mark a
        // record: raised only around a matched soldier source with playback demand. The emitter
        // bridge tests its low byte, so every other emitter call stays native. Depth is bounded
        // by concurrent threads (the wrapper is not reentrant), far below 256.
        internal static IntPtr EmitterArmed => new IntPtr(bytes + 4);
        internal static void Arm() => Interlocked.Increment(ref *(int*)(bytes + 4));
        internal static void Disarm() => Interlocked.Decrement(ref *(int*)(bytes + 4));
        internal static int ArmedValue => Volatile.Read(ref *(int*)(bytes + 4));
        // Hot-path reads behind the native filter: lock-free, never enumerate handles.
        internal static bool PlaybackWants(int index) => Volatile.Read(ref playbackSources[index]) != 0;
        internal static bool SourceWants(int index) => Volatile.Read(ref eventSources[index]) != 0;
        internal static int PlaybackCount(int index) => Volatile.Read(ref playbackSources[index]);
        internal static int SourceCount(int index) => Volatile.Read(ref eventSources[index]);
        internal static bool Wanted(int[] playback, int[] source, int index) =>
            Volatile.Read(ref playback[index]) != 0 || Volatile.Read(ref source[index]) != 0;
        private static byte* Allocate()
        {
            var memory = (byte*)Marshal.AllocHGlobal(16).ToPointer();
            for (int i = 0; i < 16; i++) memory[i] = 0;
            return memory;
        }
        internal static void Attach(SoundReturnFilter filter)
        {
            lock (gate) {
                var next = new SoundReturnFilter[filters.Length + 1];
                Array.Copy(filters, next, filters.Length); next[filters.Length] = filter;
                filters = next;
                filter.Publish(playbackSources, eventSources);
            }
        }
        // Caller holds a SoundDemand gate (handle -> demand -> this gate). An increase is published
        // before the handle is enabled, a decrease after it is disabled.
        private static void Update(int[] counts, SoundSourceSelection sources, int delta, int limit)
        {
            lock (gate) {
                for (int i = 0; i < limit; i++) if (sources.Has(i)) Volatile.Write(ref counts[i], counts[i] + delta);
                RefreshLocked();
            }
        }
        // Wrappers serve playback attribution (record marking) and native source events;
        // the emitter only marks records for Unity playback.
        internal static void Refresh() { lock (gate) RefreshLocked(); }
        private static void RefreshLocked()
        {
            bool context = SoundContextPolicy.Active;
            bool playback = Playback != null && Playback.HasObservers, source = Source != null && Source.HasObservers;
            foreach (var filter in filters) filter.Publish(playbackSources, eventSources);
            Volatile.Write(ref bytes[0], (byte)(context && (playback || source) ? 1 : 0));
            Volatile.Write(ref bytes[1], (byte)(context && playback ? 1 : 0));
        }
    }
}
