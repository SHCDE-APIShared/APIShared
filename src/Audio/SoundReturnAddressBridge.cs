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
    // Copy its original return address before a managed transition, as argument five.
    internal sealed class SoundReturnAddressBridge : IDetourIntermediaryFactory
    {
        private readonly ulong callback;
        internal SoundReturnAddressBridge(Delegate value) { callback = unchecked((ulong)Marshal.GetFunctionPointerForDelegate(value).ToInt64()); }
        public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
        {
            if (context.Architecture != DetourArchitecture.X64 || context.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("Audio bridge requires the audited Win64 backend.");
            var entry = ExecutableFunction.Create(asm => {
                asm.sub(rsp, 0x38);
                asm.mov(rax, __qword_ptr[rsp + 0x38]);
                asm.mov(__qword_ptr[rsp + 0x20], rax);
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
            var bytes = new byte[40]; Marshal.Copy(hook.HookEntryPointAddress, bytes, 0, bytes.Length);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes));
            var a = decoder.Decode(); var b = decoder.Decode(); var c = decoder.Decode(); var d = decoder.Decode();
            var e = decoder.Decode(); var f = decoder.Decode(); var g = decoder.Decode();
            if (a.Mnemonic != Mnemonic.Sub || a.Op0Register != Register.RSP || a.Immediate8 != 0x38 ||
                b.Mnemonic != Mnemonic.Mov || b.Op0Register != Register.RAX || b.MemoryBase != Register.RSP || b.MemoryDisplacement64 != 0x38 ||
                c.Mnemonic != Mnemonic.Mov || c.MemoryBase != Register.RSP || c.MemoryDisplacement64 != 0x20 || c.Op1Register != Register.RAX ||
                d.Mnemonic != Mnemonic.Mov || d.Op0Register != Register.RAX || d.Immediate64 != callback ||
                e.Mnemonic != Mnemonic.Call || e.Op0Register != Register.RAX ||
                f.Mnemonic != Mnemonic.Add || f.Op0Register != Register.RSP || f.Immediate8 != 0x38 || g.Mnemonic != Mnemonic.Ret)
                throw new InvalidOperationException("Audio return-address bridge ABI changed.");
        }
    }
}
