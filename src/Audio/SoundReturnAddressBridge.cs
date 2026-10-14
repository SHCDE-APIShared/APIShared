using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Audio
{
    // A void four-argument Win64 wrapper needs no incoming flags or volatile SIMD.
    // Dormant: the demand guard tail-jumps to the trampoline. Active: pass the entry RSP as
    // argument five ([entry RSP] is the original return address) and spill the caller's RBP
    // into this bridge's own frame at entry RSP-0x10, which a five-argument callee never owns.
    internal sealed class SoundReturnAddressBridge : IDetourIntermediaryFactory
    {
        internal const int CallerRbpBelowFrame = 0x10;
        private readonly ulong callback, demand;
        internal SoundReturnAddressBridge(Delegate value, IntPtr demand)
        {
            callback = unchecked((ulong)Marshal.GetFunctionPointerForDelegate(value).ToInt64());
            this.demand = unchecked((ulong)demand.ToInt64());
            if (demand == IntPtr.Zero) throw new ArgumentException("Audio bridge requires a demand byte.", nameof(demand));
        }
        public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
        {
            if (context.Architecture != DetourArchitecture.X64 || context.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("Audio bridge requires the audited Win64 backend.");
            ulong trampoline = unchecked((ulong)context.TrampolineAddress.ToInt64());
            var entry = ExecutableFunction.Create(asm => {
                var active = SoundBridgeCode.EmitGuard(asm, demand, trampoline);
                asm.Label(ref active);
                asm.sub(rsp, 0x38);
                asm.lea(rax, __qword_ptr[rsp + 0x38]);
                asm.mov(__qword_ptr[rsp + 0x20], rax);
                asm.mov(__qword_ptr[rsp + 0x38 - CallerRbpBelowFrame], rbp);
                asm.mov(rax, callback);
                asm.call(rax);
                asm.add(rsp, 0x38);
                asm.ret();
            });
            return new DetourIntermediaryStubs(entry.EntryPoint, context.TrampolineAddress, entry);
        }
        internal void Validate(NativeDetour<SoundRuntime.Wrapper> hook)
        {
            if (hook.OriginalEntryPointAddress != hook.TrampolineAddress || hook.HookEntryPointAddress == new IntPtr(unchecked((long)callback)))
                throw new InvalidOperationException("Audio intermediary/trampoline identity changed.");
            var decoder = SoundBridgeCode.Open(hook.HookEntryPointAddress);
            SoundBridgeCode.ValidateGuard(decoder, demand, unchecked((ulong)hook.TrampolineAddress.ToInt64()));
            var a = decoder.Decode(); var b = decoder.Decode(); var c = decoder.Decode(); var d = decoder.Decode();
            var e = decoder.Decode(); var f = decoder.Decode(); var g = decoder.Decode(); var h = decoder.Decode();
            if (a.Mnemonic != Mnemonic.Sub || a.Op0Register != Register.RSP || a.Immediate8 != 0x38 ||
                b.Mnemonic != Mnemonic.Lea || b.Op0Register != Register.RAX || b.MemoryBase != Register.RSP || b.MemoryDisplacement64 != 0x38 ||
                c.Mnemonic != Mnemonic.Mov || c.MemoryBase != Register.RSP || c.MemoryDisplacement64 != 0x20 || c.Op1Register != Register.RAX ||
                d.Mnemonic != Mnemonic.Mov || d.MemoryBase != Register.RSP || d.MemoryDisplacement64 != 0x38 - CallerRbpBelowFrame || d.Op1Register != Register.RBP ||
                e.Mnemonic != Mnemonic.Mov || e.Op0Register != Register.RAX || e.Immediate64 != callback ||
                f.Mnemonic != Mnemonic.Call || f.Op0Register != Register.RAX ||
                g.Mnemonic != Mnemonic.Add || g.Op0Register != Register.RSP || g.Immediate8 != 0x38 || h.Mnemonic != Mnemonic.Ret)
                throw new InvalidOperationException("Audio return-address bridge ABI changed.");
        }
    }

    // Shared dormant guard: `mov rax,demand; cmp byte [rax],0; jne active; mov rax,trampoline;
    // jmp rax`. Argument registers, RSP and the return address stay untouched; RAX is scratch
    // because neither displaced prologue reads it before writing it (wrapper `mov eax,r9d`,
    // emitter `movsxd rax,[rcx+8]`).
    internal static class SoundBridgeCode
    {
        internal static Label EmitGuard(Assembler asm, ulong demand, ulong trampoline)
        {
            var active = asm.CreateLabel();
            asm.mov(rax, demand);
            asm.cmp(__byte_ptr[rax], 0);
            asm.jne(active);
            asm.mov(rax, trampoline);
            asm.jmp(rax);
            return active;
        }
        internal static Decoder Open(IntPtr entry)
        {
            var bytes = new byte[128]; Marshal.Copy(entry, bytes, 0, bytes.Length);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes));
            decoder.IP = unchecked((ulong)entry.ToInt64());
            return decoder;
        }
        // The active path must start right after the guard; the decoder stays positioned there.
        internal static void ValidateGuard(Decoder decoder, ulong demand, ulong trampoline)
        {
            var a = decoder.Decode(); var b = decoder.Decode(); var c = decoder.Decode(); var d = decoder.Decode(); var e = decoder.Decode();
            if (a.Mnemonic != Mnemonic.Mov || a.Op0Register != Register.RAX || a.Immediate64 != demand ||
                b.Mnemonic != Mnemonic.Cmp || b.MemoryBase != Register.RAX || b.MemorySize != MemorySize.UInt8 || b.MemoryDisplacement64 != 0 || b.Immediate8 != 0 ||
                c.Mnemonic != Mnemonic.Jne || (c.Op0Kind != OpKind.NearBranch64) ||
                d.Mnemonic != Mnemonic.Mov || d.Op0Register != Register.RAX || d.Immediate64 != trampoline ||
                e.Mnemonic != Mnemonic.Jmp || e.Op0Register != Register.RAX || c.NearBranchTarget != e.NextIP)
                throw new InvalidOperationException("Audio demand guard ABI changed.");
        }
    }
}
