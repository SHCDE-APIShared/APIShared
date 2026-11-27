using System;
using System.Collections.Generic;
using System.IO;
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
            // RedBird re-encodes decoded instructions rather than copying their bytes.
            // A redundant REX before PUSH can disappear, changing the trampoline's
            // payload length without changing the original displaced span or resume.
            byte[] trampoline = EncodeTrampoline(unchecked((ulong)target.ToInt64()), expected,
                displacedLength, unchecked((ulong)native.TrampolineAddress.ToInt64()));
            if (native.TrampolineSize != trampoline.Length)
                throw new InvalidOperationException("Native indirect trampoline size differs.");
            for (int i = 0; i < trampoline.Length; i++)
                if (Marshal.ReadByte(native.TrampolineAddress, i) != trampoline[i])
                    throw new InvalidOperationException("Native indirect re-encoded trampoline changed.");
            for (int i = displacedLength; i < expected.Length; i++)
                if (Marshal.ReadByte(target, i) != expected[i]) throw new InvalidOperationException("Native indirect continuation changed.");
        }

        private static byte[] EncodeTrampoline(ulong target, byte[] expected, int displacedLength, ulong destination)
        {
            var bytes = new byte[displacedLength];
            Array.Copy(expected, bytes, bytes.Length);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes));
            decoder.IP = target;
            var instructions = new List<Instruction>();
            ulong resume = checked(target + (uint)displacedLength);
            while (decoder.IP < resume)
            {
                var instruction = decoder.Decode();
                if (instruction.Code == Code.INVALID || decoder.IP > resume)
                    throw new InvalidOperationException("Native indirect original instruction boundary differs.");
                instructions.Add(instruction);
            }
            instructions.Add(Instruction.CreateBranch(Code.Jmp_rel32_64, resume));
            using (var stream = new MemoryStream())
            {
                // Exact installed TrampolineBuilder algorithm, derived solely from
                // audited original bytes; live trampoline bytes never approve themselves.
                var block = new InstructionBlock(new StreamCodeWriter(stream), instructions, destination);
                if (!BlockEncoder.TryEncode(64, block, out string error, out _, BlockEncoderOptions.ReturnNewInstructionOffsets))
                    throw new InvalidOperationException("Native indirect trampoline encoding failed: " + error);
                return stream.ToArray();
            }
        }
    }
}
