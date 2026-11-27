using System;
using System.Threading;

namespace APIShared.ScriptExtenderFixes
{
    // No Unity/component lifetime and no native resource owner. Shutdown is terminal.
    internal sealed class ImGuiShutdownState
    {
        private int shutdown;
        private IntPtr previousWindowProc;
        internal bool IsShutdown => Volatile.Read(ref shutdown) != 0;
        internal bool BeginShutdown() => Interlocked.Exchange(ref shutdown, 1) == 0;
        internal IntPtr PreviousWindowProc => Interlocked.CompareExchange(ref previousWindowProc, IntPtr.Zero, IntPtr.Zero);

        internal IntPtr RememberWindowProc(IntPtr incoming, IntPtr ownWindowProc)
        {
            // SetWindowLongPtr can return our own hook on repeated initialization.
            // Never overwrite a valid predecessor with that self-reference or null.
            if (incoming != IntPtr.Zero && incoming != ownWindowProc)
                Interlocked.Exchange(ref previousWindowProc, incoming);
            return PreviousWindowProc;
        }
    }
}
