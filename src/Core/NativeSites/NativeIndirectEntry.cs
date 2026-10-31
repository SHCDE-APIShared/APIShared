using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;

namespace APIShared.NativeSites
{
    // Shared verification of the audited Win64 Indirect/10 publication form.
    internal static class NativeIndirectEntry
    {
        private const int DisplacedLength = 10;
        internal static void VerifyBytes(IntPtr target, byte[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
                if (Marshal.ReadByte(target, i) != expected[i])
                    throw new InvalidOperationException("Native indirect helper changed or is already occupied.");
        }
        internal static void Validate<T>(IDetour<T> hook, IntPtr target, byte[] expected) where T : Delegate
        {
            var native = hook as NativeDetour<T>;
            if (native == null || !native.IsInstalled || native.Scheme != DetourScheme.Indirect ||
                native.DisplacedByteCount != DisplacedLength || native.TargetAddress != unchecked((ulong)target.ToInt64()) ||
                native.ChainDepth != 1 || native.PointerSlot == IntPtr.Zero || native.TrampolineAddress == IntPtr.Zero ||
                Marshal.ReadByte(target) != 0xFF || Marshal.ReadByte(target, 1) != 0x25 ||
                target + 6 + Marshal.ReadInt32(target, 2) != native.PointerSlot ||
                Marshal.ReadIntPtr(native.PointerSlot) != native.HookEntryPointAddress)
                throw new InvalidOperationException("Native indirect Indirect/10 detour contract mismatch.");
            for (int i = 6; i < DisplacedLength; i++)
                if (Marshal.ReadByte(target, i) != 0x90) throw new InvalidOperationException("Native indirect patch padding changed.");
            for (int i = 0; i < DisplacedLength; i++)
                if (Marshal.ReadByte(native.TrampolineAddress, i) != expected[i])
                    throw new InvalidOperationException("Native indirect displaced instructions changed.");
            byte[] jumpBytes = new byte[6];
            Marshal.Copy(native.TrampolineAddress + DisplacedLength, jumpBytes, 0, jumpBytes.Length);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(jumpBytes));
            decoder.IP = unchecked((ulong)(native.TrampolineAddress + DisplacedLength).ToInt64());
            decoder.Decode(out Instruction jump);
            if (jump.Mnemonic != Mnemonic.Jmp || jump.Op0Kind != OpKind.NearBranch64 ||
                jump.NearBranchTarget != unchecked((ulong)(target + DisplacedLength).ToInt64()))
                throw new InvalidOperationException("Native indirect trampoline must resume at the audited +10 instruction.");
            for (int i = DisplacedLength; i < expected.Length; i++)
                if (Marshal.ReadByte(target, i) != expected[i]) throw new InvalidOperationException("Native indirect continuation changed.");
        }
    }
}
