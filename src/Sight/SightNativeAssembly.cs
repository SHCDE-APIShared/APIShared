using System;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Sight
{
    // Pure generator of the building-height dispatch override (see SightBlockingBuildingsNative). Executed by
    // tests/WorkerProductionBonus.NativeTests/SightDispatchTests.cs inside Vanilla's own sight routines.
    internal static class SightNativeAssembly
    {
        // Building types 7 through 78 pass the helper's type switch (lea eax,[r11-7]; cmp eax,0x47; ja).
        internal const int FirstDispatchType = 7, DispatchTypeCount = 0x48;
        // Table byte of a type no registration changes; any other value is the dispatch case to use.
        internal const byte VanillaCase = 0xFF;
        // Case 3 of the dispatch target table: the ordinary fixed building height (0x6BA41).
        internal const byte OrdinaryHeightCase = 3;

        // Reader span 0x6BA0C (17 bytes) in the obstacle-height helper 0x6B990:
        //   movzx eax, byte ptr [r8+rax+0x6BAC4]   ; dispatch case of the building type, RAX = type - 7 (0..0x47)
        //   mov ecx, dword ptr [r8+rax*4+0x6BAB4]  ; case target, followed by add rcx,r8; jmp rcx
        // R8 is the image base (lea at 0x6B9F1). RCX is written by the second load before any read, so it is free
        // before it; the flags are dead (add rcx,r8 rewrites them before the jump). RBX, RDI, RSI, R9 (the building
        // contribution, still 0), R10 (building records) and R11 (type) stay untouched. A type whose table byte is
        // VanillaCase runs both original loads; any other byte replaces only the first load's result.
        internal static void EmitDispatchOverride(Assembler a, ReadOnlySpan<Instruction> original, ulong overrides)
        {
            if (original.Length != 2 ||
                original[0].Mnemonic != Mnemonic.Movzx || original[0].Op0Register != Register.EAX ||
                original[1].Mnemonic != Mnemonic.Mov || original[1].Op0Register != Register.ECX)
                throw new InvalidOperationException("The building-height dispatch reader must be the two audited table loads.");

            Label vanilla = a.CreateLabel("sightDispatchVanilla");
            Label load = a.CreateLabel("sightDispatchLoad");
            a.mov(rcx, overrides);
            a.movzx(ecx, __byte_ptr[rcx + rax]);
            a.cmp(ecx, VanillaCase);
            a.je(vanilla);
            a.mov(eax, ecx);
            a.jmp(load);
            a.Label(ref vanilla);
            a.AddInstruction(original[0]);
            a.Label(ref load);
            a.AddInstruction(original[1]);
        }
    }
}
