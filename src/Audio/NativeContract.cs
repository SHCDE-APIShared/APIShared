using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace APIShared.Audio
{
    internal static class NativeContract
    {
        internal const string NativeHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const string ManagedHash = "BC8B6A395F01D48557DB413600C8DD8D1FDFD3ABDF97BFBBB68A3C56B04FD789";
        internal const string BackendHash = "0843DD4C381A3E77DD6D8B51D5CCF95465B3FB49BFDF2980F39A212D774AADB0";
        internal const int EmitRva = 0x1A41E0, CurrentUnitRva = 0x9302C4;
        internal const string EmitPattern = "48 63 41 08 4C 8B D9 4C 6B D0 2A 48 8B 01 66 45 89 44 02 02 48 63 41 08 4C 6B C0 2A 48 8B 01 66 45 89 4C 00 04";
        internal const int WrapperRva = 0x2960, FlagWrapperRva = 0x2990;
        internal const string WrapperPattern = "48 83 EC 38 41 8B C1 C7 44 24 20 00 00 00 00 45 8B C8 48 8D 0D ?? ?? ?? ?? 44 8B C2 8B D0 E8 ?? ?? ?? ?? 48 83 C4 38 C3";
        internal const string FlagWrapperPattern = "48 83 EC 38 41 8B C1 C7 44 24 20 01 00 00 00 45 8B C8 48 8D 0D ?? ?? ?? ?? 44 8B C2 8B D0 E8 ?? ?? ?? ?? 48 83 C4 38 C3";
        internal static readonly byte[] WrapperBytes = { 0x48,0x83,0xEC,0x38,0x41,0x8B,0xC1,0xC7,0x44,0x24,0x20,0,0,0,0 };
        internal static readonly byte[] FlagWrapperBytes = { 0x48,0x83,0xEC,0x38,0x41,0x8B,0xC1,0xC7,0x44,0x24,0x20,1,0,0,0 };
        internal static readonly byte[] EmitBytes = { 0x48,0x63,0x41,0x08,0x4C,0x8B,0xD9,0x4C,0x6B,0xD0,0x2A,0x48,0x8B,0x01 };
        // Melee damage routine (rcx=unit base, edx=attacker ID, r8d=victim ID, r9d=damage).
        // Kill cues read their attacker and victim from this routine's frame. In the hash-pinned
        // file, rbp is written only by `movsxd rbp,r8d` and the caller-home slot [rsp+0x70] only by
        // `mov [rsp+0x70],r10` with r10=attacker*0x490; the routine itself reads both again after
        // the cue choice. Live, only what attribution reads is pinned: the frame setup from the end
        // of the 20-byte prologue through that spill (0x199124..0x1991D9) and the four kill CALLs.
        // Entry detours (Script Extender's melee-damage event: Indirect, 0x199110..0x199115) and
        // frame-preserving mid-body hooks (its melee-kill context hook, 0x199444..0x199455) stay
        // outside the pins, because their trampolines replay the displaced instructions unchanged.
        internal const int MeleeDamageRva = 0x199110, MeleeDamageEndRva = 0x19999D;
        internal const int MeleeFrameRva = 0x199124, MeleeFrameLength = 0xB6;
        internal const string MeleeFrameHash = "6BC17563B008BD6123FFB6FA7C3C9FB308D66AD4B96C61B2EC422A23E9500295";
        internal static readonly byte[] MeleePrologueBytes = { 0x48,0x89,0x5C,0x24,0x18,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x83,0xEC,0x30 };
        internal const int MeleeVictimRbpRva = 0x199124, MeleeAttackerSlotRva = 0x1991D5;
        internal static readonly byte[] MeleeVictimRbpBytes = { 0x49, 0x63, 0xE8 };
        internal static readonly byte[] MeleeAttackerSlotBytes = { 0x4C, 0x89, 0x54, 0x24, 0x70 };
        // Entry RSP + 8 is the routine's RSP after `sub rsp,0x30`; +0x70 is the spilled attacker offset.
        internal const int MeleeAttackerFrameOffset = 0x78;

        internal static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        // suspendThreads=false is for private test copies no other thread can execute (RedBird's
        // thread-suspending patch destabilises the desktop CLR test host: crash at a later GC) and for
        // the synchronous startup run (NativeStartupPatchWindow).
        internal static NativeDetourBackend Backend(int size, bool suspendThreads = true)
        {
            var options = new NativeDetourOptions
            {
                AllowedSchemes = DetourScheme.Absolute, FollowCalls = false, FollowJumps = false,
                Chaining = HookChainingPolicy.Refuse, FunctionScanLimit = size
            };
            return new NativeDetourBackend(suspendThreads ? options : options with { SuspendThreadsDuringPatch = false });
        }

        internal static void Validate<T>(NativeDetour<T> hook, int length, ulong address, byte[] expected = null) where T : Delegate
        {
            if (hook.Scheme != DetourScheme.Absolute || hook.DisplacedByteCount != length ||
                hook.TargetAddress != address || hook.PointerSlot != IntPtr.Zero ||
                hook.HookEntryPointAddress == IntPtr.Zero || hook.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("NativeX64 Absolute contract mismatch at 0x" + address.ToString("X"));
            // Neither displaced prologue contains RIP operands or branches, so the
            // trampoline must preserve it verbatim and resume at the audited boundary.
            expected = expected ?? (length == 15 ? WrapperBytes : length == 14 ? EmitBytes : throw new InvalidOperationException("Unknown audio displacement."));
            for (int i = 0; i < length; i++)
                if (Marshal.ReadByte(hook.TrampolineAddress, i) != expected[i]) throw new InvalidOperationException("Audio trampoline prologue changed.");
            byte[] jumpBytes = new byte[14];
            Marshal.Copy(hook.TrampolineAddress + length, jumpBytes, 0, jumpBytes.Length);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(jumpBytes));
            decoder.IP = unchecked((ulong)(hook.TrampolineAddress + length).ToInt64());
            decoder.Decode(out Instruction jump);
            ulong continuation;
            if (jump.Mnemonic != Mnemonic.Jmp) throw new InvalidOperationException("Audio trampoline continuation is not a jump.");
            if (jump.Op0Kind == OpKind.NearBranch64) continuation = jump.NearBranchTarget;
            else if (jump.Op0Kind == OpKind.Memory && jump.IsIPRelativeMemoryOperand)
                continuation = unchecked((ulong)Marshal.ReadInt64(new IntPtr(unchecked((long)jump.IPRelativeMemoryAddress))));
            else throw new InvalidOperationException("Unsupported audio trampoline jump.");
            if (continuation != address + (uint)length) throw new InvalidOperationException("Audio trampoline resumes outside audited boundary.");
            if (!hook.IsInstalled) return;
            byte[] bytes = new byte[length];
            Marshal.Copy(new IntPtr(unchecked((long)address)), bytes, 0, length);
            if (bytes[0] != 0xFF || bytes[1] != 0x25 || BitConverter.ToInt32(bytes, 2) != 0 ||
                BitConverter.ToInt64(bytes, 6) != hook.HookEntryPointAddress.ToInt64())
                throw new InvalidOperationException("Published Absolute patch mismatch.");
            for (int i = 14; i < length; i++) if (bytes[i] != 0x90)
                throw new InvalidOperationException("Published Absolute padding mismatch.");
        }
    }
}
