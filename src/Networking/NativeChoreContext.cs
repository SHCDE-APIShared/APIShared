using System;
using System.Runtime.InteropServices;
using System.Threading;
using SHCDESE.GameGlobals;

namespace APIShared.Networking
{
    internal readonly struct NativeChoreExecution
    {
        internal readonly int Sender, CommandId, ScheduledTick;
        internal readonly long Manager;
        internal readonly int Slot;
        internal NativeChoreExecution(int sender, int commandId, int scheduledTick, long manager, int slot)
        { Sender = sender; CommandId = commandId; ScheduledTick = scheduledTick; Manager = manager; Slot = slot; }
        internal bool Matches(NativeChoreExecution other) => Sender == other.Sender && CommandId == other.CommandId &&
            ScheduledTick == other.ScheduledTick && Manager == other.Manager && Slot == other.Slot;
    }

    // This reader is deliberately separate from the existing Chore transport. A
    // sender-less R3 delivery alone does not prove that a native Chore is executing.
    internal static class NativeChoreContext
    {
        // Independently confirmed by local enqueue, receive and due execution in
        // the supported image; a mutable SE resolver field is not sufficient.
        internal const int ManagerRva = 0x8574320;
        private static long moduleStart, moduleEnd;
        private static readonly Func<IntPtr, int> ReadInt = Marshal.ReadInt32;
        private static readonly Func<IntPtr, byte> ReadByte = Marshal.ReadByte;
        internal static void Initialize(long moduleBase, int length, string nativeHash)
        {
            Volatile.Write(ref moduleEnd, 0);
            if (!string.Equals(nativeHash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase) || moduleBase <= 0 || length <= 0)
                throw new InvalidOperationException("Native Chore issuer context unavailable: unsupported image or invalid module range; nativeHash=" + nativeHash);
            moduleStart = moduleBase;
            Volatile.Write(ref moduleEnd, checked(moduleBase + length));
        }
        internal static bool IsReady => Volatile.Read(ref moduleEnd) != 0;

        internal static bool TryCaptureRaw(out NativeChoreExecution execution)
        {
            execution = default;
            if (!TryGetManager(out long manager)) return false;
            return TryRead(manager, ReadInt, ReadByte, out execution);
        }

        internal static bool TryGetManager(out long manager)
        {
            manager = 0;
            long end = Volatile.Read(ref moduleEnd);
            if (end == 0) return false;
            long start = moduleStart;
            long resolved = unchecked((long)GameGlobalsManager.Instance.ChoreManagerVA);
            if (!IsManagerBinding(start, end, resolved)) return false;
            manager = resolved; return true;
        }

        internal static bool IsManagerBinding(long start, long end, long resolved) => start > 0 && end > start &&
            end - start >= ManagerRva + 0x370C00L && resolved == start + ManagerRva;

        internal static bool HasFreeQueueSlot(long manager) => HasFreeQueueSlot(manager, ReadInt, ReadByte);

        // 0x20950 increments the last queue cursor, wraps at 500, and accepts only
        // state zero or signed-char state >=10. If all 500 are active it still
        // returns a slot; 0x23990 would then overwrite that command. Preflight this
        // under the SAME engine monitor immediately before the one native enqueue.
        // Bounded event-time scan only; never a per-frame/object/UI search.
        internal static bool HasFreeQueueSlot(long manager, Func<IntPtr, int> readInt, Func<IntPtr, byte> readByte)
        {
            if (manager <= 0) return false;
            int slot = readInt(new IntPtr(manager + 0x370BF4));
            if (slot < 0 || slot >= 500) return false;
            for (int visited = 0; visited < 500; visited++)
            {
                if (++slot == 500) slot = 0;
                sbyte state = unchecked((sbyte)readByte(new IntPtr(manager + 0xB0BF8 + slot * 0x500L + 9)));
                if (state == 0 || state >= 10) return true;
            }
            return false;
        }

        // Bind the managed dispatcher blob to the exact native pending body. Both
        // length and completed transfer cursor are checked; no serialization or
        // packet-provided source is used to establish this relationship.
        internal static unsafe bool MatchesPayload(NativeChoreExecution captured, byte[] blob)
        {
            if (blob == null || blob.Length < 2 || blob.Length > ChoreTransport.MaximumPayloadBytes ||
                !TryCaptureRaw(out var current) || !captured.Matches(current)) return false;
            long entry = current.Manager + 0xB0BF8 + current.Slot * 0x500L;
            int size = Marshal.ReadInt32(new IntPtr(entry + 17));
            if (size != blob.Length || !MatchesPayloadFields(blob, size,
                    Marshal.ReadInt32(new IntPtr(current.Manager + 0x84CD4)),
                    Marshal.ReadInt32(new IntPtr(current.Manager + 0x370BF8)),
                    new ReadOnlySpan<byte>((void*)(entry + 21), size))) return false;
            return TryCaptureRaw(out var after) && after.Matches(captured);
        }

        internal static bool MatchesPayloadFields(byte[] blob, int declaredSize, int payloadSize, int cursor, ReadOnlySpan<byte> nativeBody) =>
            blob != null && blob.Length >= 2 && blob.Length <= ChoreTransport.MaximumPayloadBytes &&
            declaredSize == blob.Length && payloadSize == declaredSize + 4 && cursor == payloadSize &&
            nativeBody.Length == declaredSize && nativeBody.SequenceEqual(blob);

        // Delegates are only a native-memory test boundary. Offsets follow the
        // hash-bound queue/execute/transfer-field audit, not packet-provided data.
        internal static bool TryRead(long manager, Func<IntPtr, int> readInt, Func<IntPtr, byte> readByte,
            out NativeChoreExecution execution)
        {
            execution = default;
            if (readInt(new IntPtr(manager + 0x84CCC)) != 0) return false;
            int slot = readInt(new IntPtr(manager + 0x84CC8));
            if (slot < 0 || slot >= 500) return false;
            long entry = manager + 0xB0BF8 + slot * 0x500L;
            if (readByte(new IntPtr(entry + 8)) != 106) return false;
            int rawSender = readInt(new IntPtr(entry + 4));
            int sender = readInt(new IntPtr(manager + 0x36FA4C));
            int commandId = readInt(new IntPtr(entry + 12));
            int scheduled = readInt(new IntPtr(entry));
            // The current-image 1F9A0 helper is exactly mov eax,edx; ret.
            // Managed multiplayer remapping has already happened before ReceiveChore.
            // A disagreement here is a changed/foreign context, not another remap.
            if (rawSender < 1 || rawSender > 8 || sender != rawSender || commandId < 0 || scheduled < 0) return false;
            if (readInt(new IntPtr(manager + 0x84CCC)) != 0 || readInt(new IntPtr(manager + 0x84CC8)) != slot ||
                readByte(new IntPtr(entry + 8)) != 106 || readInt(new IntPtr(entry + 4)) != rawSender ||
                readInt(new IntPtr(entry)) != scheduled || readInt(new IntPtr(entry + 12)) != commandId ||
                readInt(new IntPtr(manager + 0x36FA4C)) != sender) return false;
            execution = new NativeChoreExecution(sender, commandId, scheduled, manager, slot);
            return true;
        }
    }
}
