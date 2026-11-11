using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Backends.NativeX64;

namespace APIShared.Audio
{
    internal sealed class UnitVoiceNativeTarget
    {
        internal readonly string Key;
        internal readonly int Rva, Kind, FunctionLength;
        internal readonly byte[] Prologue;
        internal UnitVoiceNativeTarget(string key, int rva, int kind, int length, string bytes)
        { Key = key; Rva = rva; Kind = kind; FunctionLength = length; Prologue = Hex(bytes); }
        private static byte[] Hex(string value)
        { var bytes = new byte[value.Length / 2]; for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16); return bytes; }
    }

    internal static class UnitVoiceNativeContract
    {
        internal const string Hash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const int NativePlayerRva = 0x88E3D70, SelectedCivilianRva = 0x67E839C;
        internal const int SoundManagerRva = 0x88E72D0, SpeechPathOffset = 0x41A6;
        internal static readonly UnitVoiceNativeTarget[] Targets = {
            new UnitVoiceNativeTarget("Troop.Dispatcher", 0x3BC0, 0, 1244, "40535556574883EC78488B05D8473300"),
            new UnitVoiceNativeTarget("Engineer.SpecialActions", 0x3770, 1, 300, "83FA270F8722010000534883EC70"),
            new UnitVoiceNativeTarget("Civilian.Selection", 0x97DA0, 2, 426, "40574883EC3048633DEF05750685FF"),
            new UnitVoiceNativeTarget("Recruitment.European", 0xD5B40, 3, 671, "48895C24084889742410574883EC30"),
            new UnitVoiceNativeTarget("Recruitment.Bedouin", 0xD5DE0, 3, 585, "48895C24084889742410574883EC30"),
            new UnitVoiceNativeTarget("Recruitment.Arabian", 0xD6290, 3, 649, "48895C24084889742410574883EC30"),
            new UnitVoiceNativeTarget("Recruitment.Support", 0xD6030, 3, 594, "48895C241048896C2418564883EC30"),
            new UnitVoiceNativeTarget("Recruitment.Tunneler", 0xD6830, 3, 365, "48895C24084889742410574883EC30"),
            new UnitVoiceNativeTarget("UnitVoice.Output", 0xEDF0, 4, 179, "48895C2408574883EC2083792800")
        };

        internal static void ValidateImage(ReadOnlySpan<byte> original, ReadOnlySpan<byte> live, string hash)
        {
            ValidateIdentity(original, hash);
            foreach (var target in Targets) ValidateEntry(original, live, target);
        }

        internal static void ValidateIdentity(ReadOnlySpan<byte> original, string hash)
        {
            if (!string.Equals(hash, Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unit voice context requires the audited native binary identity.");
            if (original.Length < SoundManagerRva + SpeechPathOffset + 128 || original.Length < SelectedCivilianRva + 4 || original.Length < NativePlayerRva + 4)
                throw new InvalidOperationException("Unit voice native globals are outside the image.");
        }

        internal static void ValidateEntry(ReadOnlySpan<byte> original, ReadOnlySpan<byte> live, UnitVoiceNativeTarget target)
        {
            if (target.Rva < 0 || target.Rva > original.Length - target.Prologue.Length || target.Rva > live.Length - target.Prologue.Length ||
                !original.Slice(target.Rva, target.Prologue.Length).SequenceEqual(target.Prologue) ||
                !live.Slice(target.Rva, target.Prologue.Length).SequenceEqual(target.Prologue))
                throw new InvalidOperationException("Unit voice entry changed or overlaps another hook: " + target.Key);
        }

        // Unlike the simple spatial wrappers, three source entries contain RIP-relative reads,
        // and the engineer entry contains a conditional branch. Validate their relocated targets.
        internal static void ValidateHook(NativeDetour<UnitVoiceNativeRuntime.NativeCall> hook, UnitVoiceNativeTarget target, ulong address)
        {
            if (hook.Scheme != DetourScheme.Absolute || hook.DisplacedByteCount != target.Prologue.Length ||
                hook.TargetAddress != address || hook.PointerSlot != IntPtr.Zero || hook.TrampolineAddress == IntPtr.Zero || hook.HookEntryPointAddress == IntPtr.Zero ||
                hook.OriginalEntryPointAddress != hook.TrampolineAddress)
                throw new InvalidOperationException("Unit voice NativeX64 Absolute identity changed: " + target.Key);
            var bytes = new byte[128]; Marshal.Copy(hook.TrampolineAddress, bytes, 0, bytes.Length);
            var relocated = Decoder.Create(64, new ByteArrayCodeReader(bytes)); relocated.IP = unchecked((ulong)hook.TrampolineAddress.ToInt64());
            var original = Decoder.Create(64, new ByteArrayCodeReader(target.Prologue)); original.IP = address;
            while (original.IP < address + (uint)target.Prologue.Length)
            {
                var before = original.Decode(); var after = relocated.Decode();
                if (before.Mnemonic != after.Mnemonic || before.OpCount != after.OpCount)
                    throw new InvalidOperationException("Unit voice trampoline instruction changed: " + target.Key);
                for (int operand = 0; operand < before.OpCount; operand++)
                {
                    var a = before.GetOpKind(operand); var b = after.GetOpKind(operand);
                    if (a != b || (a == OpKind.Register && before.GetOpRegister(operand) != after.GetOpRegister(operand)))
                        throw new InvalidOperationException("Unit voice trampoline operand changed: " + target.Key);
                    if (a == OpKind.Memory && (before.MemoryBase != after.MemoryBase || before.MemoryIndex != after.MemoryIndex ||
                        before.MemoryIndexScale != after.MemoryIndexScale || before.MemorySize != after.MemorySize ||
                        !before.IsIPRelativeMemoryOperand && before.MemoryDisplacement64 != after.MemoryDisplacement64))
                        throw new InvalidOperationException("Unit voice trampoline memory operand changed: " + target.Key);
                    switch (a)
                    {
                        case OpKind.Immediate8: case OpKind.Immediate8_2nd: case OpKind.Immediate16:
                        case OpKind.Immediate32: case OpKind.Immediate64: case OpKind.Immediate8to16:
                        case OpKind.Immediate8to32: case OpKind.Immediate8to64: case OpKind.Immediate32to64:
                            if (before.GetImmediate(operand) != after.GetImmediate(operand))
                                throw new InvalidOperationException("Unit voice trampoline immediate changed: " + target.Key);
                            break;
                    }
                }
                if (before.SegmentPrefix != after.SegmentPrefix || before.HasLockPrefix != after.HasLockPrefix ||
                    before.HasRepPrefix != after.HasRepPrefix || before.HasRepnePrefix != after.HasRepnePrefix)
                    throw new InvalidOperationException("Unit voice trampoline instruction prefix changed: " + target.Key);
                if ((before.IsIPRelativeMemoryOperand && before.IPRelativeMemoryAddress != after.IPRelativeMemoryAddress) ||
                    (before.Op0Kind == OpKind.NearBranch64 && before.NearBranchTarget != after.NearBranchTarget))
                    throw new InvalidOperationException("Unit voice trampoline relocation changed: " + target.Key);
                if (!before.IsIPRelativeMemoryOperand && before.Op0Kind != OpKind.NearBranch64 && before.Code != after.Code)
                    throw new InvalidOperationException("Unit voice trampoline encoding changed: " + target.Key);
            }
            var continuation = relocated.Decode();
            ulong destination;
            if (continuation.Mnemonic != Mnemonic.Jmp) throw new InvalidOperationException("Unit voice trampoline continuation missing.");
            if (continuation.Op0Kind == OpKind.NearBranch64) destination = continuation.NearBranchTarget;
            else if (continuation.IsIPRelativeMemoryOperand) destination = unchecked((ulong)Marshal.ReadInt64(new IntPtr(unchecked((long)continuation.IPRelativeMemoryAddress))));
            else throw new InvalidOperationException("Unit voice trampoline continuation shape changed.");
            if (destination != address + (uint)target.Prologue.Length) throw new InvalidOperationException("Unit voice trampoline continuation target changed.");
            if (hook.IsInstalled)
            {
                var pointer = new IntPtr(unchecked((long)address));
                if (Marshal.ReadByte(pointer) != 0xFF || Marshal.ReadByte(pointer, 1) != 0x25 ||
                    Marshal.ReadInt32(pointer, 2) != 0 || Marshal.ReadInt64(pointer, 6) != hook.HookEntryPointAddress.ToInt64())
                    throw new InvalidOperationException("Unit voice published Absolute entry changed.");
                for (int i = 14; i < target.Prologue.Length; i++)
                    if (Marshal.ReadByte(pointer, i) != 0x90) throw new InvalidOperationException("Unit voice published padding changed.");
            }
        }
    }
}
