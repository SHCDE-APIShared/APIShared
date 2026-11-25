using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace APIShared.Economy
{
    // Only the exact original invocation owns this flag. SE wrapper recursion suspends it
    // before any foreign Pre callback and restores it after the complete nested wrapper.
    internal static unsafe class AllyTransferChatScope
    {
        internal static readonly IntPtr DemandAddress = AllocateDemand();
        [ThreadStatic] private static bool suppressed;
        [ThreadStatic] private static List<Token> wrappers;
        internal static bool IsSuppressed => suppressed;
        private static IntPtr AllocateDemand()
        { var pointer = Marshal.AllocHGlobal(4); Marshal.WriteInt32(pointer, 0); return pointer; }
        internal static Token Enter(bool suppress)
        { var token = new Token(suppressed); Set(suppress); return token; }
        internal static Token Suspend() => Enter(false);
        private static void Set(bool value)
        {
            if (suppressed == value) return;
            // The native fast gate counts active threads; the callback still checks TLS.
            if (value) Interlocked.Increment(ref *(int*)DemandAddress);
            suppressed = value;
            if (!value) Interlocked.Decrement(ref *(int*)DemandAddress);
        }
        internal static void EnterWrapper()
        {
            if (!suppressed && (wrappers == null || wrappers.Count == 0)) return;
            if (wrappers == null) wrappers = new List<Token>();
            var token = Suspend();
            try { wrappers.Add(token); }
            catch { token.Dispose(); throw; }
        }
        internal static void LeaveWrapper()
        {
            if (wrappers == null || wrappers.Count == 0) return;
            int index = wrappers.Count - 1; var token = wrappers[index];
            wrappers.RemoveAt(index); token.Dispose();
        }
        internal readonly struct Token : IDisposable
        {
            private readonly bool previous;
            internal Token(bool previous) { this.previous = previous; }
            public void Dispose() => Set(previous);
        }
    }
}
