using System;
using System.Runtime.InteropServices;
using SHCDESE.Detours;

namespace APIShared.Networking
{
    internal static class NativeChoreQueueBinding
    {
        // expectedAddress is the audited queue entry in the already hash-validated image.
        // Unity/Mono may marshal a native delegate back to a managed-to-native wrapper,
        // so GetFunctionPointerForDelegate is not an inverse of GetDelegateForFunctionPointer.
        // Compare the exact typed native binding instead; this invokes neither delegate.
        internal static bool Matches(BulkChoreDetours.c_game_queue_chore_delegate queue, IntPtr expectedAddress)
        {
            if (queue == null || expectedAddress == IntPtr.Zero) return false;
            var expected = Marshal.GetDelegateForFunctionPointer<BulkChoreDetours.c_game_queue_chore_delegate>(expectedAddress);
            return queue.Equals(expected);
        }
    }
}
