using System;
using System.IO;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.PlayerControl
{
    // Assembly primitive for the audited RunTick spectator-input boundary, NOT a
    // permission service. No live hook is installed by constructing this object.
    // Only a validated control context may publish a nonzero actor into its cell.
    // Zero means fully inactive and reenters the original displaced block. A
    // nonzero invalid/mismatched context blocks input; -1 is the suspension state
    // while a view is being restored. Clear to zero only after restoration.
    internal sealed class ControlInputBridge : IDetourIntermediaryFactory
    {
        private readonly ulong actor, nativeView, continuation;
        internal ControlInputBridge(ulong actorCell, ulong nativeView, ulong continuation)
        {
            if (actorCell == 0 || nativeView == 0 || continuation == 0) throw new ArgumentException("Complete input bridge addresses are required.");
            actor = actorCell; this.nativeView = nativeView; this.continuation = continuation;
        }

        public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
        {
            if (context.Architecture != DetourArchitecture.X64 || context.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("Player-control input needs the audited Win64 trampoline contract.");
            ulong original = unchecked((ulong)context.TrampolineAddress.ToInt64());
            var entry = ExecutableFunction.Create(a => Emit(a, actor, nativeView, original, continuation));
            return new DetourIntermediaryStubs(entry.EntryPoint, context.TrampolineAddress, entry);
        }

        // Unlike the writer's two MOV stack stores, this displaced block has a
        // RIP-relative operand. Byte equality at its trampoline would be wrong:
        // verify the decoded destination, exact instruction form and continuation.
        // Call both before publication and after installation. This does not
        // replace the caller's pristine-image/whole-function branch-span audit.
        internal void Validate<T>(IDetour<T> hook, IntPtr target, byte[] expected, bool installed = true) where T : Delegate
        {
            var native = hook as NativeDetour<T>;
            ulong address = unchecked((ulong)target.ToInt64());
            if (expected == null || expected.Length != 14 || native == null || native.IsInstalled != installed ||
                native.Scheme != DetourScheme.Indirect || native.DisplacedByteCount != 10 || native.TargetAddress != address ||
                native.ChainDepth != 1 || native.PointerSlot == IntPtr.Zero || native.TrampolineAddress == IntPtr.Zero ||
                native.HookEntryPointAddress == IntPtr.Zero || native.OriginalEntryPointAddress != native.TrampolineAddress ||
                native.TrampolineSize < 12 || continuation != address + 10)
                throw new InvalidOperationException("Player-control input Indirect/10 boundary changed.");
            ValidateDisplaced(expected, address);
            var original = Decoder.Create(64, new ByteArrayCodeReader(expected)); original.IP = address;
            original.Decode(); original.Decode();
            var next = original.Decode();
            if (next.Code != Code.Movzx_r32_rm8 || next.Op0Register != Register.R8D || next.Op1Register != Register.R14L || next.Length != 4)
                throw new InvalidOperationException("Player-control pristine input continuation changed.");
            if (!installed)
            {
                for (int i = 0; i < 10; i++)
                    if (Marshal.ReadByte(target, i) != expected[i])
                        throw new InvalidOperationException("Player-control input boundary is already occupied.");
            }
            else
            {
                if (Marshal.ReadByte(target) != 0xFF || Marshal.ReadByte(target, 1) != 0x25 ||
                    target + 6 + Marshal.ReadInt32(target, 2) != native.PointerSlot ||
                    Marshal.ReadIntPtr(native.PointerSlot) != native.HookEntryPointAddress)
                    throw new InvalidOperationException("Player-control input patch destination changed.");
                for (int i = 6; i < 10; i++)
                    if (Marshal.ReadByte(target, i) != 0x90) throw new InvalidOperationException("Player-control input padding changed.");
            }
            // A separately owned hook may already begin exactly at +10. The
            // caller checks the original continuation against the pristine image.
            byte first = Marshal.ReadByte(target, 10);
            bool adjacentHook = first == 0xE9 || first == 0xFF && Marshal.ReadByte(target, 11) == 0x25;
            if (!adjacentHook)
                for (int i = 10; i < expected.Length; i++)
                    if (Marshal.ReadByte(target, i) != expected[i])
                        throw new InvalidOperationException("Player-control input continuation changed.");
            var relocated = new byte[Math.Min(native.TrampolineSize, 24)];
            Marshal.Copy(native.TrampolineAddress, relocated, 0, relocated.Length);
            ulong trampoline = unchecked((ulong)native.TrampolineAddress.ToInt64());
            ValidateDisplaced(relocated, trampoline);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(relocated)); decoder.IP = trampoline;
            decoder.Decode(); decoder.Decode();
            var jump = decoder.Decode();
            if (jump.Mnemonic != Mnemonic.Jmp || jump.Op0Kind != OpKind.NearBranch64 || jump.NearBranchTarget != continuation)
                throw new InvalidOperationException("Player-control input trampoline continuation changed.");
            var bridge = new Assembler(64); Emit(bridge, actor, nativeView, trampoline, continuation);
            using (var stream = new MemoryStream())
            {
                bridge.Assemble(new StreamCodeWriter(stream), unchecked((ulong)native.HookEntryPointAddress.ToInt64()));
                NativeSites.NativeIndirectEntry.VerifyBytes(native.HookEntryPointAddress, stream.ToArray());
            }
        }

        private void ValidateDisplaced(byte[] bytes, ulong ip)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = ip;
            var conditional = decoder.Decode(); var store = decoder.Decode();
            if (conditional.Code != Code.Cmovne_r32_rm32 || conditional.Op0Register != Register.EAX ||
                conditional.Op1Register != Register.R12D || conditional.Length != 4 ||
                store.Code != Code.Mov_rm32_r32 || store.Op0Kind != OpKind.Memory || store.Op1Register != Register.EAX ||
                !store.IsIPRelativeMemoryOperand || store.IPRelativeMemoryAddress != nativeView || store.Length != 6 || store.NextIP != ip + 10)
                throw new InvalidOperationException("Player-control input CMOVNE/native-view store changed.");
        }

        // Original input: EAX=EBX=view, R12D=0, flags from the spectator flag's
        // compare. Displaced CMOVNE EAX,R12D and MOV [view],EAX change no flags.
        // We preserve the entire incoming flags and R11; no callback, stack ABI,
        // SIMD scratch or managed state is involved in this frequent path.
        internal static void Emit(Assembler a, ulong actor, ulong nativeView, ulong original, ulong continuation)
        {
            var fallback = a.CreateLabel();
            var blocked = a.CreateLabel();
            var publish = a.CreateLabel();
            a.pushfq(); a.push(r11);
            a.mov(r11, actor);
            // One load: a concurrent actor change cannot mix two states in one decision.
            a.mov(r11d, __dword_ptr[r11]);
            a.test(r11d, r11d); a.je(fallback);
            a.cmp(r11d, ebx); a.jne(blocked);
            a.cmp(ebx, 1); a.jb(blocked);
            a.cmp(ebx, 8); a.ja(blocked);
            a.mov(eax, ebx);
            a.jmp(publish);
            a.Label(ref blocked);
            a.mov(eax, 0);
            a.Label(ref publish);
            a.mov(r11, nativeView); a.mov(__dword_ptr[r11], eax);
            a.pop(r11); a.popfq();
            a.jmp(continuation);
            a.Label(ref fallback);
            a.pop(r11); a.popfq();
            a.jmp(original);
        }
    }
}
