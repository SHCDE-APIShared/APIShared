using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace APIShared.Audio
{
    // Native prefilter for the wrapper bridges: a bitmap over absolute return addresses that
    // OnWrapper can attribute (catalog soldier sources and melee kill sites). Any other caller
    // (workers, animals, buildings, ...) leaves OnWrapper without marking or dispatching
    // anything, so the bridge skips the managed transition for it. Bit i covers Base + i.
    // Process-lifetime unmanaged storage: published bridges embed Base, Length and Bitmap, so a
    // demand change rewrites bits in place and never repatches a bridge.
    internal sealed unsafe class SoundReturnFilter
    {
        internal readonly ulong Base;
        internal readonly ulong Length;
        internal readonly IntPtr Bitmap;
        // Catalog filters only: bitmap byte slot and bit mask per SoundSourceSelection demand index.
        private readonly int[] slotOf;
        private readonly byte[] maskOf;
        private readonly int[] slotByte;
        private readonly byte[] scratch;

        private SoundReturnFilter(ulong baseAddress, ulong length, IntPtr bitmap, int[] slotOf, byte[] maskOf, int[] slotByte)
        {
            Base = baseAddress; Length = length; Bitmap = bitmap;
            this.slotOf = slotOf; this.maskOf = maskOf; this.slotByte = slotByte;
            scratch = slotByte == null ? null : new byte[slotByte.Length];
        }

        // Starts empty and follows SoundNativeDemand: only sources some enabled registration needs
        // have their bit set. Attaching publishes the current demand before the bridges exist.
        internal static SoundReturnFilter ForCatalog(ulong moduleBase)
        {
            int count = SoundSourceSelection.Count;
            var addresses = new ulong[count];
            for (int i = 0; i < count; i++)
            {
                int rva = i < SoundSources.All.Length ? SoundSources.All[i].ReturnRva : SoundSources.Kills[i - SoundSources.All.Length].ReturnRva;
                if (rva <= 0) throw new InvalidOperationException("Invalid audio return RVA.");
                addresses[i] = unchecked(moduleBase + (uint)rva);
            }
            IntPtr bitmap = Allocate(addresses, out ulong min, out ulong length);
            var slots = new Dictionary<ulong, int>(); var slotByte = new List<int>();
            var slotOf = new int[count]; var maskOf = new byte[count];
            for (int i = 0; i < count; i++)
            {
                ulong bit = addresses[i] - min, offset = bit >> 3;
                if (!slots.TryGetValue(offset, out int slot)) { slot = slotByte.Count; slots.Add(offset, slot); slotByte.Add(checked((int)offset)); }
                slotOf[i] = slot; maskOf[i] = (byte)(1 << (int)(bit & 7));
            }
            var filter = new SoundReturnFilter(min, length, bitmap, slotOf, maskOf, slotByte.ToArray());
            SoundNativeDemand.Attach(filter);
            return filter;
        }

        // Static filter with every address set (private-copy tests).
        internal static SoundReturnFilter ForAddresses(IReadOnlyList<ulong> addresses)
        {
            IntPtr bitmap = Allocate(addresses, out ulong min, out ulong length);
            byte* raw = (byte*)bitmap.ToPointer();
            foreach (ulong address in addresses)
            {
                ulong bit = address - min;
                raw[bit >> 3] |= (byte)(1 << (int)(bit & 7));
            }
            return new SoundReturnFilter(min, length, bitmap, null, null, null);
        }

        private static IntPtr Allocate(IReadOnlyList<ulong> addresses, out ulong min, out ulong length)
        {
            if (addresses == null || addresses.Count == 0) throw new ArgumentException("Audio return filter needs addresses.", nameof(addresses));
            min = ulong.MaxValue; ulong max = 0;
            foreach (ulong address in addresses) { if (address < min) min = address; if (address > max) max = address; }
            length = max - min + 1;
            // The catalog spans about 0x6B000 bytes (~54 KB of bits); refuse anything absurd.
            if (length > 1UL << 24) throw new InvalidOperationException("Audio return filter span is too large.");
            int bytes = checked((int)(((length + 63) / 64) * 8));
            IntPtr bitmap = Marshal.AllocHGlobal(bytes);
            byte* raw = (byte*)bitmap.ToPointer();
            for (int i = 0; i < bytes; i++) raw[i] = 0;
            return bitmap;
        }

        // Caller holds the SoundNativeDemand lock. Each touched byte is recomputed from the counts and
        // written once, so a concurrent native reader sees every bit either old or new; bits of sources
        // whose demand did not change keep their value in both. The demand ordering (raise before
        // enable, clear after disable) therefore never shows an enabled source as filtered out.
        internal void Publish(int[] playback, int[] source)
        {
            if (slotOf == null) return;
            Array.Clear(scratch, 0, scratch.Length);
            for (int i = 0; i < slotOf.Length; i++)
                if (SoundNativeDemand.Wanted(playback, source, i)) scratch[slotOf[i]] |= maskOf[i];
            byte* raw = (byte*)Bitmap.ToPointer();
            for (int slot = 0; slot < scratch.Length; slot++) Volatile.Write(ref raw[slotByte[slot]], scratch[slot]);
        }

        // Managed mirror of the native test, for validation and tests.
        internal bool Contains(ulong address)
        {
            ulong bit = unchecked(address - Base);
            if (bit >= Length) return false;
            return (Volatile.Read(ref ((byte*)Bitmap.ToPointer())[bit >> 3]) & (1 << (int)(bit & 7))) != 0;
        }
    }
}
