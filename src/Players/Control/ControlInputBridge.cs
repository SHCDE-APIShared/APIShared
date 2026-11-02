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
    // The two audited places where Vanilla replaces the native view with zero for a
    // spectator before map input is processed (see UpdateToNewDLL.md).
    internal enum ControlInputSite
    {
        // DLL_RunTick: CMOVNE EAX,R12D + MOV [view],EAX are displaced (Indirect/10);
        // the stub stores the view itself and resumes at MOVZX R8D,R14B.
        RunTick,
        // Nested re-dispatch in the input dispatcher: CMOVNE EAX,EDI + XOR R9D,R9D are
        // displaced (Indirect/6); the unchanged MOV [view],EAX at +6 stores the stub's EAX.
        Dispatcher
    }

    // Assembly primitive for the audited spectator-input boundaries, NOT a permission
    // service. Only LocalInputControl publishes a nonzero actor into its cell. Zero, or a
    // clear spectator comparison, reenters the original displaced block. For a spectator, a
    // nonzero actor that is not the current view in 1..8 (including -1) keeps view 0.
    internal sealed class ControlInputBridge : IDetourIntermediaryFactory
    {
        private readonly ControlInputSite site;
        private readonly ulong actor, nativeView, continuation;

        internal ControlInputBridge(ulong actorCell, ulong nativeView, ulong continuation)
            : this(ControlInputSite.RunTick, actorCell, nativeView, continuation) { }

        internal ControlInputBridge(ControlInputSite site, ulong actorCell, ulong nativeView, ulong continuation)
        {
            if (site != ControlInputSite.RunTick && site != ControlInputSite.Dispatcher) throw new ArgumentOutOfRangeException(nameof(site));
            if (actorCell == 0 || nativeView == 0 || continuation == 0) throw new ArgumentException("Complete input bridge addresses are required.");
            this.site = site; actor = actorCell; this.nativeView = nativeView; this.continuation = continuation;
        }

        internal static int DisplacedLength(ControlInputSite site) => site == ControlInputSite.RunTick ? 10 : 6;

        // Displaced instructions plus the complete continuation instruction.
        internal static int WindowLength(ControlInputSite site) => site == ControlInputSite.RunTick ? 14 : 12;

        public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
        {
            if (context.Architecture != DetourArchitecture.X64 || context.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("Player-control input needs the audited Win64 trampoline contract.");
            ulong original = unchecked((ulong)context.TrampolineAddress.ToInt64());
            var entry = ExecutableFunction.Create(a => Emit(a, site, actor, nativeView, original, continuation));
            return new DetourIntermediaryStubs(entry.EntryPoint, context.TrampolineAddress, entry);
        }

        // The displaced block can contain a RIP-relative operand, so byte equality at the
        // trampoline would be wrong: verify the decoded destination, exact instruction form
        // and continuation. Call both before publication and after installation. This does
        // not replace the caller's pristine-image/whole-function branch-span audit.
        internal void Validate<T>(IDetour<T> hook, IntPtr target, byte[] expected, bool installed = true) where T : Delegate
        {
            var native = hook as NativeDetour<T>;
            ulong address = unchecked((ulong)target.ToInt64());
            int displaced = DisplacedLength(site);
            if (expected == null || expected.Length != WindowLength(site) || native == null || native.IsInstalled != installed ||
                native.Scheme != DetourScheme.Indirect || native.DisplacedByteCount != displaced || native.TargetAddress != address ||
                native.ChainDepth != 1 || native.PointerSlot == IntPtr.Zero || native.TrampolineAddress == IntPtr.Zero ||
                native.HookEntryPointAddress == IntPtr.Zero || native.OriginalEntryPointAddress != native.TrampolineAddress ||
                native.TrampolineSize < displaced + 2 || continuation != address + (ulong)displaced)
                throw new InvalidOperationException("Player-control input Indirect/" + displaced + " boundary changed.");
            ValidateDisplaced(expected, address);
            ValidateContinuation(expected, address);
            if (!installed)
            {
                for (int i = 0; i < displaced; i++)
                    if (Marshal.ReadByte(target, i) != expected[i])
                        throw new InvalidOperationException("Player-control input boundary is already occupied.");
            }
            else
            {
                if (Marshal.ReadByte(target) != 0xFF || Marshal.ReadByte(target, 1) != 0x25 ||
                    target + 6 + Marshal.ReadInt32(target, 2) != native.PointerSlot ||
                    Marshal.ReadIntPtr(native.PointerSlot) != native.HookEntryPointAddress)
                    throw new InvalidOperationException("Player-control input patch destination changed.");
                for (int i = 6; i < displaced; i++)
                    if (Marshal.ReadByte(target, i) != 0x90) throw new InvalidOperationException("Player-control input padding changed.");
            }
            // A separately owned hook may already begin exactly at the continuation. The
            // caller checks the original continuation against the pristine image.
            byte first = Marshal.ReadByte(target, displaced);
            bool adjacentHook = first == 0xE9 || first == 0xFF && Marshal.ReadByte(target, displaced + 1) == 0x25;
            if (!adjacentHook)
                for (int i = displaced; i < expected.Length; i++)
                    if (Marshal.ReadByte(target, i) != expected[i])
                        throw new InvalidOperationException("Player-control input continuation changed.");
            var relocated = new byte[Math.Min(native.TrampolineSize, displaced + 14)];
            Marshal.Copy(native.TrampolineAddress, relocated, 0, relocated.Length);
            ulong trampoline = unchecked((ulong)native.TrampolineAddress.ToInt64());
            ValidateDisplaced(relocated, trampoline);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(relocated)); decoder.IP = trampoline;
            decoder.Decode(); decoder.Decode();
            var jump = decoder.Decode();
            if (jump.Mnemonic != Mnemonic.Jmp || jump.Op0Kind != OpKind.NearBranch64 || jump.NearBranchTarget != continuation)
                throw new InvalidOperationException("Player-control input trampoline continuation changed.");
            var bridge = new Assembler(64); Emit(bridge, site, actor, nativeView, trampoline, continuation);
            using (var stream = new MemoryStream())
            {
                bridge.Assemble(new StreamCodeWriter(stream), unchecked((ulong)native.HookEntryPointAddress.ToInt64()));
                NativeSites.NativeIndirectEntry.VerifyBytes(native.HookEntryPointAddress, stream.ToArray());
            }
        }

        private void ValidateDisplaced(byte[] bytes, ulong ip)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = ip;
            var conditional = decoder.Decode(); var second = decoder.Decode();
            bool valid = site == ControlInputSite.RunTick
                ? conditional.Code == Code.Cmovne_r32_rm32 && conditional.Op0Register == Register.EAX &&
                  conditional.Op1Register == Register.R12D && conditional.Length == 4 &&
                  second.Code == Code.Mov_rm32_r32 && second.Op0Kind == OpKind.Memory && second.Op1Register == Register.EAX &&
                  second.IsIPRelativeMemoryOperand && second.IPRelativeMemoryAddress == nativeView && second.Length == 6 &&
                  second.NextIP == ip + 10
                : conditional.Code == Code.Cmovne_r32_rm32 && conditional.Op0Register == Register.EAX &&
                  conditional.Op1Register == Register.EDI && conditional.Length == 3 &&
                  second.Code == Code.Xor_r32_rm32 && second.Op0Register == Register.R9D && second.Op1Register == Register.R9D &&
                  second.Length == 3 && second.NextIP == ip + 6;
            if (!valid) throw new InvalidOperationException("Player-control input CMOVNE/native-view block changed.");
        }

        // RunTick resumes at MOVZX R8D,R14B. The dispatcher resumes at the native-view store,
        // which must still write EAX into the audited cell.
        private void ValidateContinuation(byte[] window, ulong ip)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(window)); decoder.IP = ip;
            decoder.Decode(); decoder.Decode();
            var next = decoder.Decode();
            bool valid = site == ControlInputSite.RunTick
                ? next.Code == Code.Movzx_r32_rm8 && next.Op0Register == Register.R8D && next.Op1Register == Register.R14L && next.Length == 4
                : next.Code == Code.Mov_rm32_r32 && next.Op0Kind == OpKind.Memory && next.Op1Register == Register.EAX &&
                  next.IsIPRelativeMemoryOperand && next.IPRelativeMemoryAddress == nativeView && next.Length == 6;
            if (!valid) throw new InvalidOperationException("Player-control pristine input continuation changed.");
        }

        // RunTick form, kept for the existing fixtures.
        internal static void Emit(Assembler a, ulong actor, ulong nativeView, ulong original, ulong continuation) =>
            Emit(a, ControlInputSite.RunTick, actor, nativeView, original, continuation);

        // Original input: EAX=EBX=view, the zero register is 0, flags come from the spectator
        // flag's compare. The displaced instructions change no flags. We preserve the entire
        // incoming flags and R11; no callback, stack ABI, SIMD scratch or managed state is
        // involved in this frequent path. The dispatcher form also performs the displaced
        // XOR R9D,R9D and leaves the store to the unchanged continuation.
        internal static void Emit(Assembler a, ControlInputSite site, ulong actor, ulong nativeView, ulong original, ulong continuation)
        {
            var fallback = a.CreateLabel();
            var blocked = a.CreateLabel();
            var publish = a.CreateLabel();
            a.pushfq(); a.push(r11);
            // ZF set: the spectator flag is zero, so Vanilla keeps the view anyway. A non-spectator's
            // input never depends on the actor cell, even during a stale actor window.
            a.jz(fallback);
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
            if (site == ControlInputSite.RunTick) { a.mov(r11, nativeView); a.mov(__dword_ptr[r11], eax); }
            else a.xor(r9d, r9d);
            a.pop(r11); a.popfq();
            a.jmp(continuation);
            a.Label(ref fallback);
            a.pop(r11); a.popfq();
            a.jmp(original);
        }
    }
}
