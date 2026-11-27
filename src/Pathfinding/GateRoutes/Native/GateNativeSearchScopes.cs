using System;
using System.Runtime.InteropServices;
using System.Threading;
namespace APIShared.GateRouting.Native
{
    // Shared immediate scope storage. Process-lived data, not executable memory.
    /// <summary>Advanced backend scope data, separate from executable hooks. Thread slots and masks
    /// must remain allocated through process exit; masks are immutable 320800-byte direction arrays.
    /// Enter/Leave must pair on the same synchronous native thread, including exceptions.
    /// Ordinary contributors use GateAccessRules and never allocate native buffers.</summary>
    public static unsafe class GateNativeSearchScopes
    {
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        // Slot layout of the buffers below, for a provider that reads its own scope results. A slot is
        // selected by (native thread ID & (count - 1)) * stride. Run-time values, not compiled-in constants.
        /// <summary>Number of generic search slots (a power of two).</summary>
        public static int GenericSlotCount => DirectionFilterAdapterEmitter.ThreadSlotCount;
        /// <summary>Byte stride of one generic search slot.</summary>
        public static int GenericSlotStride => 1 << DirectionFilterAdapterEmitter.ThreadSlotStrideShift;
        /// <summary>Offset of the Int32 nesting depth in a generic slot; non-zero while a search is active.</summary>
        public static int GenericSlotDepthOffset => DirectionFilterAdapterEmitter.SlotDepthOffset;
        /// <summary>Number of tactical slots (a power of two).</summary>
        public static int TacticalSlotCount => AiTacticalTargetAdapterEmitter.ThreadSlotCount;
        /// <summary>Byte stride of one tactical slot.</summary>
        public static int TacticalSlotStride => AiTacticalTargetAdapterEmitter.ThreadSlotStride;
        /// <summary>Offset of the Int32 nesting depth in a tactical slot; non-zero while a tactical query is active.</summary>
        public static int TacticalSlotDepthOffset => AiTacticalTargetAdapterEmitter.SlotDepthOffset;
        /// <summary>Offset of the Int64 rejected-edge counter of the building search.</summary>
        public static int TacticalSlotBuildingTouchedOffset => AiTacticalTargetAdapterEmitter.SlotBuildingTouchedOffset;
        /// <summary>Offset of the Int64 rejected-edge counter of the unit search.</summary>
        public static int TacticalSlotUnitTouchedOffset => AiTacticalTargetAdapterEmitter.SlotUnitTouchedOffset;
        /// <summary>Offset of the Int64 rejected-edge counter of the fallback tile search.</summary>
        public static int TacticalSlotFallbackTouchedOffset => AiTacticalTargetAdapterEmitter.SlotFallbackTouchedOffset;
        /// <summary>Offset of the Int32 source tile of a sampled rejected building-search edge.</summary>
        public static int TacticalSlotBuildingSourceOffset => AiTacticalTargetAdapterEmitter.SlotBuildingSourceOffset;
        /// <summary>Offset of the Int32 target tile of a sampled rejected building-search edge.</summary>
        public static int TacticalSlotBuildingTargetOffset => AiTacticalTargetAdapterEmitter.SlotBuildingTargetOffset;
        /// <summary>Offset of the Int32 direction of a sampled rejected building-search edge.</summary>
        public static int TacticalSlotBuildingDirectionOffset => AiTacticalTargetAdapterEmitter.SlotBuildingDirectionOffset;
        /// <summary>Offset of the Int32 source tile of a sampled rejected unit-search edge.</summary>
        public static int TacticalSlotUnitSourceOffset => AiTacticalTargetAdapterEmitter.SlotUnitSourceOffset;
        /// <summary>Offset of the Int32 target tile of a sampled rejected unit-search edge.</summary>
        public static int TacticalSlotUnitTargetOffset => AiTacticalTargetAdapterEmitter.SlotUnitTargetOffset;
        /// <summary>Offset of the Int32 direction of a sampled rejected unit-search edge.</summary>
        public static int TacticalSlotUnitDirectionOffset => AiTacticalTargetAdapterEmitter.SlotUnitDirectionOffset;
        /// <summary>Offset of the Int32 source tile of a sampled rejected fallback-search edge.</summary>
        public static int TacticalSlotFallbackSourceOffset => AiTacticalTargetAdapterEmitter.SlotFallbackSourceOffset;
        /// <summary>Offset of the Int32 target tile of a sampled rejected fallback-search edge.</summary>
        public static int TacticalSlotFallbackTargetOffset => AiTacticalTargetAdapterEmitter.SlotFallbackTargetOffset;
        /// <summary>Offset of the Int32 direction of a sampled rejected fallback-search edge.</summary>
        public static int TacticalSlotFallbackDirectionOffset => AiTacticalTargetAdapterEmitter.SlotFallbackDirectionOffset;
        /// <summary>Allocates zeroed permanent data; generic search slots need 2048 bytes, tactical slots 8192 bytes. The caller retains ownership for process lifetime.</summary>
        public static IntPtr Allocate(int length) {var p=Marshal.AllocHGlobal(length);for(int i=0;i<length;i++)((byte*)p)[i]=0;return p;}
        /// <summary>Enters a nested generic search; null means thread-slot collision and must preserve native behavior. Mask zero is explicitly unfiltered.</summary>
        public static byte* Enter(IntPtr slots,IntPtr mask,out IntPtr previous,out long previousTouched)
        {
            previous=IntPtr.Zero;previousTouched=0;
            uint thread=GetCurrentThreadId();byte* slot=(byte*)slots+((thread&63)*32);
            int owner=Volatile.Read(ref *(int*)slot);
            if(owner!=0&&owner!=unchecked((int)thread))return null;
            if(owner==0&&Interlocked.CompareExchange(ref *(int*)slot,unchecked((int)thread),0)!=0)return null;
            previous=*(IntPtr*)(slot+8);previousTouched=*(long*)(slot+16);
            (*(int*)(slot+24))++;*(IntPtr*)(slot+8)=mask;*(long*)(slot+16)=0;return slot;
        }
        /// <summary>Restores the exact preceding mask/counter and returns this search's rejected-edge count.</summary>
        public static long Leave(byte* slot,IntPtr previous,long touched)
        {
            if(slot==null)return 0;long result=*(long*)(slot+16);
            *(IntPtr*)(slot+8)=previous;*(long*)(slot+16)=touched;
            if(--(*(int*)(slot+24))==0)Volatile.Write(ref *(int*)slot,0);return result;
        }
        /// <summary>Enters a tactical scope. Nested/colliding owners return null without disturbing the outer scope.</summary>
        public static byte* EnterTactical(IntPtr slots, IntPtr mask)
        {
            uint thread = GetCurrentThreadId();
            byte* slot = (byte*)slots + ((thread &
                (AiTacticalTargetAdapterEmitter.ThreadSlotCount - 1)) *
                AiTacticalTargetAdapterEmitter.ThreadSlotStride);
            int owner = Volatile.Read(ref *(int*)(slot +
                AiTacticalTargetAdapterEmitter.SlotOwnerOffset));
            if (owner != 0 || (owner == 0 && Interlocked.CompareExchange(
                    ref *(int*)(slot + AiTacticalTargetAdapterEmitter.SlotOwnerOffset),
                    unchecked((int)thread), 0) != 0))
            {
                return null;
            }
            *(IntPtr*)(slot + AiTacticalTargetAdapterEmitter.SlotMaskOffset) = mask;
            *(long*)(slot + AiTacticalTargetAdapterEmitter.SlotBuildingTouchedOffset) = 0;
            *(long*)(slot + AiTacticalTargetAdapterEmitter.SlotUnitTouchedOffset) = 0;
            *(long*)(slot + AiTacticalTargetAdapterEmitter.SlotFallbackTouchedOffset) = 0;
            *(int*)(slot + AiTacticalTargetAdapterEmitter.SlotDepthOffset) = 1;
            return slot;
        }
        /// <summary>Clears a successfully entered tactical scope after its synchronous native call.</summary>
        public static void LeaveTactical(byte* slot)
        {
                *(IntPtr*)(slot + AiTacticalTargetAdapterEmitter.SlotMaskOffset) = IntPtr.Zero;
                *(int*)(slot + AiTacticalTargetAdapterEmitter.SlotDepthOffset) = 0;
                Volatile.Write(ref *(int*)(slot +
                    AiTacticalTargetAdapterEmitter.SlotOwnerOffset), 0);
        }
    }
}
