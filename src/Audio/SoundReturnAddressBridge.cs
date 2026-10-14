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
    // Dormant: the demand guard tail-jumps to the trampoline. Active: copy the original return
    // address, the entry RSP and the caller's RBP as arguments five to seven, then transition.
    internal sealed class SoundReturnAddressBridge : IDetourIntermediaryFactory
    {
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
                asm.mov(rax, __qword_ptr[rsp + 0x38]);
                asm.mov(__qword_ptr[rsp + 0x20], rax);
                asm.lea(rax, __qword_ptr[rsp + 0x38]);
                asm.mov(__qword_ptr[rsp + 0x28], rax);
                asm.mov(__qword_ptr[rsp + 0x30], rbp);
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
            var a = decoder.Decode(); var b = decoder.Decode(); var c = decoder.Decode(); var d = decoder.Decode(); var e = decoder.Decode();
            var f = decoder.Decode(); var g = decoder.Decode(); var h = decoder.Decode(); var i = decoder.Decode(); var j = decoder.Decode();
            if (a.Mnemonic != Mnemonic.Sub || a.Op0Register != Register.RSP || a.Immediate8 != 0x38 ||
                b.Mnemonic != Mnemonic.Mov || b.Op0Register != Register.RAX || b.MemoryBase != Register.RSP || b.MemoryDisplacement64 != 0x38 ||
                c.Mnemonic != Mnemonic.Mov || c.MemoryBase != Register.RSP || c.MemoryDisplacement64 != 0x20 || c.Op1Register != Register.RAX ||
                d.Mnemonic != Mnemonic.Lea || d.Op0Register != Register.RAX || d.MemoryBase != Register.RSP || d.MemoryDisplacement64 != 0x38 ||
                e.Mnemonic != Mnemonic.Mov || e.MemoryBase != Register.RSP || e.MemoryDisplacement64 != 0x28 || e.Op1Register != Register.RAX ||
                f.Mnemonic != Mnemonic.Mov || f.MemoryBase != Register.RSP || f.MemoryDisplacement64 != 0x30 || f.Op1Register != Register.RBP ||
                g.Mnemonic != Mnemonic.Mov || g.Op0Register != Register.RAX || g.Immediate64 != callback ||
                h.Mnemonic != Mnemonic.Call || h.Op0Register != Register.RAX ||
                i.Mnemonic != Mnemonic.Add || i.Op0Register != Register.RSP || i.Immediate8 != 0x38 || j.Mnemonic != Mnemonic.Ret)
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
