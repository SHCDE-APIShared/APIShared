using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;

namespace APIShared.NativeSites
{
    // Shared verification of audited Win64 Indirect publication forms.
    internal static class NativeIndirectEntry
    {
        private const int DisplacedLength = 10;
        internal static void VerifyBytes(IntPtr target, byte[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
                if (Marshal.ReadByte(target, i) != expected[i])
                    throw new InvalidOperationException("Native indirect helper changed or is already occupied.");
        }
        internal static void Validate<T>(IDetour<T> hook, IntPtr target, byte[] expected) where T : Delegate =>
            Validate(hook, target, expected, true);
        internal static void Validate<T>(IDetour<T> hook, IntPtr target, byte[] expected, bool installed) where T : Delegate
            => Validate(hook, target, expected, DisplacedLength, installed);

        internal static void Validate<T>(IDetour<T> hook, IntPtr target, byte[] expected,
            int displacedLength, bool installed) where T : Delegate
        {
            if (displacedLength < 6 || displacedLength > expected.Length)
                throw new ArgumentOutOfRangeException(nameof(displacedLength));
            var native = hook as NativeDetour<T>;
            if (native == null || native.IsInstalled != installed || native.Scheme != DetourScheme.Indirect ||
                native.DisplacedByteCount != displacedLength || native.TargetAddress != unchecked((ulong)target.ToInt64()) ||
                native.ChainDepth != 1 || native.PointerSlot == IntPtr.Zero || native.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("Native indirect detour contract mismatch.");
            if (installed && (
                Marshal.ReadByte(target) != 0xFF || Marshal.ReadByte(target, 1) != 0x25 ||
                target + 6 + Marshal.ReadInt32(target, 2) != native.PointerSlot ||
                Marshal.ReadIntPtr(native.PointerSlot) != native.HookEntryPointAddress))
                throw new InvalidOperationException("Native indirect installed detour differs.");
            if (installed)
            {
                for (int i = 6; i < displacedLength; i++)
                    if (Marshal.ReadByte(target, i) != 0x90) throw new InvalidOperationException("Native indirect patch padding changed.");
            }
            else VerifyBytes(target, expected);
            for (int i = 0; i < displacedLength; i++)
                if (Marshal.ReadByte(native.TrampolineAddress, i) != expected[i])
                    throw new InvalidOperationException("Native indirect displaced instructions changed.");
            byte[] jumpBytes = new byte[6];
            Marshal.Copy(native.TrampolineAddress + displacedLength, jumpBytes, 0, jumpBytes.Length);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(jumpBytes));
            decoder.IP = unchecked((ulong)(native.TrampolineAddress + displacedLength).ToInt64());
            decoder.Decode(out Instruction jump);
            if (jump.Mnemonic != Mnemonic.Jmp || jump.Op0Kind != OpKind.NearBranch64 ||
                jump.NearBranchTarget != unchecked((ulong)(target + displacedLength).ToInt64()))
                throw new InvalidOperationException("Native indirect trampoline continuation differs.");
            for (int i = displacedLength; i < expected.Length; i++)
                if (Marshal.ReadByte(target, i) != expected[i]) throw new InvalidOperationException("Native indirect continuation changed.");
        }
    }
}
