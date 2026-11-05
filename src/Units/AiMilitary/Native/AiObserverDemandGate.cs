using System;
using System.Runtime.InteropServices;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.X64.Assembly;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Units.AiMilitary.Native
{
    // Signature-independent tail dispatch: preserves flags, all argument registers and the stack.
    // No managed transition while demand is zero. ExecutableFunction is retained by the native hook.
    internal sealed class AiObserverDemandGate : IDetourIntermediaryFactory
    {
        private readonly IntPtr demand;
        private readonly ulong callback;
        internal AiObserverDemandGate(IntPtr demand, Delegate callback)
        { this.demand = demand; this.callback = unchecked((ulong)Marshal.GetFunctionPointerForDelegate(callback).ToInt64()); }
        public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
        {
            if (context.Architecture != DetourArchitecture.X64 || context.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("AI observer demand gate requires the validated x64 trampoline.");
            ulong original = unchecked((ulong)context.TrampolineAddress.ToInt64());
            var entry = ExecutableFunction.Create(a => Generate(a, unchecked((ulong)demand.ToInt64()), original, callback));
            return new DetourIntermediaryStubs(entry.EntryPoint, context.TrampolineAddress, entry);
        }
        internal static void Generate(Assembler a, ulong demand, ulong original, ulong callback)
        {
            var dormant = a.CreateLabel();
            a.pushfq(); a.push(rax); a.mov(rax, demand); a.cmp(__dword_ptr[rax], 0); a.je(dormant);
            a.pop(rax); a.popfq(); a.AddUnrestrictedJmp(callback);
            a.Label(ref dormant); a.pop(rax); a.popfq(); a.AddUnrestrictedJmp(original);
        }
    }
}
