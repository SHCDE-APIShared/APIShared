using System;
using System.Runtime.InteropServices;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
namespace APIShared
{
    internal static class AssassinAttackNativeContract
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void UpdateDelegate();
        // 122800 takes tribe-manager base, one-based unit ID and signed objective role (+2FE).
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int RetargetDelegate(IntPtr tribeManager, int unitId, int objectiveRole);
        internal static void ValidateRetargetEntry(IntPtr target)
        {
            byte[] expected = { 0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x6C,0x24,0x10,0x48,0x89,0x74,0x24,0x18,
                0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57 };
            for (int i=0;i<expected.Length;i++)
                if (Marshal.ReadByte(target,i) != expected[i])
                    throw new InvalidOperationException("Vanilla Assassin retarget entry changed or occupied.");
        }
        internal static readonly NativeDetourBackend Backend=new NativeDetourBackend(
            new NativeDetourOptions { AllowedSchemes=DetourScheme.Indirect, FollowJumps=false });
        internal static void Validate(IDetour<UpdateDelegate> hook,IntPtr target)
        {
            var native=hook as NativeDetour<UpdateDelegate>;
            if(native==null || !native.IsInstalled || native.Scheme!=DetourScheme.Indirect || native.DisplacedByteCount!=10 ||
                native.TargetAddress!=unchecked((ulong)target.ToInt64()) || native.PointerSlot==IntPtr.Zero ||
                Marshal.ReadByte(target)!=0xFF || Marshal.ReadByte(target,1)!=0x25)
                throw new InvalidOperationException("Assassin update NativeX64 Indirect/10 contract mismatch.");
            int delta=Marshal.ReadInt32(target,2);
            if(target+6+delta!=native.PointerSlot || Marshal.ReadIntPtr(native.PointerSlot)!=native.HookEntryPointAddress)
                throw new InvalidOperationException("Assassin update pointer-slot/entry mismatch.");
        }
    }
}
