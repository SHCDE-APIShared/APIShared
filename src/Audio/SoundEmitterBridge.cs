using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Audio
{
    // The spatial emitter needs no extra arguments: after the demand guard, the active path
    // tail-jumps into the rooted managed thunk with the original frame, including stack argument five.
    internal sealed class SoundEmitterBridge : IDetourIntermediaryFactory
    {
        private readonly ulong callback, demand;
        internal SoundEmitterBridge(Delegate value, IntPtr demand)
        {
            callback = unchecked((ulong)Marshal.GetFunctionPointerForDelegate(value).ToInt64());
            this.demand = unchecked((ulong)demand.ToInt64());
            if (demand == IntPtr.Zero) throw new ArgumentException("Audio bridge requires a demand byte.", nameof(demand));
        }
        public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
        {
            if (context.Architecture != DetourArchitecture.X64 || context.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("Audio emitter bridge requires the audited Win64 backend.");
            ulong trampoline = unchecked((ulong)context.TrampolineAddress.ToInt64());
            var entry = ExecutableFunction.Create(asm => {
                var active = SoundBridgeCode.EmitGuard(asm, demand, trampoline);
                asm.Label(ref active);
                asm.mov(rax, callback);
                asm.jmp(rax);
            });
            return new DetourIntermediaryStubs(entry.EntryPoint, context.TrampolineAddress, entry);
        }
        internal void Validate(NativeDetour<SoundRuntime.Emit> hook)
        {
            if (hook.OriginalEntryPointAddress != hook.TrampolineAddress || hook.HookEntryPointAddress == new IntPtr(unchecked((long)callback)))
                throw new InvalidOperationException("Audio emitter intermediary/trampoline identity changed.");
            var decoder = SoundBridgeCode.Open(hook.HookEntryPointAddress);
            SoundBridgeCode.ValidateGuard(decoder, demand, unchecked((ulong)hook.TrampolineAddress.ToInt64()));
            var a = decoder.Decode(); var b = decoder.Decode();
            if (a.Mnemonic != Mnemonic.Mov || a.Op0Register != Register.RAX || a.Immediate64 != callback ||
                b.Mnemonic != Mnemonic.Jmp || b.Op0Register != Register.RAX)
                throw new InvalidOperationException("Audio emitter bridge ABI changed.");
        }
    }
}
