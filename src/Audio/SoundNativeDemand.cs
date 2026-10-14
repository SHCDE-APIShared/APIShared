using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace APIShared.Audio
{
    // Demand bytes read by the native bridges before any managed transition. Owned transitions:
    // SoundDemand count changes (registration attach, SetEnabled) and native context readiness
    // (SoundContextPolicy.SetReady). Each calls Refresh, which recomputes both bytes from the
    // current counts under one lock, so the last writer always reflects the latest state.
    internal static unsafe class SoundNativeDemand
    {
        private static readonly object gate = new object();
        // Process-lifetime unmanaged storage: the permanent bridges embed this address.
        private static readonly byte* bytes = Allocate();
        internal static readonly SoundDemand Playback = new SoundDemand(Refresh);
        internal static readonly SoundDemand Source = new SoundDemand(Refresh);
        /// <summary>Wrapper bridges enter managed code only when this byte is nonzero.</summary>
        internal static IntPtr Wrapper => new IntPtr(bytes);
        /// <summary>The emitter bridge enters managed code only when this byte is nonzero.</summary>
        internal static IntPtr Emitter => new IntPtr(bytes + 1);
        internal static byte WrapperValue => Volatile.Read(ref bytes[0]);
        internal static byte EmitterValue => Volatile.Read(ref bytes[1]);
        private static byte* Allocate()
        {
            var memory = (byte*)Marshal.AllocHGlobal(16).ToPointer();
            for (int i = 0; i < 16; i++) memory[i] = 0;
            return memory;
        }
        // Wrappers serve playback attribution (record marking) and native source events;
        // the emitter only marks records for Unity playback.
        internal static void Refresh()
        {
            lock (gate) {
                bool context = SoundContextPolicy.Active;
                bool playback = Playback != null && Playback.HasObservers, source = Source != null && Source.HasObservers;
                Volatile.Write(ref bytes[0], (byte)(context && (playback || source) ? 1 : 0));
                Volatile.Write(ref bytes[1], (byte)(context && playback ? 1 : 0));
            }
        }
    }
}
