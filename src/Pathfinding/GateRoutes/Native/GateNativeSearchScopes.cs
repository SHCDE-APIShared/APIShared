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
