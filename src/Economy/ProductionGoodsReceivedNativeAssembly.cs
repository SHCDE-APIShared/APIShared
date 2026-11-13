using System;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Economy
{
    internal static class ProductionGoodsReceivedNativeAssembly
    {
        // A tail relay preserves all seven Win64 arguments, the caller's stack/shadow
        // space and the return address. Only volatile R11 and flags are used, just as by
        // an ordinary called function. Dormant delivery never crosses into managed code.
        internal static void Relay(Assembler a, ulong demand, ulong callback, ulong original)
        {
            if (demand==0 || callback==0 || original==0) throw new ArgumentException("Incomplete delivery relay.");
            var vanilla=a.CreateLabel();
            a.mov(r11,demand); a.cmp(__dword_ptr[r11],0); a.je(vanilla);
            a.AddUnrestrictedJmp(callback);
            a.Label(ref vanilla); a.AddUnrestrictedJmp(original);
        }
        internal static void Source(Assembler a, ReadOnlySpan<Instruction> original, ulong back, ulong image, ulong adapter)
        {
            if(original.Length==0 || original[original.Length-1].Code!=Code.Call_rel32_64 ||
                original[original.Length-1].NearBranchTarget!=image+WeaponProductionNativeDefinition.AddGoodRva ||
                original[original.Length-1].NextIP!=back || adapter==0)
                throw new InvalidOperationException("Production receipt source CALL differs.");
            WorkerProductionBonusNativeAssembly.Replay(a,original.Slice(0,original.Length-1));
            a.AddUnrestrictedCall(adapter);
            a.AddUnrestrictedJmp(back);
        }
        internal static void ReplayDeposit(Assembler a, ReadOnlySpan<Instruction> original, ulong image, ulong adapter)
        {
            foreach(var instruction in original)
                if(adapter!=0 && instruction.Code==Code.Call_rel32_64 && instruction.NearBranchTarget==image+WeaponProductionNativeDefinition.AddGoodRva)
                    a.AddUnrestrictedCall(adapter);
                else a.AddInstruction(instruction);
        }
    }
}
